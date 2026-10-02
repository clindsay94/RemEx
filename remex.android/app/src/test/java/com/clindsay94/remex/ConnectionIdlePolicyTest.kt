package com.clindsay94.remex

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** RemEx-9yei0: the idle teardown of the background keepalive, as pure functions. */
class ConnectionIdlePolicyTest {

    private val window = ConnectionIdlePolicy.IDLE_TIMEOUT_MS

    @Test
    fun theWindowIsTenMinutes() {
        assertEquals(10 * 60_000L, window)
    }

    @Test
    fun backgroundAndIdleForTheWholeWindow_tearsDownAtOnce() {
        assertEquals(0L, ConnectionIdlePolicy.teardownDelayMs(false, false, false, quietMs = window))
        assertEquals(0L, ConnectionIdlePolicy.teardownDelayMs(false, false, false, quietMs = window * 3))
    }

    @Test
    fun backgroundAndIdleForPartOfTheWindow_waitsOutTheRest() {
        assertEquals(window - 90_000L, ConnectionIdlePolicy.teardownDelayMs(false, false, false, quietMs = 90_000L))
        assertEquals(window, ConnectionIdlePolicy.teardownDelayMs(false, false, false, quietMs = 0L))
        // A clock that steps backwards never shortens the wait below a full window.
        assertEquals(window, ConnectionIdlePolicy.teardownDelayMs(false, false, false, quietMs = -5L))
    }

    @Test
    fun foreground_neverTearsDown() {
        assertNull(ConnectionIdlePolicy.teardownDelayMs(true, false, false, quietMs = window * 10))
    }

    @Test
    fun aConnectionTheHeartbeatWouldRebuild_isNotTornDown() {
        // A hardware widget on a lit screen: tearing down only for the heartbeat to reconnect is churn.
        assertNull(ConnectionIdlePolicy.teardownDelayMs(false, true, false, quietMs = window * 10))
    }

    @Test
    fun anythingInFlight_holdsTheConnection() {
        assertNull(ConnectionIdlePolicy.teardownDelayMs(false, false, true, quietMs = window * 10))
    }

    @Test
    fun theFullTruthTable_tearsDownOnlyWhenBackgroundUnwantedAndIdle() {
        for (foreground in listOf(false, true)) for (wanted in listOf(false, true)) for (busy in listOf(false, true)) {
            val delay = ConnectionIdlePolicy.teardownDelayMs(foreground, wanted, busy, quietMs = window)
            val expected = !foreground && !wanted && !busy
            assertEquals("fg=$foreground wanted=$wanted busy=$busy", expected, delay == 0L)
        }
    }

    // ── ConnectionActivityTracker ──

    private class FakeClock(var now: Long = 1_000L) : () -> Long {
        override fun invoke() = now
    }

    @Test
    fun aHoldKeepsTheTrackerBusyUntilReleased_andTheReleaseRestartsTheCountdown() {
        val clock = FakeClock()
        val tracker = ConnectionActivityTracker(clock)
        tracker.hold(ConnectionActivity.routineRun("r1"))
        clock.now += window * 2
        assertTrue(tracker.busy)

        tracker.release(ConnectionActivity.routineRun("r1"))
        assertFalse(tracker.busy)
        assertEquals(0L, tracker.quietMs)
        clock.now += 60_000L
        assertEquals(60_000L, tracker.quietMs)
    }

    @Test
    fun twoRunsAtOnce_theFirstToFinishDoesNotFreeTheSecond() {
        val tracker = ConnectionActivityTracker(FakeClock())
        tracker.hold(ConnectionActivity.routineRun("a"))
        tracker.hold(ConnectionActivity.routineRun("b"))
        tracker.release(ConnectionActivity.routineRun("a"))
        assertTrue(tracker.busy)
        tracker.release(ConnectionActivity.routineRun("b"))
        assertFalse(tracker.busy)
    }

    @Test
    fun aDoubledReleaseOrARepeatedHold_cannotUnbalanceIt() {
        val tracker = ConnectionActivityTracker(FakeClock())
        tracker.release(ConnectionActivity.REMOTE_DESKTOP)
        assertFalse(tracker.busy)
        tracker.hold(ConnectionActivity.REMOTE_DESKTOP)
        tracker.hold(ConnectionActivity.REMOTE_DESKTOP)
        tracker.release(ConnectionActivity.REMOTE_DESKTOP)
        assertFalse(tracker.busy)
    }

    @Test
    fun aTouch_restartsTheCountdown() {
        val clock = FakeClock()
        val tracker = ConnectionActivityTracker(clock)
        clock.now += window
        assertEquals(window, tracker.quietMs)
        tracker.touch()
        assertEquals(0L, tracker.quietMs)
    }
}
