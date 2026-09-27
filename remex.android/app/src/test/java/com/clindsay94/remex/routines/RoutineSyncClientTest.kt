package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunReportPayload
import com.clindsay94.remex.routines.model.RoutineRunStep
import com.clindsay94.remex.routines.model.RoutineSyncItemResult
import com.clindsay94.remex.routines.model.RoutineSyncResultPayload
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The phone's routine sync client (routines spec §7.3.1, §7.3.8, §7.4.1, §7.4.5; §13.3
 * RoutineSyncClientTest, ForgetPcFlushTest, PhoneRunPcRoutineTest; RemEx-pp0rt.12).
 */
@OptIn(ExperimentalCoroutinesApi::class)
class RoutineSyncClientTest {
    private class Rig(scope: TestScope) {
        val h = RepositoryHarness(clock = FakeClock(elapsed = { scope.testScheduler.currentTime }))
        val repo = h.repository()
        val link = FakeHostLink()
        val connections = MutableStateFlow<Long?>(null)
        val client = RoutineSyncClient(repo, link, connections, h.observer, h.clock, ioDispatcher = StandardTestDispatcher(scope.testScheduler))

        fun syncs(): List<JSONObject> = link.sent.filter { it.getString("type") == "routines_sync" }.map { it.getJSONObject("routinesSync") }

        fun runRequests(): List<JSONObject> = link.sent.filter { it.getString("type") == "routine_run_request" }.map { it.getJSONObject("routineRunRequest") }

        suspend fun storedSync(): Map<String, RoutineHostSync> =
            (RoutineDocumentStore(h.docKv, h.cipherSource).load() as RoutineDocumentLoad.Loaded).document.hostSync
    }

    private suspend fun RoutineRepository.savePc(): String = checkNotNull((save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)

    private fun ok(revision: Long, vararg results: RoutineSyncItemResult, unsolicited: Boolean = false, pcDisabled: List<String>? = null, hostPaused: Boolean = false) =
        RoutineSyncResultPayload(
            revision = revision,
            storedRevision = revision,
            status = if (results.all { it.accepted }) "ok" else "partial",
            results = results.toList(),
            unsolicited = unsolicited,
            pcDisabled = pcDisabled,
            hostPaused = hostPaused,
            idleSource = "win32.lastinput",
            sessionSource = "win32.wts",
        )

    // backgroundScope work does not keep advanceUntilIdle going, so time is moved on explicitly.
    private fun TestScope.settle() {
        advanceTimeBy(10_000)
        runCurrent()
    }

    private fun TestScope.connect(rig: Rig) {
        backgroundScope.launch { rig.client.run() }
        rig.connections.value = 1L
        runCurrent()
    }

    @Test
    fun `an edit bumps the PC's revision in the same store write`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            val doc = (RoutineDocumentStore(rig.h.docKv, rig.h.cipherSource).load() as RoutineDocumentLoad.Loaded).document
            assertEquals(listOf(id), doc.routines.map { it.id })
            assertEquals(1L, doc.hostSync.getValue(HOST).localRevision)
            // A phone-run routine for the same PC is not part of the PC's set.
            rig.repo.save(manualRoutine(lock()))
            assertEquals(1L, rig.storedSync().getValue(HOST).localRevision)
        }

    @Test
    fun `connect sends the full set once, and an edit again after the 2 s coalescing`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            connect(rig)
            val first = rig.syncs().single()
            assertEquals(1L, first.getLong("revision"))
            assertEquals(id, first.getJSONArray("routines").getJSONObject(0).getString("id"))
            assertFalse(first.optBoolean("forget"))

