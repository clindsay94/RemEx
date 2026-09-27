package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineNotifyKinds
import com.clindsay94.remex.routines.model.RoutineNotifyPayload
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunReportPayload
import com.clindsay94.remex.routines.model.RoutineRunStep
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Routines spec §13.3 `QueuedMessagePresenterTest` (R-UX-33): held versus live, expired, de-duplication. */
class QueuedMessagePresenterTest {
    private val now = 1_790_000_000_000L

    private fun notify(queuedAgoMs: Long, expiresInMs: Long = 3_600_000L - queuedAgoMs) =
        RoutineNotifyPayload(notifyId = uuid(), kind = RoutineNotifyKinds.STEP, queuedAtUnixMs = now - queuedAgoMs, expiresAtUnixMs = now + expiresInMs)

    @Test
    fun `a message that arrives within a minute of being sent is live`() {
        assertFalse(QueuedMessagePresenter.wasQueued(notify(queuedAgoMs = 2_000), now))
    }

    @Test
    fun `a message the PC held is presented as sent earlier`() {
        assertTrue(QueuedMessagePresenter.wasQueued(notify(queuedAgoMs = 20 * 60_000), now))
    }

    @Test
    fun `an expired message is not shown`() {
        assertTrue(QueuedMessagePresenter.isExpired(notify(queuedAgoMs = 3_700_000, expiresInMs = -100_000), now))
        assertFalse(QueuedMessagePresenter.isExpired(notify(queuedAgoMs = 60_000), now))
    }

    @Test
    fun `dedup remembers the last 200 ids`() {
        val dedup = RoutineNotifyDedup()
        assertTrue(dedup.firstTime("a"))
        assertFalse(dedup.firstTime("a"))
        repeat(RoutineNotifyDedup.CAPACITY) { dedup.firstTime("id$it") }
        // "a" has been pushed out of the window.
        assertTrue(dedup.firstTime("a"))
    }
}

