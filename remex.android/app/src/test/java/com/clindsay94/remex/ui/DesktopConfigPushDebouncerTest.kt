package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.DesktopConfigPushDebouncer
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Covers the `desktop_config` debounce for the remote-desktop quality/fps/scale sliders (perf P1-2).
 *
 * Every `desktop_config` makes the host rebuild its encoder, and the sliders call in on every drag
 * tick. The contract: a drag sends its first tick and its settled value, nothing in between; the
 * settled value is always the one the user let go of; an isolated change is not delayed.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class DesktopConfigPushDebouncerTest {

    private companion object {
        const val QUIET = 200L
    }

    /** Simulates the slider value the ViewModel's send reads at send time, and records each send. */
    private class Harness(scope: TestScope) {
        var current = 0
        val sent = mutableListOf<Pair<Long, Int>>()
        val debouncer =
                DesktopConfigPushDebouncer(
                        scope = scope,
                        nowMs = { scope.testScheduler.currentTime },
                        quietMs = QUIET,
                        send = { sent += scope.testScheduler.currentTime to current }
                )

        fun change(value: Int) {
            current = value
            debouncer.onChange()
        }
    }

    @Test
    fun `an isolated change is sent immediately`() = runTest {
        val h = Harness(this)
        advanceTimeBy(1_000L)

        h.change(80)

        assertEquals(listOf(1_000L to 80), h.sent)
    }

    @Test
    fun `a continuous drag sends its first tick and its settled value only`() = runTest {
        // The perf premise stated as a count: 60 ticks at ~60Hz used to be 60 encoder rebuilds.
        val h = Harness(this)
        advanceTimeBy(1_000L)

        repeat(60) { i ->
            h.change(40 + i)
            advanceTimeBy(16L)
        }
        advanceUntilIdle()

        assertEquals("one leading + one trailing send, not one per tick", 2, h.sent.size)
        assertEquals("leading send carries the first tick", 40, h.sent[0].second)
        assertEquals("trailing send carries the value the user let go of", 99, h.sent[1].second)
    }

    @Test
    fun `nothing is sent mid-drag`() = runTest {
        val h = Harness(this)
        h.change(10) // leading send

        repeat(30) { i ->
            advanceTimeBy(QUIET - 1) // always just inside the quiet window
            h.change(11 + i)
        }
        runCurrent()

        assertEquals("only the leading send while ticks keep arriving", 1, h.sent.size)

        advanceUntilIdle()
        assertEquals(2, h.sent.size)
        assertEquals(40, h.sent.last().second)
    }

    @Test
    fun `the trailing send fires one quiet interval after the last tick`() = runTest {
        val h = Harness(this)
        advanceTimeBy(1_000L)
        h.change(1)
        advanceTimeBy(50L)
        h.change(2) // deferred, last change at t=1050

        advanceTimeBy(QUIET - 1)
        runCurrent()
        assertEquals("not before the quiet interval has elapsed", 1, h.sent.size)

        advanceTimeBy(1L)
        runCurrent()
        assertEquals(listOf(1_000L to 1, 1_250L to 2), h.sent)
    }

    @Test
    fun `slow deliberate steps each send promptly`() = runTest {
        val h = Harness(this)
        advanceTimeBy(1_000L)

        repeat(4) { i ->
            h.change(i)
            runCurrent()
            assertEquals("step $i must go out without waiting", i + 1, h.sent.size)
            advanceTimeBy(QUIET + 50L)
        }
        advanceUntilIdle()

        assertEquals("no trailing duplicates after isolated steps", 4, h.sent.size)
    }

    @Test
    fun `a new drag after the previous one settled starts with an immediate send`() = runTest {
        val h = Harness(this)
        h.change(1)
        advanceTimeBy(10L)
        h.change(2)
        advanceUntilIdle() // trailing send of 2

        advanceTimeBy(500L)
        h.change(3)

        assertEquals(listOf(1, 2, 3), h.sent.map { it.second })
    }

    @Test
    fun `resuming a drag right after the trailing send still waits out a full quiet interval`() = runTest {
        // A change-only quiet check would send this tick at once: it is far from the PREVIOUS change,
        // but only milliseconds after the trailing send it caused, so the host would rebuild twice in
        // a few ms - exactly the per-tick flood this debounce exists to prevent.
        val h = Harness(this)
        h.change(1) // leading send at t=0
        advanceTimeBy(50L)
        h.change(2) // deferred, last change at t=50
        advanceTimeBy(QUIET.toLong())
        runCurrent() // trailing send of 2 fires at t=250

        assertEquals(listOf(1, 2), h.sent.map { it.second })

        advanceTimeBy(5L)
        h.change(3) // resumes 5ms after the trailing send

        assertEquals("must not send immediately so soon after the last send", 2, h.sent.size)

        advanceUntilIdle()
        assertEquals(listOf(1, 2, 3), h.sent.map { it.second })
    }
}