            rig.repo.setEnabled(id, false)
            advanceTimeBy(500)
            runCurrent()
            assertEquals("coalesced, not sent at once", 1, rig.syncs().size)
            settle()
            assertEquals(listOf(1L, 2L), rig.syncs().map { it.getLong("revision") })
        }

    @Test
    fun `a phone with no PC routines never sends anything`() =
        runTest {
            val rig = Rig(this)
            rig.repo.save(manualRoutine(lock()))
            connect(rig)
            settle()
            assertTrue(rig.syncs().isEmpty())
        }

    @Test
    fun `an older PC never hears routines_sync`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            rig.link.supports = false
            connect(rig)
            settle()
            assertTrue(rig.syncs().isEmpty())
        }

    @Test
    fun `stale_revision jumps past the PC's revision and resends once, never twice`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            connect(rig)
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 1, storedRevision = 5, status = "stale_revision"))
            settle()
            assertEquals(listOf(1L, 6L), rig.syncs().map { it.getLong("revision") })

            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 6, storedRevision = 9, status = "stale_revision"))
            settle()
            assertEquals(10L, rig.storedSync().getValue(HOST).localRevision)
            assertEquals("resent once only", listOf(1L, 6L), rig.syncs().map { it.getLong("revision") })
        }

    @Test
    fun `revision_conflict bumps and resends once`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            connect(rig)
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 1, storedRevision = 1, status = "revision_conflict"))
            settle()
            assertEquals(listOf(1L, 2L), rig.syncs().map { it.getLong("revision") })
        }

    @Test
    fun `the PC's not-ready answer is retried after a back-off, never hot`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            connect(rig)
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 1, status = "rate_limited"))
            runCurrent()
            advanceTimeBy(1_999)
            runCurrent()
            assertEquals(1, rig.syncs().size)
            advanceTimeBy(2)
            runCurrent()
            assertEquals(listOf(1L, 1L), rig.syncs().map { it.getLong("revision") })

            // The next wait doubles.
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 1, status = "rate_limited"))
            advanceTimeBy(3_999)
            runCurrent()
            assertEquals(2, rig.syncs().size)
            advanceTimeBy(2)
            runCurrent()
            assertEquals(3, rig.syncs().size)
        }

    @Test
    fun `the back-off doubles to a ceiling`() {
        assertEquals(listOf(2_000L, 4_000L, 8_000L, 16_000L, 32_000L, 60_000L, 60_000L), (0..6).map { RoutineSyncBackoff.delayMs(it) })
    }

    @Test
    fun `ok and partial answers become per-routine sync states`() =
        runTest {
            val rig = Rig(this)
            val a = rig.repo.savePc()
            val b = rig.repo.savePc()
            connect(rig)
            val revision = rig.syncs().last().getLong("revision")
            val routineA = checkNotNull(rig.repo.routine(a))
            val routineB = checkNotNull(rig.repo.routine(b))
            assertEquals(RoutineSyncState.PENDING, RoutineSyncStates.routine(routineA, rig.repo.hostSync.value[HOST])?.state)

            rig.client.onSyncResult(
                ok(revision, RoutineSyncItemResult(a, accepted = true), RoutineSyncItemResult(b, accepted = false, reasonCode = "power_unsupported"), hostPaused = true),
            )
            val entry = rig.repo.hostSync.value[HOST]
            assertEquals(revision, entry?.ackedRevision)
            assertEquals(RoutineSyncState.SYNCED, RoutineSyncStates.routine(routineA, entry)?.state)
            val rejected = RoutineSyncStates.routine(routineB, entry)
            assertEquals(RoutineSyncState.REJECTED, rejected?.state)
            assertEquals("power_unsupported", rejected?.reasonCode)
            val pc = RoutineSyncStates.pc(HOST, entry)
            assertTrue(pc.hostPaused)
            assertFalse(pc.pending)
            assertEquals(true, RoutineSyncStates.triggerAvailable("pc.idle", pc))

            // An unsolicited answer: the PC user switched routine A off there (R-UX-37).
            rig.client.onSyncResult(ok(revision, RoutineSyncItemResult(a, accepted = true), unsolicited = true, pcDisabled = listOf(a)))
            assertEquals(RoutineSyncState.OFF_ON_PC, RoutineSyncStates.routine(routineA, rig.repo.hostSync.value[HOST])?.state)
            assertTrue(rig.storedSync().getValue(HOST).lastResultJson.orEmpty().contains("pcDisabled"))
        }

    @Test
    fun `blocked_by_pc stops sending until the PC unblocks, then the newer set goes`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            connect(rig)
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 1, storedRevision = 0, status = "blocked_by_pc"))
            val routine = checkNotNull(rig.repo.routine(id))
            assertEquals(RoutineSyncState.REFUSED, RoutineSyncStates.routine(routine, rig.repo.hostSync.value[HOST])?.state)
            assertTrue(RoutineSyncStates.pc(HOST, rig.repo.hostSync.value[HOST]).blocked)

            rig.repo.setEnabled(id, false)
            settle()
            assertEquals("nothing sent while blocked", 1, rig.syncs().size)

            // Unblocked on the PC: its unsolicited answer names the older set it still holds.
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = 0, storedRevision = 0, status = "ok", unsolicited = true))
            settle()
            assertEquals(listOf(1L, 2L), rig.syncs().map { it.getLong("revision") })
        }

    @Test
    fun `Pause all reaches the PC as the paused flag of the next sync`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            connect(rig)
            rig.repo.setPausedAll(true)
            settle()
            val last = rig.syncs().last()
            assertEquals(2L, last.getLong("revision"))
            assertTrue(last.getBoolean("paused"))
        }

    @Test
    fun `reordering a PC's routines is an edit of that PC's set`() =
        runTest {
            val rig = Rig(this)
            val a = rig.repo.savePc()
            val b = rig.repo.savePc()
            val before = rig.storedSync().getValue(HOST).localRevision
            assertTrue(rig.repo.reorder(listOf(b, a)))
            assertEquals(before + 1, rig.storedSync().getValue(HOST).localRevision)
        }

    // ── Forget flush (T8, ForgetPcFlushTest) ──

    @Test
    fun `forget flush tells the connected PC, waits for its answer, then clears the phone`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            rig.repo.save(manualRoutine(lock()))
            connect(rig)
            val outcome = async { rig.client.forgetPc(HOST) }
            runCurrent()
            val flush = rig.syncs().last()
            assertTrue(flush.getBoolean("forget"))
            assertEquals(0, flush.getJSONArray("routines").length())
            rig.client.onSyncResult(RoutineSyncResultPayload(revision = flush.getLong("revision"), storedRevision = 0, status = "ok", results = emptyList()))
            assertEquals(RoutinePcForget.FLUSHED, outcome.await())

            assertTrue("every routine bound to that PC is gone", rig.repo.routines.value.isEmpty())
            assertNull(rig.storedSync()[HOST])
            val sent = rig.syncs().size
            settle()
            assertEquals("nothing recreates the set on the PC afterwards", sent, rig.syncs().size)
        }

    @Test
    fun `forget with no connection clears the phone and says the PC keeps its copy`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            rig.link.connectedHost = null
            assertEquals(RoutinePcForget.PHONE_ONLY, rig.client.forgetPc(HOST))
            assertTrue(rig.link.sent.isEmpty())
            assertTrue(rig.repo.routines.value.isEmpty())
        }

    @Test
    fun `forget waits at most 3 s for the PC`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            connect(rig)
            val outcome = async { rig.client.forgetPc(HOST) }
            advanceTimeBy(3_001)
            runCurrent()
            assertEquals(RoutinePcForget.PHONE_ONLY, outcome.await())
            assertTrue(rig.repo.routines.value.isEmpty())
        }

    @Test
    fun `forgetting a PC with no routines is nothing to report`() =
        runTest {
            val rig = Rig(this)
            rig.link.connectedHost = null
            assertEquals(RoutinePcForget.NOTHING, rig.client.forgetPc(HOST))
        }

    // ── Run and Test from the phone (§7.3.8, PhoneRunPcRoutineTest) ──

    @Test
    fun `Run sends routine_run_request naming the stored routine only, and live reports drive progress`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            connect(rig)
            rig.client.onSyncResult(ok(1, RoutineSyncItemResult(id, accepted = true)))

            val start = async { rig.client.runOnPc(id, testRun = true) }
            runCurrent()
            val request = rig.runRequests().single()
            // T24: a run request can name a stored routine, never carry a definition.
            assertEquals(setOf("runId", "routineId", "testRun", "source"), request.keys().asSequence().toSet())
            assertEquals(id, request.getString("routineId"))
            assertTrue(request.getBoolean("testRun"))
            assertEquals("manual.app", request.getString("source"))

            val runId = request.getString("runId")
            val live =
                RoutineRun(
                    runId = runId, routineId = id, hostIdentity = HOST, outcome = "running", testRun = true,
                    triggeredAtUnixMs = rig.h.clock.now, startedAtUnixMs = rig.h.clock.now,
                    steps = listOf(RoutineRunStep(0, "power", "running")),
                )
            val report = RoutineRunReportPayload(runs = listOf(live), live = true)
            rig.client.onRunReport(report, rig.repo.applyRunReport(report))
            assertEquals(RoutinePcRunStart.Started(runId), start.await())
            assertEquals(listOf<Int?>(0), rig.h.observer.progress)
            assertEquals(runId, rig.repo.pcActiveRuns.value[id]?.runId)
            assertTrue("a PC run is not a phone run", rig.repo.activeRuns.value.isEmpty())

            val final = RoutineRunReportPayload(runs = listOf(live.copy(seq = 3, outcome = "succeeded", steps = listOf(RoutineRunStep(0, "power", "simulated")))))
            rig.client.onRunReport(final, rig.repo.applyRunReport(final))
            assertEquals(listOf("succeeded"), rig.h.observer.finished.map { it.outcome })
            assertTrue(rig.repo.pcActiveRuns.value.isEmpty())
        }

    @Test
    fun `Run for a PC other than the selected one asks to switch and sends nothing`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.link.selected = OTHER_HOST
            assertEquals(RoutinePcRunStart.NotSelected(HOST), rig.client.runOnPc(id, testRun = false))
            assertTrue(rig.runRequests().isEmpty())
        }

    @Test
    fun `Switch and run starts once the person has switched to that PC`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.client.runAfterSwitch(id, HOST, testRun = false)
            connect(rig)
            advanceTimeBy(3_001)
            runCurrent()
            assertEquals(id, rig.runRequests().single().getString("routineId"))
        }

    @Test
    fun `a pending switch-run dies with a connection to another PC, and a later reconnect to the target runs nothing`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.client.runAfterSwitch(id, HOST, testRun = false)
            rig.link.connectedHost = OTHER_HOST
            connect(rig)
            settle()
            // Back to the target PC well inside the 2 minute window, for any reason.
            rig.connections.value = null
            runCurrent()
            rig.link.connectedHost = HOST
            rig.connections.value = 2L
            settle()
            assertTrue(rig.runRequests().isEmpty())
        }

    @Test
    fun `the switch itself (a drop, then the target) runs it once`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.link.connectedHost = OTHER_HOST
            connect(rig)
            // Armed while connected to the other PC: that connection is not the "next" one.
            rig.client.runAfterSwitch(id, HOST, testRun = false)
            rig.connections.value = null
            runCurrent()
            rig.link.connectedHost = HOST
            rig.connections.value = 2L
            settle()
            assertEquals(1, rig.runRequests().size)
            // A later reconnect does not run it again.
            rig.connections.value = null
            runCurrent()
            rig.connections.value = 3L
            settle()
            assertEquals(1, rig.runRequests().size)
        }

    @Test
    fun `leaving Connection or letting the window pass drops a pending switch-run`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.client.runAfterSwitch(id, HOST, testRun = false)
            rig.client.cancelPendingSwitchRun()
            connect(rig)
            settle()
            assertTrue(rig.runRequests().isEmpty())

            rig.connections.value = null
            runCurrent()
            rig.client.runAfterSwitch(id, HOST, testRun = false)
            advanceTimeBy(RoutineSyncClient.SWITCH_RUN_WINDOW_MS + 1)
            rig.connections.value = 2L
            settle()
            assertTrue(rig.runRequests().isEmpty())
        }

    @Test
    fun `a switched run that cannot start is reported for the Routines screen`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.client.runAfterSwitch(id, HOST, testRun = true)
            rig.link.supports = false
            connect(rig)
            settle()
            val outcome = rig.client.switchRunOutcomes.value
            assertEquals(RoutineSwitchRunOutcome(id, true, RoutinePcRunStart.Failed("pc_too_old")), outcome)
            rig.client.consumeSwitchRunOutcome(checkNotNull(outcome))
            assertNull(rig.client.switchRunOutcomes.value)
        }

    @Test
    fun `the whole forget flush, capability wait included, is bounded by 3 s`() =
        runTest {
            val rig = Rig(this)
            rig.repo.savePc()
            connect(rig)
            rig.link.supportsDelayMs = 5_000
            val outcome = async { rig.client.forgetPc(HOST) }
            advanceTimeBy(3_001)
            runCurrent()
            assertTrue(outcome.isCompleted)
            assertEquals(RoutinePcForget.PHONE_ONLY, outcome.await())
        }

    @Test
    fun `a skipped answer surfaces the PC's reason`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            connect(rig)
            rig.client.onSyncResult(ok(1, RoutineSyncItemResult(id, accepted = true)))
            val start = async { rig.client.runOnPc(id, testRun = false) }
            runCurrent()
            val runId = rig.runRequests().single().getString("runId")
            val skipped = RoutineRunReportPayload(runs = listOf(RoutineRun(runId = runId, routineId = id, hostIdentity = HOST, outcome = "skipped", reasonCode = "disabled_on_pc")), live = true)
            rig.client.onRunReport(skipped, rig.repo.applyRunReport(skipped))
            assertEquals(RoutinePcRunStart.Skipped("disabled_on_pc", runId), start.await())
        }

    @Test
    fun `an unreachable PC and an older PC are reported, nothing is sent`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            rig.link.supports = false
            assertEquals(RoutinePcRunStart.Failed("pc_too_old"), rig.client.runOnPc(id, testRun = false))
            rig.link.supports = true
            rig.link.setAuthenticated(false)
            rig.link.connectSucceeds = false
            val start = async { rig.client.runOnPc(id, testRun = false) }
            advanceTimeBy(10_001)
            assertEquals(RoutinePcRunStart.Failed("pc_unreachable"), start.await())
            assertTrue(rig.runRequests().isEmpty())
        }

    @Test
    fun `Stop on a PC run sends routine_cancel for it`() =
        runTest {
            val rig = Rig(this)
            val id = rig.repo.savePc()
            val runId = uuid()
            val live = RoutineRunReportPayload(runs = listOf(RoutineRun(runId = runId, routineId = id, hostIdentity = HOST, outcome = "running", startedAtUnixMs = rig.h.clock.now)), live = true)
            rig.repo.applyRunReport(live)
            assertFalse("the phone runner cannot stop a PC run", rig.repo.cancelRun(runId))
            assertTrue(rig.client.cancelPcRun(runId))
            assertEquals(runId, rig.link.cancels().single().getString("runId"))
        }
}
