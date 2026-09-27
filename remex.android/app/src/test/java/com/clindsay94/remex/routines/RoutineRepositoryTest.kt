package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunReportPayload
import com.clindsay94.remex.routines.model.RoutineRunStep
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The S1d-facing repository over in-memory stores (spec §6.8, §7.4.1, §8.1, §8.7, §8.8; RemEx-pp0rt.5). */
class RoutineRepositoryTest {

    @Test
    fun `save creates with revision 1 and timestamps, and an update bumps the revision`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val created = (repo.save(manualRoutine(lock()).copy(id = null)) as RoutineSaveResult.Saved).routine
            assertNotNull(created.id)
            assertEquals(1L, created.revision)
            assertEquals(h.clock.now, created.createdAtUnixMs)

            h.clock.now += 5_000
            val updated = (repo.save(created.copy(name = "Late night")) as RoutineSaveResult.Saved).routine
            assertEquals(2L, updated.revision)
            assertEquals(created.createdAtUnixMs, updated.createdAtUnixMs)
            assertEquals(h.clock.now, updated.updatedAtUnixMs)
            assertEquals(listOf("Late night"), repo.routines.value.map { it.routine.name })

            // A fresh repository over the same stores reads the same thing back.
            assertEquals("Late night", h.repository().routine(checkNotNull(created.id))?.name)
        }

    @Test
    fun `an invalid routine is refused with its first failing rule and nothing is stored`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val result = repo.save(manualRoutine(delaySeconds(0)))
            assertEquals("invalid_field", (result as RoutineSaveResult.Invalid).verdict.reasonCode)
            assertTrue(repo.routines.value.isEmpty())
            assertTrue(h.docKv.map.isEmpty())
        }

    @Test
    fun `editing a PC-run routine or the pause flag bumps that PC's sync revision in the same write`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            repo.save(manualRoutine(lock()))
            assertNull(storedSync(h)[HOST])

            repo.save(pcIdleRoutine())
            assertEquals(1L, storedSync(h).getValue(HOST).localRevision)

            repo.setPausedAll(true)
            assertEquals(2L, storedSync(h).getValue(HOST).localRevision)
            assertTrue(repo.pausedAll.value)
        }

    private suspend fun storedSync(h: RepositoryHarness): Map<String, RoutineHostSync> =
        (RoutineDocumentStore(h.docKv, h.cipherSource).load() as RoutineDocumentLoad.Loaded).document.hostSync

    @Test
    fun `run records the run as running BEFORE enqueueing its unique work`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val routine = (repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine
            val start = repo.run(checkNotNull(routine.id), "manual.app", testRun = true)
            val runId = (start as RoutineRunStart.Started).runId

            val record = checkNotNull(repo.findRun(runId))
            assertEquals("running", record.outcome)
            assertEquals(listOf("pending"), record.steps?.map { it.status })
            assertTrue(record.testRun)
            assertEquals(runId, h.scheduler.enqueued.single().runId)
            assertEquals(record, repo.activeRuns.value[routine.id])
        }

    @Test
    fun `starts that may not run are recorded as skips, and identical skips coalesce`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)

            // Single-flight: a second start while the first is still running is a recorded skip.
            assertTrue(repo.run(id, "manual.app") is RoutineRunStart.Started)
            val first = repo.run(id, "nfc.tap") as RoutineRunStart.Skipped
            assertEquals("already_running", first.reasonCode)
            assertNotNull(first.runId)
            val second = repo.run(id, "nfc.tap") as RoutineRunStart.Skipped
            assertNull("an identical skip inside 60 s is folded", second.runId)
            assertEquals(1, repo.history.value.count { it.outcome == "skipped" })
            assertEquals(1, h.observer.finished.size)
        }

    @Test
    fun `a disabled routine is skipped for every source but in-app Run`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock(), enabled = false)) as RoutineSaveResult.Saved).routine.id)
            assertEquals("skipped_disabled", (repo.run(id, "manual.shortcut") as RoutineRunStart.Skipped).reasonCode)
        }

    @Test
    fun `Pause all skips automatic sources and leaves person-initiated ones alone`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            repo.setPausedAll(true)
            assertEquals("paused_on_phone", (repo.run(id, "home.arrive") as RoutineRunStart.Skipped).reasonCode)
            assertTrue(repo.run(id, "manual.widget") is RoutineRunStart.Started)
        }

    @Test
    fun `Pause all cancels an automatic run in progress but not a manual one`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val auto = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            val manual = checkNotNull((repo.save(manualRoutine(lock(), name = "Other")) as RoutineSaveResult.Saved).routine.id)
            val autoRun = (repo.run(auto, "home.arrive") as RoutineRunStart.Started).runId
            val manualRun = (repo.run(manual, "manual.app") as RoutineRunStart.Started).runId

            repo.setPausedAll(true)

            // Neither had a runner attached, so the queued automatic run is cancelled outright.
            val cancelled = checkNotNull(repo.findRun(autoRun))
            assertEquals("cancelled", cancelled.outcome)
            assertEquals("pause", cancelled.cancelledBy)
            assertEquals(listOf(auto), h.scheduler.cancelled)
            assertEquals("running", repo.findRun(manualRun)?.outcome)
        }

    @Test
    fun `cancelling a run with its runner attached only asks the runner to stop`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            val runId = (repo.run(id, "manual.app") as RoutineRunStart.Started).runId
            h.controls.obtain(runId, id).attached = true

            assertTrue(repo.cancel(id))
            assertEquals(RoutineCancelKind.USER, h.controls.find(runId)?.cancelRequest?.value)
            assertEquals("running", repo.findRun(runId)?.outcome)
            assertTrue(h.scheduler.cancelled.isEmpty())
        }

    @Test
    fun `a PC-run routine is never run by the phone runner`() =
        runTest {
            val repo = RepositoryHarness().repository()
            val id = checkNotNull((repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)
            assertEquals(RoutineRunStart.RunsOnPc, repo.run(id, "manual.app"))
        }

    @Test
    fun `WorkManager refusing the work records a failed run`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            h.scheduler.refuse = true
            val start = repo.run(id, "manual.app") as RoutineRunStart.Failed
            assertEquals("failed", repo.findRun(start.runId)?.outcome)
            assertEquals("internal_error", repo.findRun(start.runId)?.reasonCode)
        }

    @Test
    fun `deleting a routine removes its history`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            repo.run(id, "manual.app")
            assertTrue(h.historyKv.map.containsKey("run/$id"))
            assertTrue(repo.delete(id))
            assertFalse(h.historyKv.map.containsKey("run/$id"))
            assertTrue(repo.routines.value.isEmpty())
        }

    @Test
    fun `an unreadable store is never written over until the user resets it`() =
        runTest {
            val h = RepositoryHarness()
            h.docKv.map[RoutineDocumentStore.KEY] = "sealed under a key that is gone"
            val repo = h.repository()
            repo.load()
            assertEquals(RoutineStoreHealth.UNREADABLE, repo.status.value.health)
            assertEquals(RoutineSaveResult.Unavailable, repo.save(manualRoutine(lock())))
            assertEquals("sealed under a key that is gone", h.docKv.map[RoutineDocumentStore.KEY])

            assertTrue(repo.resetUnreadableStore())
            assertEquals(RoutineStoreHealth.OK, repo.status.value.health)
            assertTrue(repo.save(manualRoutine(lock())) is RoutineSaveResult.Saved)
        }

    @Test
    fun `a keyset loss is reported with a banner and a store_reset history record, once`() =
        runTest {
            val lostAt = FakeClock().now - 1_000
            val h = RepositoryHarness(cipherSource = FakeCipherSource(keyLossAt = lostAt))
            val repo = h.repository()
            repo.load()
            assertEquals(lostAt, repo.status.value.resetAtUnixMs)
            assertEquals(listOf("store_reset"), repo.history.value.map { it.reasonCode })
            assertNull(h.cipherSource.keyLossAt)

            assertTrue(repo.dismissStoreReset())
            assertNull(repo.status.value.resetAtUnixMs)
        }

    @Test
    fun `a newer document is read-only`() =
        runTest {
            val h = RepositoryHarness()
            RoutineDocumentStore(h.docKv, h.cipherSource).save(RoutineStoreDocument(schemaVersion = 2))
            val repo = h.repository()
            repo.load()
            assertTrue(repo.status.value.readOnly)
            assertEquals(RoutineSaveResult.ReadOnly, repo.save(manualRoutine(lock())))
            assertFalse(repo.setPausedAll(true))
        }

    @Test
    fun `a phone run left running by a dead process is swept to interrupted_phone`() =
        runTest {
            val h = RepositoryHarness()
            val first = h.repository()
            val id = checkNotNull((first.save(manualRoutine(lock(), delaySeconds(5))) as RoutineSaveResult.Saved).routine.id)
            val runId = (first.run(id, "manual.app") as RoutineRunStart.Started).runId
            val started = checkNotNull(first.findRun(runId))
            first.recordRun(started.copy(steps = listOf(RoutineRunStep(0, "power", "succeeded"), RoutineRunStep(1, "delay", "running"))))

            // The process died and WorkManager holds nothing for it any more.
            h.scheduler.active.clear()
            val next = h.repository()
            val swept = checkNotNull(next.findRun(runId))
            assertEquals("interrupted", swept.outcome)
            assertEquals("interrupted_phone", swept.reasonCode)
            assertEquals(listOf("succeeded", "failed"), swept.steps?.map { it.status })
        }

    @Test
    fun `PC run reports upsert by run id and advance the cursor only for stored pages`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val pcRoutine = checkNotNull((repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)
            val pcRun = RoutineRun(runId = uuid(), seq = 7, routineId = pcRoutine, hostIdentity = HOST, outcome = "running", triggeredAtUnixMs = h.clock.now)

            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(pcRun), live = true))
            assertEquals(0L, storedSync(h).getValue(HOST).runCursor)

            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(pcRun.copy(seq = 9, outcome = "succeeded", ownerClientId = "leak"))))
            val stored = repo.history.value.single { it.runId == pcRun.runId }
            assertEquals("succeeded", stored.outcome)
            assertEquals("pc", stored.origin)
            assertNull(stored.ownerClientId)
            assertEquals(9L, storedSync(h).getValue(HOST).runCursor)

            // An older copy never replaces a newer one.
            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(pcRun.copy(seq = 8, outcome = "failed"))))
            assertEquals("succeeded", repo.history.value.single { it.runId == pcRun.runId }.outcome)
        }

    @Test
    fun `reorder keeps routines the list did not name`() =
        runTest {
            val repo = RepositoryHarness().repository()
            val a = checkNotNull((repo.save(manualRoutine(lock(), name = "A")) as RoutineSaveResult.Saved).routine.id)
            val b = checkNotNull((repo.save(manualRoutine(lock(), name = "B")) as RoutineSaveResult.Saved).routine.id)
            repo.save(manualRoutine(lock(), name = "C"))
            assertTrue(repo.reorder(listOf(b, a)))
            assertEquals(listOf("B", "A", "C"), repo.routines.value.map { it.routine.name })
        }
}
