package com.clindsay94.remex.ui.splash

import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import kotlin.math.abs

/**
 * The Live Handshake director against the shared vectors (RemEx-8g6n0). The C# director reads the
 * same file, so the two platforms cannot drift on when the splash hands off, where it opens from,
 * or when each answer and the lock are shown. Steps `now` from 0 in 1 ms increments, exactly as the
 * spec prescribes.
 */
class LiveHandshakeDirectorTest {

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    private val vectors: JSONObject by lazy {
        val file = File(repoRoot(), "docs/specs/live-handshake-director-vectors.json")
        JSONObject(file.readText(Charsets.UTF_8))
    }

    private fun JSONObject.optTime(name: String): Double? = if (!has(name) || isNull(name)) null else getDouble(name)

    private fun inputsOf(case: JSONObject): DirectorInputs {
        val targetId = if (case.getBoolean("hasTarget")) "target" else null
        val answers = case.getJSONArray("answers").let { a ->
            List(a.length()) { i ->
                val o = a.getJSONObject(i)
                val isTarget = o.optBoolean("target", false)
                DirectorAnswer(if (isTarget) "target" else "peer$i", o.getDouble("at"))
            }
        }
        return DirectorInputs(
            peers = case.getInt("peers"),
            targetId = targetId,
            answers = answers,
            readyAt = case.optTime("readyAt"),
            linkedAt = case.optTime("linkedAt"),
            failedAt = case.optTime("failedAt"),
            skipAt = case.optTime("skipAt"),
        )
    }

    private fun run(case: JSONObject): LiveHandshakeDirector {
        val inputs = inputsOf(case)
        val director = LiveHandshakeDirector(exitFromMark = case.getBoolean("exitFromMark"))
        var ms = 0
        while (!director.step(ms / 1000.0, inputs)) {
            ms++
            check(ms < 10_000) { "${case.getString("name")}: never handed off" }
        }
        return director
    }

    @Test
    fun `constants match the shared vectors`() {
        val c = vectors.getJSONObject("constants")
        assertEquals(c.getDouble("floor"), LiveHandshakeTiming.FLOOR, 0.0)
        assertEquals(c.getDouble("grace"), LiveHandshakeTiming.GRACE, 0.0)
        assertEquals(c.getDouble("cap"), LiveHandshakeTiming.CAP, 0.0)
        assertEquals(c.getDouble("lockHold"), LiveHandshakeTiming.LOCK_HOLD, 0.0)
        assertEquals(c.getDouble("exit"), LiveHandshakeTiming.EXIT, 0.0)
        assertEquals(c.getDouble("fadeExit"), LiveHandshakeTiming.FADE_EXIT, 0.0)
        assertEquals(c.getDouble("firstPulse"), LiveHandshakeTiming.FIRST_PULSE, 0.0)
        assertEquals(c.getDouble("pulsePeriod"), LiveHandshakeTiming.PULSE_PERIOD, 0.0)
        assertEquals(c.getDouble("answerMin"), LiveHandshakeTiming.ANSWER_MIN, 0.0)
        assertEquals(c.getDouble("answerGap"), LiveHandshakeTiming.ANSWER_GAP, 0.0)
        assertEquals(c.getDouble("lockAfter"), LiveHandshakeTiming.LOCK_AFTER, 0.0)
        assertEquals(c.getDouble("lineGap"), LiveHandshakeTiming.LINE_GAP, 0.0)
    }

