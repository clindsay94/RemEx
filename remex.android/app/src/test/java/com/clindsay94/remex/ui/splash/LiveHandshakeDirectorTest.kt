package com.clindsay94.remex.ui.splash

import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import kotlin.math.abs

/**
 * The Live Handshake director against the shared vectors (RemEx-8g6n0). The C# director reads the
 * same file, so the two platforms cannot drift on when the splash hands off or where it opens from.
 * Steps `now` from 0 in 1 ms increments, exactly as the spec prescribes.
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

    private fun JSONObject.optDouble(name: String): Double? = if (isNull(name)) null else getDouble(name)

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
    }

    @Test
    fun `every shared vector hands off at the expected time, from the expected origin`() {
        val cases = vectors.getJSONArray("cases")
        assertTrue("the vectors file has cases", cases.length() > 0)
        for (i in 0 until cases.length()) {
            val case = cases.getJSONObject(i)
            val name = case.getString("name")
            val inputs = DirectorInputs(
                peers = case.getInt("peers"),
                hasTarget = case.getBoolean("hasTarget"),
                readyAt = case.optDouble("readyAt"),
                linkedAt = case.optDouble("linkedAt"),
                failedAt = case.optDouble("failedAt"),
                skipAt = case.optDouble("skipAt"),
            )
            val director = LiveHandshakeDirector(exitFromMark = case.getBoolean("exitFromMark"))
            var ms = 0
            while (!director.step(ms / 1000.0, inputs)) {
                ms++
                check(ms < 10_000) { "$name: never handed off" }
            }
            val handoff = assertNotNull_(director.handoffAt, name)
            val expected = case.getDouble("expectHandoff")
            assertTrue(
                "$name: handed off at $handoff, expected $expected",
                abs(handoff - expected) <= 0.002,
            )
            val expectOrigin = when (case.getString("expectOrigin")) {
                "target" -> ExitOrigin.Target
                "mark" -> ExitOrigin.Mark
                else -> error("$name: unknown origin")
            }
            assertEquals("$name: origin", expectOrigin, director.origin)

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
        val early = DirectorInputs(peers = 1, hasTarget = true, readyAt = 0.2)
        var ms = 0
        while (!director.step(ms / 1000.0, early)) ms++
        val fixed = director.handoffAt
        // A link arriving after the hand-off must neither move it nor change the origin.
        director.step(3.0, early.copy(linkedAt = 2.9, skipAt = 2.95))
        assertEquals(fixed, director.handoffAt)
        assertEquals(ExitOrigin.Mark, director.origin)
    }

    @Test
    fun `last pulse follows the schedule and stops at the hand-off`() {
        assertEquals(null, LiveHandshakeDirector.lastPulse(null, 0.2, false))
        assertEquals(0.32, LiveHandshakeDirector.lastPulse(null, 0.5, false)!!, 1e-9)
        assertEquals(1.32, LiveHandshakeDirector.lastPulse(null, 1.5, false)!!, 1e-9)
        assertEquals(0.32, LiveHandshakeDirector.lastPulse(1.1, 2.0, false)!!, 1e-9)
        assertEquals(null, LiveHandshakeDirector.lastPulse(null, 1.5, true))
    }

    @Test
    fun `status line follows the evidence`() {
        val m = LiveHandshakeStatusModel
        assertEquals(HandshakeStatus.Starting, m.statusAt(0.1, 0, 0, null, false, null, null))
        assertEquals(HandshakeStatus.NonePaired, m.statusAt(0.5, 0, 0, null, false, null, null))
        assertEquals(HandshakeStatus.Starting, m.statusAt(0.1, 3, 0, "A", false, null, null))
        assertEquals(HandshakeStatus.Pinging(3), m.statusAt(0.4, 3, 0, "A", false, null, null))
        assertEquals(HandshakeStatus.Awake(2, 3), m.statusAt(0.7, 3, 2, "A", false, null, null))
        assertEquals(HandshakeStatus.Linked("A", 9), m.statusAt(1.1, 3, 2, "A", true, 9, null))
        assertEquals(HandshakeStatus.NotAnswering("A"), m.statusAt(1.3, 3, 1, "A", false, null, 1.2))
        assertEquals(HandshakeStatus.Awake(1, 3), m.statusAt(1.1, 3, 1, "A", false, null, 1.2))
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

    private fun assertNotNull_(value: Double?, name: String): Double {
        assertNotNull("$name: hand-off time", value)
        return value!!
    }
}