/**
 * Routines spec §13.3 `CountdownMirrorNotificationTest` (R-UX-34, the PC-run half) and the phone side of
 * `routine_notify` (§7.3.5): posting, acknowledging, and the countdown mirror whose Cancel sends
 * `routine_cancel`.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class RoutineNotifyInboxTest {
    private class Sink : RoutineMessageSink {
        val posted = mutableListOf<Pair<RoutineNotifyPayload, Boolean>>()

        override fun postPcMessage(notify: RoutineNotifyPayload, queued: Boolean): Boolean {
            posted += notify to queued
            return true
        }
    }

    private class Rig(scope: TestScope) {
        val h = RepositoryHarness()
        val repo = h.repository()
        val link = FakeHostLink()
        val sink = Sink()
        val client =
            RoutineSyncClient(repo, link, MutableStateFlow<Long?>(null), h.observer, h.clock, ioDispatcher = StandardTestDispatcher(scope.testScheduler), messages = sink)

        fun acks(): List<String> =
            link.sent.filter { it.getString("type") == "routine_notify_ack" }
                .flatMap { json -> json.getJSONObject("routineNotifyAck").getJSONArray("notifyIds").let { a -> List(a.length()) { a.getString(it) } } }
    }

    private fun step(rig: Rig, queuedAgoMs: Long = 0, id: String = uuid()) =
        RoutineNotifyPayload(
            notifyId = id, kind = RoutineNotifyKinds.STEP, routineId = uuid(), runId = uuid(), title = "GPU running hot", body = "Your GPU is hot.",
            queuedAtUnixMs = rig.h.clock.now - queuedAgoMs, expiresAtUnixMs = rig.h.clock.now - queuedAgoMs + 3_600_000,
        )

    @Test
    fun `a message is posted and acknowledged`() =
        runTest {
            val rig = Rig(this)
            val message = step(rig)
            rig.client.onNotify(message)
            runCurrent()

            assertEquals(listOf(message to false), rig.sink.posted)
            assertEquals(listOf(message.notifyId), rig.acks())
        }

    @Test
    fun `a held message is posted as sent earlier`() =
        runTest {
            val rig = Rig(this)
            rig.client.onNotify(step(rig, queuedAgoMs = 30 * 60_000))
            runCurrent()

            assertTrue(rig.sink.posted.single().second)
        }

    @Test
    fun `a resend is shown once but acknowledged again`() =
        runTest {
            val rig = Rig(this)
            val message = step(rig)
            rig.client.onNotify(message)
            rig.client.onNotify(message)
            runCurrent()

            assertEquals(1, rig.sink.posted.size)
            assertEquals(listOf(message.notifyId, message.notifyId), rig.acks())
        }

    @Test
    fun `an expired message is acknowledged but not shown`() =
        runTest {
            val rig = Rig(this)
            rig.client.onNotify(step(rig, queuedAgoMs = 2 * 3_600_000))
            runCurrent()

            assertTrue(rig.sink.posted.isEmpty())
            assertEquals(1, rig.acks().size)
        }

    @Test
    fun `a countdown mirrors on the PC run's progress notification and is never acknowledged`() =
        runTest {
            val rig = Rig(this)
            val routine = pcIdleRoutine()
            val id = checkNotNull((rig.repo.save(routine) as RoutineSaveResult.Saved).routine.id)
            val runId = uuid()
            val live =
                RoutineRun(
                    runId = runId, routineId = id, hostIdentity = HOST, origin = "pc", outcome = "running",
                    triggeredAtUnixMs = rig.h.clock.now, startedAtUnixMs = rig.h.clock.now,
                    steps = listOf(RoutineRunStep(0, "power", "running")),
                )
            val report = RoutineRunReportPayload(runs = listOf(live), live = true)
            rig.client.onRunReport(report, rig.repo.applyRunReport(report))

            rig.client.onNotify(
                RoutineNotifyPayload(
                    notifyId = uuid(), kind = RoutineNotifyKinds.COUNTDOWN, routineId = id, runId = runId, title = "Bedtime", body = "x",
                    countdownEndsAtUnixMs = rig.h.clock.now + 15_000, queuedAtUnixMs = rig.h.clock.now, expiresAtUnixMs = rig.h.clock.now + 15_000,
                ),
            )
            runCurrent()

            assertEquals(listOf(0), rig.h.observer.countdowns)
            assertTrue(rig.acks().isEmpty())
            assertTrue(rig.sink.posted.isEmpty())
        }

    @Test
    fun `the countdown's Cancel sends routine_cancel for that PC run`() =
        runTest {
            val rig = Rig(this)
            val id = checkNotNull((rig.repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)
            val runId = uuid()
            val live =
                RoutineRun(
                    runId = runId, routineId = id, hostIdentity = HOST, origin = "pc", outcome = "running",
                    triggeredAtUnixMs = rig.h.clock.now, startedAtUnixMs = rig.h.clock.now,
                    steps = listOf(RoutineRunStep(0, "power", "running")),
                )
            val report = RoutineRunReportPayload(runs = listOf(live), live = true)
            rig.client.onRunReport(report, rig.repo.applyRunReport(report))

            assertTrue(rig.client.cancelPcRun(runId))
            runCurrent()

            val cancel = rig.link.sent.single { it.getString("type") == "routine_cancel" }.getJSONObject("routineCancel")
            assertEquals(runId, cancel.getString("runId"))
        }

    @Test
    fun `a countdown past its end is not shown`() =
        runTest {
            val rig = Rig(this)
            rig.client.onNotify(
                RoutineNotifyPayload(
                    notifyId = uuid(), kind = RoutineNotifyKinds.COUNTDOWN, routineId = uuid(), runId = uuid(),
                    countdownEndsAtUnixMs = rig.h.clock.now - 1, queuedAtUnixMs = rig.h.clock.now - 20_000,
                ),
            )
            runCurrent()

            assertTrue(rig.h.observer.countdowns.isEmpty())
            assertTrue(rig.sink.posted.isEmpty())
        }

}