    @Test
    fun `every shared vector hands off, stages and opens as expected`() {
        val cases = vectors.getJSONArray("cases")
        assertTrue("the vectors file has cases", cases.length() > 0)
        for (i in 0 until cases.length()) {
            val case = cases.getJSONObject(i)
            val name = case.getString("name")
            val director = run(case)
            val handoff = director.handoffAt
            assertNotNull("$name: hand-off time", handoff)
            val expected = case.getDouble("expectHandoff")
            assertTrue("$name: handed off at $handoff, expected $expected", abs(handoff!! - expected) <= 0.002)

            val expectOrigin = when (case.getString("expectOrigin")) {
                "target" -> ExitOrigin.Target
                "mark" -> ExitOrigin.Mark
                else -> error("$name: unknown origin")
            }
            assertEquals("$name: origin", expectOrigin, director.origin)

            val expectLock = case.optTime("expectLockShown")
            if (expectLock == null) {
                assertNull("$name: no lock shown", director.lockShown)
            } else {
                val lock = director.lockShown
                assertNotNull("$name: lock shown", lock)
                assertTrue("$name: lock shown at $lock, expected $expectLock", abs(lock!! - expectLock) <= 0.002)
            }

            val expectShown = case.getJSONArray("expectAnswersShown").let { a -> DoubleArray(a.length()) { a.getDouble(it) } }
            assertArrayEquals(
                "$name: answers shown",
                expectShown,
                director.staging.shownTimes().toDoubleArray(),
                0.002,
            )

            val expectPulses = case.getJSONArray("expectPulses").let { a -> DoubleArray(a.length()) { a.getDouble(it) } }
            val pulses = LiveHandshakeDirector.pulses(handoff, handoff, reducedMotion = false).toDoubleArray()
            assertArrayEquals("$name: pulses", expectPulses, pulses, 0.001)
            assertTrue(
                "$name: no pulses under reduced motion",
                LiveHandshakeDirector.pulses(handoff, handoff, reducedMotion = true).isEmpty(),
            )
        }
    }

    @Test
    fun `later events never move a hand-off that has started`() {
        val director = LiveHandshakeDirector()
        val early = DirectorInputs(peers = 1, targetId = "t", readyAt = 0.2)
        var ms = 0
        while (!director.step(ms / 1000.0, early)) ms++
        val fixed = director.handoffAt
        // A link arriving after the hand-off must neither move it nor change the origin.
        director.step(3.0, early.copy(linkedAt = 2.9, skipAt = 2.95))
        assertEquals(fixed, director.handoffAt)
        assertEquals(ExitOrigin.Mark, director.origin)
        assertNull(director.lockShown)
    }

    @Test
    fun `last pulse follows the schedule and stops at the hand-off`() {
        assertEquals(null, LiveHandshakeDirector.lastPulse(null, 0.2, false))
        assertEquals(0.32, LiveHandshakeDirector.lastPulse(null, 0.5, false)!!, 1e-9)
        assertEquals(1.32, LiveHandshakeDirector.lastPulse(null, 1.5, false)!!, 1e-9)
        assertEquals(0.32, LiveHandshakeDirector.lastPulse(1.1, 2.0, false)!!, 1e-9)
        assertEquals(null, LiveHandshakeDirector.lastPulse(null, 1.5, true))
    }

    /** Steps a director and a console together, the way the splash's frame loop does. */
    private fun consoleFor(
        inputs: DirectorInputs,
        rtt: Map<String, Long>,
        silentAt: Double? = null,
        until: Double = 3.0,
    ): List<ConsoleLine> {
        val director = LiveHandshakeDirector()
        val console = LiveHandshakeConsole()
        val rttFor: (String) -> Long? = { rtt[it] }
        var ms = 0
        while (ms / 1000.0 <= until) {
            val now = ms / 1000.0
            director.step(now, inputs)
            val silent = silentAt ?: director.handoffAt?.takeIf { director.lockShown == null }
            console.update(now, director, inputs, rttFor, silent)
            ms++
        }
        return console.lines
    }

