package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineRun
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** History retention, skip coalescing and start limits (spec §8.1, §8.7, §8.8, T9; RemEx-pp0rt.5). */
class RoutineRetentionTest {
    private val now = 1_790_000_000_000L
    private val day = 24L * 60 * 60 * 1000

    private fun run(routineId: String, ageMs: Long, outcome: String = "succeeded", reason: String? = "ok", origin: String = "phone") =
        RoutineRun(runId = uuid(), routineId = routineId, triggeredAtUnixMs = now - ageMs, outcome = outcome, reasonCode = reason, origin = origin)

    @Test
    fun `the last 50 runs of a routine are kept whatever their age, up to 90 days`() {
        val runs = (0 until 60).map { run("a", 40 * day + it) }
        val kept = RoutineRetention.apply(mapOf("a" to runs), now).getValue("a")
        assertEquals(50, kept.size)
        // The newest 50 survive.
        assertEquals(runs.take(50).map { it.runId }.toSet(), kept.map { it.runId }.toSet())
    }

    @Test
    fun `runs younger than 30 days are kept beyond 50`() {
        val runs = (0 until 70).map { run("a", it * 60_000L) }
        assertEquals(70, RoutineRetention.apply(mapOf("a" to runs), now).getValue("a").size)
    }

    @Test
    fun `nothing older than 90 days is kept, even within the last 50`() {
        val runs = listOf(run("a", day), run("a", 91 * day))
        assertEquals(1, RoutineRetention.apply(mapOf("a" to runs), now).getValue("a").size)
    }

    @Test
    fun `the whole history is capped at 1000, oldest first`() {
        val byRoutine = (0 until 30).associate { r -> "r$r" to (0 until 40).map { i -> run("r$r", (r * 40 + i) * 1000L) } }
        val kept = RoutineRetention.apply(byRoutine, now)
        assertEquals(1000, kept.values.sumOf { it.size })
        val oldestKept = kept.values.flatten().minOf { it.triggeredAtUnixMs }
        val newestDropped = byRoutine.values.flatten().filter { r -> kept.values.flatten().none { it.runId == r.runId } }.maxOf { it.triggeredAtUnixMs }
        assertTrue("dropped runs must all be older than kept ones", newestDropped <= oldestKept)
    }

    @Test
    fun `a run still marked running is never dropped`() {
        val runs = (0 until 60).map { run("a", 91 * day + it) } + run("a", 95 * day, outcome = "running", reason = null)
        val kept = RoutineRetention.apply(mapOf("a" to runs), now).getValue("a")
        assertEquals(listOf("running"), kept.map { it.outcome })
    }

    @Test
    fun `skips coalesce inside their window only`() {
        val skip = run("a", 30_000L, outcome = "skipped", reason = "already_running")
        assertTrue(RoutineSkipCoalescing.isCoalesced(listOf(skip), "already_running", now))
        assertFalse(RoutineSkipCoalescing.isCoalesced(listOf(skip.copy(triggeredAtUnixMs = now - 61_000L)), "already_running", now))
        assertFalse(RoutineSkipCoalescing.isCoalesced(listOf(skip), "cooldown", now))
        // Paused skips fold for an hour (§8.7).
        val paused = run("a", 50 * 60_000L, outcome = "skipped", reason = "paused_on_phone")
        assertTrue(RoutineSkipCoalescing.isCoalesced(listOf(paused), "paused_on_phone", now))
    }

    private fun check(routine: List<RoutineRun>, source: String, all: List<RoutineRun> = routine) =
        RoutineStartLimits.check(routine, all, source, now)

    @Test
    fun `a start within 60 s of the last is a cooldown, except for in-app Run and Test`() {
        val recent = listOf(run("a", 20_000L))
        assertEquals("cooldown", check(recent, "nfc.tap"))
        assertNull(check(recent, "manual.app"))
        assertNull(check(listOf(run("a", 61_000L)), "nfc.tap"))
        // Skips and PC runs are not starts.
        assertNull(check(listOf(run("a", 1_000L, outcome = "skipped")), "nfc.tap"))
        assertNull(check(listOf(run("a", 1_000L, origin = "pc")), "nfc.tap"))
    }

    @Test
    fun `thirty starts of one routine in an hour hold it back, but never in-app Run`() {
        val starts = (0 until 30).map { run("a", 61_000L + it * 60_000L) }
        assertEquals("rate_limited", check(starts, "manual.shortcut"))
        assertNull(check(starts.drop(1), "manual.shortcut"))
        assertNull(check(starts, "manual.app"))
    }

    @Test
    fun `thirty automatic starts across ALL routines in an hour hold every automatic start back (T9 owner cap)`() {
        // Thirty different routines, one automatic start each: no single routine is near its own cap.
        val others = (0 until 30).map { run("r$it", 61_000L + it * 60_000L).copy(source = "home.arrive") }
        assertEquals("rate_limited", check(emptyList(), "home.leave", all = others))
        // The owner cap counts automatic starts only, and holds back automatic starts only.
        assertNull(check(emptyList(), "home.leave", all = others.drop(1)))
        assertNull(check(emptyList(), "nfc.tap", all = others))
        assertNull(check(emptyList(), "home.leave", all = others.map { it.copy(source = "nfc.tap") }))
    }
}
