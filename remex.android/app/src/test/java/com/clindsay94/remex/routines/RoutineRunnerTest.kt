package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunStep
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/**
 * The phone runner end to end over the real repository and in-memory stores, on virtual time
 * (spec §8.1, §8.2, §8.6, §8.9; RemEx-pp0rt.5). WorkManager is not involved: the worker only unpacks
 * a ticket, and `work-testing` would need an Android context these JVM tests do not have.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class RoutineRunnerTest {

    private class Rig(scope: TestScope) {
        val h = RepositoryHarness(clock = FakeClock(elapsed = { scope.testScheduler.currentTime }))
        val repo = h.repository()
        val link = FakeHostLink()
        val phone = FakePhone()
        val runner = RoutineRunner(repo, link, phone, h.observer, h.clock, h.controls)

        suspend fun queue(routine: Routine, source: String = "manual.app", testRun: Boolean = false): RoutineRunTicket {
            val saved = (repo.save(routine) as RoutineSaveResult.Saved).routine
            val start = repo.run(checkNotNull(saved.id), source, testRun)
            check(start is RoutineRunStart.Started) { "run did not start: $start" }
            return h.scheduler.enqueued.last()
        }

        suspend fun run(routine: Routine, source: String = "manual.app", testRun: Boolean = false): RoutineRun =
            checkNotNull(runner.execute(queue(routine, source, testRun)))
    }

    private fun RoutineRun.statuses() = steps.orEmpty().map { it.status }

    @Test
    fun `phone steps run in order and every step is recorded`() =
        runTest {
            val rig = Rig(this)
            val run = rig.run(manualRoutine(wake(), waitOnline(), delaySeconds(5), notifyPhone()))
            assertEquals("succeeded", run.outcome)
            assertEquals("ok", run.reasonCode)
            assertEquals(listOf("succeeded", "succeeded", "succeeded", "succeeded"), run.statuses())
            assertEquals(1, rig.phone.wakeAttempts)
            assertEquals(listOf("PC ready" to "Steam is starting."), rig.phone.messages)
            assertEquals(listOf(null, 0, 1, 2, 3), rig.h.observer.progress)
            assertEquals(run, rig.repo.findRun(checkNotNull(run.runId)))
            assertTrue(rig.link.sent.isEmpty())
        }

    @Test
    fun `a host step is sent as a routine_step_request and succeeds on the PC's answer`() =
        runTest {
            val rig = Rig(this)
            val run = rig.run(manualRoutine(lock()))
            assertEquals("succeeded", run.outcome)
            val request = rig.link.requests().single()
            assertEquals(run.runId, request.getString("runId"))
            assertEquals(0, request.getInt("stepIndex"))
            assertEquals("manual", request.getString("triggerType"))
            assertEquals("manual.app", request.getString("source"))
            assertFalse(request.getBoolean("testRun"))
            assertEquals("LOCK", request.getJSONObject("step").getString("verb"))
        }

    @Test
    fun `a test run flags every host step and records the PC's simulation`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = { FakeHostLink.result(it, "simulated", reason = "simulated") }
            val run = rig.run(manualRoutine(shutdown()), testRun = true)
            assertTrue(rig.link.requests().single().getBoolean("testRun"))
            assertEquals("succeeded", run.outcome)
            assertEquals(listOf("simulated"), run.statuses())
            assertTrue("simulated" in run.attributes.orEmpty())
            // The phone mirrors the PC's countdown for a destructive step it sent (§8.6).
            assertEquals(listOf(0), rig.h.observer.countdowns)
            assertTrue(run.countdown?.shown == true)
        }

    @Test
    fun `no answer from the PC is step_timeout`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = { null }
            val run = rig.run(manualRoutine(lock(), notifyPhone()))
            assertEquals("failed", run.outcome)
            assertEquals("step_timeout", run.reasonCode)
            assertEquals(listOf("failed", "skipped"), run.statuses())
            assertEquals("Gaming PC", run.reasonArgs?.pc)
            assertEquals("LOCK", run.reasonArgs?.action)
        }

    @Test
    fun `the same request is resent after a dropped connection, at most twice`() =
        runTest {
            val rig = Rig(this)
            var calls = 0
            rig.link.onRequest = { request ->
                calls++
                if (calls < 3) {
                    rig.link.setAuthenticated(false)
                    null
                } else {
                    FakeHostLink.succeeded(request)
                }
            }
            val run = rig.run(manualRoutine(lock()))
            assertEquals("succeeded", run.outcome)
            val requests = rig.link.requests()
            assertEquals(3, requests.size)
            assertEquals(1, requests.map { it.getString("runId") to it.getInt("stepIndex") }.toSet().size)
        }

    @Test
    fun `a connection that keeps dropping is transport_lost after two resends`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = {
                rig.link.setAuthenticated(false)
                null
            }
            val run = rig.run(manualRoutine(lock()))
            assertEquals("transport_lost", run.reasonCode)
            assertEquals(3, rig.link.requests().size)
        }

    @Test
    fun `a routine for another PC fails pc_not_selected without sending anything`() =
        runTest {
            val rig = Rig(this)
            rig.link.selected = OTHER_HOST
            val run = rig.run(manualRoutine(lock()))
            assertEquals("pc_not_selected", run.reasonCode)
            assertTrue(rig.link.sent.isEmpty())
        }

    @Test
    fun `an older PC is pc_too_old, and the raw command verb is never used instead`() =
        runTest {
            val rig = Rig(this)
            rig.link.supports = false
            val run = rig.run(manualRoutine(lock()))
            assertEquals("pc_too_old", run.reasonCode)
            assertTrue(rig.link.sent.isEmpty())
        }

    @Test
    fun `an unpaired PC fails before any step`() =
        runTest {
            val rig = Rig(this)
            rig.link.paired = false
            val run = rig.run(manualRoutine(wake(), lock()))
            assertEquals("pc_not_paired", run.reasonCode)
            assertEquals(0, rig.phone.wakeAttempts)
        }

    @Test
    fun `a retried worker whose run already started a step records interrupted_phone and repeats nothing`() =
        runTest {
            val rig = Rig(this)
            val ticket = rig.queue(manualRoutine(shutdown(), notifyPhone()))
            val record = checkNotNull(rig.repo.findRun(ticket.runId))
            rig.repo.recordRun(record.copy(steps = listOf(RoutineRunStep(0, "power", "running"), RoutineRunStep(1, "notify", "pending"))))

            val run = checkNotNull(rig.runner.execute(ticket.copy(attempt = 1)))
            assertEquals("interrupted", run.outcome)
            assertEquals("interrupted_phone", run.reasonCode)
            assertEquals(listOf("failed", "skipped"), run.statuses())
            assertTrue(rig.link.sent.isEmpty())
            assertTrue(rig.phone.messages.isEmpty())
        }

    @Test
    fun `the 540 s budget is a hard stop`() =
        runTest {
            val rig = Rig(this)
            // Two slow wake attempts time out (5 s each, 1 s apart) before the third is sent: the
            // validator's static budget does not count them, so the 530 s delay then overruns.
            rig.phone.wakeBehaviour = { attempt ->
                if (attempt < 3) delay(10_000)
                true
            }
            val run = rig.run(manualRoutine(wake(), delaySeconds(530)))
            assertEquals("failed", run.outcome)
            assertEquals("budget_exceeded", run.reasonCode)
            assertEquals(listOf("succeeded", "failed"), run.statuses())
        }

    @Test
    fun `a cancel during a phone step stops it at once and cancels the rest`() =
        runTest {
            val rig = Rig(this)
            val ticket = rig.queue(manualRoutine(delaySeconds(300), lock()))
            val running = async { rig.runner.execute(ticket) }
            advanceTimeBy(1_000)
            checkNotNull(rig.h.controls.find(ticket.runId)).requestCancel(RoutineCancelKind.USER)
            val run = checkNotNull(running.await())
            assertEquals("cancelled", run.outcome)
            assertEquals("cancelled_on_phone", run.reasonCode)
            assertEquals("phone", run.cancelledBy)
            assertEquals(listOf("cancelled", "cancelled"), run.statuses())
            assertTrue("it must not wait out the delay", testScheduler.currentTime < 300_000)
            assertTrue(rig.link.sent.isEmpty())
        }

    @Test
    fun `a cancel during a host step sends routine_cancel and records what the PC answers`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = { null } // the PC is counting down
            val ticket = rig.queue(manualRoutine(shutdown()))
            val running = async { rig.runner.execute(ticket) }
            advanceTimeBy(3_000)
            checkNotNull(rig.h.controls.find(ticket.runId)).requestCancel(RoutineCancelKind.USER)
            runCurrent()

            val cancel = rig.link.cancels().single()
            assertEquals(ticket.runId, cancel.getString("runId"))
            assertEquals("user", cancel.getString("reason"))
            assertFalse("the run keeps listening for the PC's answer", running.isCompleted)

            val request = rig.link.requests().single()
            rig.link.results.tryEmit(FakeHostLink.result(request, "cancelled", reason = "cancelled_on_phone", cancelledBy = "phone"))
            val run = checkNotNull(running.await())
            assertEquals("cancelled", run.outcome)
            assertEquals("cancelled_on_phone", run.reasonCode)
            assertEquals("phone", run.cancelledBy)
            assertEquals("phone", run.countdown?.cancelledBy)
        }

    @Test
    fun `Pause all stops an automatic run before its next step`() =
        runTest {
            val rig = Rig(this)
            val ticket = rig.queue(manualRoutine(delaySeconds(60), lock()), source = "home.arrive")
            val running = async { rig.runner.execute(ticket) }
            advanceTimeBy(1_000)
            rig.repo.setPausedAll(true)
            val run = checkNotNull(running.await())
            assertEquals("cancelled", run.outcome)
            assertEquals("pause", run.cancelledBy)
            assertTrue(rig.link.sent.isEmpty())
        }

    @Test
    fun `a host step after a power-off step is skipped unless a waitOnline comes between`() =
        runTest {
            val rig = Rig(this)
            val skipped = rig.run(manualRoutine(shutdown(), lock()))
            assertEquals("succeeded", skipped.outcome)
            assertEquals(listOf("succeeded", "skipped"), skipped.statuses())
            assertEquals("after_power_off", skipped.steps?.get(1)?.reasonCode)
            assertEquals(1, rig.link.requests().size)

            val rig2 = Rig(this)
            val waited = rig2.run(manualRoutine(shutdown(), waitOnline(), lock()))
            assertEquals(listOf("succeeded", "succeeded", "succeeded"), waited.statuses())
            assertEquals(2, rig2.link.requests().size)
        }

    @Test
    fun `waitOnline that never sees the PC is wait_timeout after a wake, pc_unreachable without one`() =
        runTest {
            val woke = Rig(this)
            woke.link.setAuthenticated(false)
            woke.link.connectSucceeds = false
            val afterWake = woke.run(manualRoutine(wake(), waitOnline(30)))
            assertEquals("wait_timeout", afterWake.reasonCode)
            assertEquals("30", afterWake.steps?.get(1)?.reasonArgs?.duration)
            assertTrue(woke.link.connectAttempts > 1)

            val cold = Rig(this)
            cold.link.setAuthenticated(false)
            cold.link.connectSucceeds = false
            assertEquals("pc_unreachable", cold.run(manualRoutine(waitOnline(30))).reasonCode)

            val away = Rig(this)
            away.link.setAuthenticated(false)
            away.link.connectSucceeds = false
            away.phone.away = true
            assertEquals("pc_unreachable_away", away.run(manualRoutine(lock())).reasonCode)
        }

    @Test
    fun `wake failures map to their own codes`() =
        runTest {
            val noPermission = Rig(this)
            noPermission.phone.localNetwork = false
            assertEquals("permission_local_network", noPermission.run(manualRoutine(wake())).reasonCode)

            val notSent = Rig(this)
            notSent.phone.wakeSends = false
            assertEquals("wake_send_failed", notSent.run(manualRoutine(wake())).reasonCode)
            assertEquals(3, notSent.phone.wakeAttempts)
        }

    @Test
    fun `a message the phone may not show is recorded, and the run still succeeds`() =
        runTest {
            val rig = Rig(this)
            rig.phone.notificationsAllowed = false
            val run = rig.run(manualRoutine(notifyPhone()))
            assertEquals("succeeded", run.outcome)
            assertTrue("notify_denied_phone" in run.attributes.orEmpty())
        }

    @Test
    fun `a destructive step refused for a countdown conflict is skipped and the run goes on`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = { FakeHostLink.result(it, "failed", reason = "conflict_countdown_active") }
            val run = rig.run(manualRoutine(shutdown(), notifyPhone()))
            assertEquals("succeeded", run.outcome)
            assertEquals(listOf("skipped", "succeeded"), run.statuses())
            assertEquals("conflict_countdown_active", run.steps?.first()?.reasonCode)
        }

    /** The repository as the runner's store, with recordRun failing on chosen calls (1-based). */
    private class FlakyStore(private val inner: RoutineRepository, val failOn: (Int) -> Boolean) : RoutineRunStore by inner {
        var calls = 0

        override suspend fun recordRun(run: RoutineRun): RoutineRun {
            calls++
            if (failOn(calls)) throw java.io.IOException("disk full")
            return inner.recordRun(run)
        }
    }

    @Test
    fun `a throwing link before the first step ends the run internal_error and never blocks the next start`() =
        runTest {
            val rig = Rig(this)
            rig.link.displayNameError = java.io.IOException("known-host records unreadable")
            val run = rig.run(manualRoutine(lock()))
            assertEquals("failed", run.outcome)
            assertEquals("internal_error", run.reasonCode)
            assertTrue(rig.repo.activeRuns.value.isEmpty())
            assertTrue(rig.repo.run(checkNotNull(run.routineId), "manual.app") is RoutineRunStart.Started)
        }

    @Test
    fun `a throwing persist ends the run internal_error through the normal finish path`() =
        runTest {
            val rig = Rig(this)
            val ticket = rig.queue(manualRoutine(lock()))
            // The first write (the started record) fails; the finish path's write succeeds.
            val store = FlakyStore(rig.repo) { it == 1 }
            val runner = RoutineRunner(store, rig.link, rig.phone, rig.h.observer, rig.h.clock, rig.h.controls)
            val run = checkNotNull(runner.execute(ticket))
            assertEquals("internal_error", run.reasonCode)
            assertEquals("failed", rig.repo.findRun(ticket.runId)?.outcome)
            assertTrue(rig.link.sent.isEmpty())
            assertTrue(rig.repo.activeRuns.value.isEmpty())
            assertTrue(rig.repo.run(ticket.routineId, "manual.app") is RoutineRunStart.Started)
        }

    @Test
    fun `when even the finish path fails, the worker's backstop clears the run so later starts still work`() =
        runTest {
            val rig = Rig(this)
            val ticket = rig.queue(manualRoutine(lock()))
            val runner = RoutineRunner(FlakyStore(rig.repo) { true }, rig.link, rig.phone, rig.h.observer, rig.h.clock, rig.h.controls)
            try {
                runner.execute(ticket)
                fail("every write fails, so the runner cannot finish")
            } catch (_: java.io.IOException) {
            }
            // What RoutineWorker's catch does.
            assertTrue(rig.repo.abandonRun(ticket.runId, "internal_error"))
            assertEquals("internal_error", rig.repo.findRun(ticket.runId)?.reasonCode)
            assertTrue(rig.repo.activeRuns.value.isEmpty())
            assertTrue(rig.repo.run(ticket.routineId, "manual.app") is RoutineRunStart.Started)
        }

    @Test
    fun `a budget that runs out during a destructive host step sends routine_cancel before giving up`() =
        runTest {
            val rig = Rig(this)
            // Statically inside the budget (460 + 60 + 15 = 535 s), but the connect before the step
            // takes a minute, so the PC is still counting down when the 540 s run out.
            rig.link.setAuthenticated(false)
            rig.link.connectDelayMs = 60_000
            rig.link.onRequest = { null }
            val run = rig.run(manualRoutine(delaySeconds(460), shutdown()))
            assertEquals("budget_exceeded", run.reasonCode)
            val cancel = rig.link.cancels().single()
            assertEquals(run.runId, cancel.getString("runId"))
            assertEquals("user", cancel.getString("reason"))
        }

    @Test
    fun `a stopped worker cancels the host step it was waiting on`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = { null }
            val ticket = rig.queue(manualRoutine(shutdown()))
            val running = async { rig.runner.execute(ticket) }
            advanceTimeBy(3_000)
            running.cancel() // Android stops the job: no cancel of ours was requested
            runCurrent()
            assertEquals(ticket.runId, rig.link.cancels().single().getString("runId"))
            // Left `running` for the retry or the sweep to report interrupted_phone.
            assertEquals("running", rig.repo.findRun(ticket.runId)?.outcome)
        }

    @Test
    fun `a power-off step that did not happen never makes later host steps skip after_power_off`() =
        runTest {
            val conflict = Rig(this)
            conflict.link.onRequest = { request ->
                if (request.getInt("stepIndex") == 0) FakeHostLink.result(request, "failed", reason = "conflict_countdown_active")
                else FakeHostLink.succeeded(request)
            }
            val afterConflict = conflict.run(manualRoutine(shutdown(), lock()))
            assertEquals(listOf("skipped", "succeeded"), afterConflict.statuses())
            assertEquals(2, conflict.link.requests().size)

            val test = Rig(this)
            test.link.onRequest = { request ->
                if (request.getInt("stepIndex") == 0) FakeHostLink.result(request, "simulated", reason = "simulated")
                else FakeHostLink.succeeded(request)
            }
            val afterSimulation = test.run(manualRoutine(shutdown(), lock()), testRun = true)
            assertEquals(listOf("simulated", "succeeded"), afterSimulation.statuses())
            assertEquals(2, test.link.requests().size)
        }

    @Test
    fun `a failure reported by the PC is the run's reason`() =
        runTest {
            val rig = Rig(this)
            rig.link.onRequest = { FakeHostLink.result(it, "failed", reason = "power_denied_by_os") }
            val run = rig.run(manualRoutine(shutdown()))
            assertEquals("failed", run.outcome)
            assertEquals("power_denied_by_os", run.reasonCode)
            assertEquals("SHUTDOWN", run.reasonArgs?.action)
        }
}