    @Test
    fun `the console narrates the fast link one readable line at a time`() {
        // Connor's case (the lab's solo scenario): everything real lands inside half a second.
        val lines = consoleFor(
            DirectorInputs(
                peers = 1, targetId = "desk", answers = listOf(DirectorAnswer("desk", 0.18)),
                readyAt = 0.35, linkedAt = 0.44,
            ),
            rtt = mapOf("desk" to 8L),
        )
        assertEquals(
            listOf(ConsoleLineKind.Pinging, ConsoleLineKind.Answered, ConsoleLineKind.Linked, ConsoleLineKind.Opening),
            lines.map { it.kind },
        )
        val at = lines.map { it.at }
        assertEquals(0.32, at[0], 0.002)
        assertEquals(0.64, at[1], 0.002) // due 0.62, pushed by LINE_GAP
        assertEquals(1.12, at[2], 0.002)
        assertEquals(1.90, at[3], 0.002) // the FLOOR (RemEx-pp4cm.11), past lock + hold at 1.82
        assertEquals(8L, lines[1].value)
        assertTrue(lines[2].hot && lines[3].hot && !lines[1].hot)
    }

    @Test
    fun `an awake PC mid-handshake is waited for and never called not answering`() {
        // The AVD case (RemEx-pp4cm.11): the probe hears the PC at 0.41 s, the app is ready at once,
        // and the host's ack lands at 1.95 s. Some evidence of silence (a connect attempt ending)
        // turns up at 0.6 s. The splash must hold for the lock and must not say the PC that just
        // answered is "not answering".
        val lines = consoleFor(
            DirectorInputs(
                peers = 1, targetId = "desk", answers = listOf(DirectorAnswer("desk", 0.41)),
                readyAt = 0.10, linkedAt = 1.95,
            ),
            rtt = mapOf("desk" to 414L),
            silentAt = 0.6,
        )
        assertEquals(
            listOf(ConsoleLineKind.Pinging, ConsoleLineKind.Answered, ConsoleLineKind.Linked, ConsoleLineKind.Opening),
            lines.map { it.kind },
        )
        assertEquals(1.95, lines[2].at, 0.002)
        assertEquals(2.65, lines[3].at, 0.002) // lock + LOCK_HOLD, not readyAt + GRACE
    }

    @Test
    fun `the console says so when the target is not answering, and nothing paired`() {
        val asleep = consoleFor(
            DirectorInputs(peers = 2, targetId = "desk", answers = listOf(DirectorAnswer("htpc", 0.70)), readyAt = 0.70),
            rtt = mapOf("htpc" to 31L),
            silentAt = 1.5,
        )
        assertEquals(
            listOf(ConsoleLineKind.Pinging, ConsoleLineKind.Answered, ConsoleLineKind.NotAnswering, ConsoleLineKind.Opening),
            asleep.map { it.kind },
        )
        // Learned at 1.5, after its 1.2 slot: it appears when learned, whole.
        assertEquals(1.5, asleep[2].at, 0.002)
        assertEquals(1.9, asleep[3].at, 0.002)

        val none = consoleFor(DirectorInputs(peers = 0, targetId = null, readyAt = 0.55), rtt = emptyMap())
        assertEquals(listOf(ConsoleLineKind.NonePaired, ConsoleLineKind.Opening), none.map { it.kind })
    }

    @Test
    fun `motion helpers match the lab`() {
        val m = LiveHandshakeMotion
        assertEquals(0.58, m.radiusFor(1.0), 1e-9)
        assertEquals(1.0, m.radiusFor(250.0), 1e-9)
        assertEquals(1.0, m.radiusFor(5000.0), 1e-9)
        assertEquals(0.0, m.spring(0.0), 0.0)
        assertTrue("spring overshoots", (0..60).map { m.spring(it / 100.0) }.max() > 1.1)
        assertEquals(1.0, m.spring(2.0), 1e-3)
        assertEquals(0.0, m.EmphasizedDecelerate(0.0), 0.0)
        assertEquals(1.0, m.EmphasizedDecelerate(1.0), 0.0)
        assertTrue(m.EmphasizedDecelerate(0.5) > 0.8)
        val h = m.hashStr("DESKTOP-RIG|MEDIA-HTPC|OFFICE-LAPTOP")
        assertTrue(h in 0.0..<1.0)
        assertEquals(h, m.hashStr("DESKTOP-RIG|MEDIA-HTPC|OFFICE-LAPTOP"), 0.0)
    }
}
