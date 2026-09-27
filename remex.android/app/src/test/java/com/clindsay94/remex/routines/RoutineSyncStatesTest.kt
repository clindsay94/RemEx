package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunReportPayload
import com.clindsay94.remex.routines.model.RoutineSyncItemResult
import com.clindsay94.remex.routines.model.RoutineSyncResultPayload
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** `routine_sync_result` mapping and the PC history merge (routines spec §7.4.1, §8.8; RemEx-pp0rt.12). */
class RoutineSyncStatesTest {
    private fun apply(entry: RoutineHostSync, result: RoutineSyncResultPayload) =
        RoutineSyncProtocol.apply(entry, result, RoutineJson.write(result).toString(), 42L)

    @Test
    fun `the protocol follow-up for every status`() {
        val entry = RoutineHostSync(localRevision = 4, ackedRevision = 2)
        assertEquals(RoutineSyncFollowUp.DONE, apply(entry, RoutineSyncResultPayload(revision = 4, storedRevision = 4, status = "ok")).second)
        assertEquals(RoutineSyncFollowUp.DONE, apply(entry, RoutineSyncResultPayload(revision = 4, storedRevision = 4, status = "partial")).second)
        assertEquals(RoutineSyncFollowUp.RESEND, apply(entry, RoutineSyncResultPayload(revision = 4, storedRevision = 7, status = "stale_revision")).second)
        assertEquals(RoutineSyncFollowUp.RESEND, apply(entry, RoutineSyncResultPayload(revision = 4, storedRevision = 4, status = "revision_conflict")).second)
        for (stop in listOf("blocked_by_pc", "schema_too_new", "payload_too_large")) {
            assertEquals(stop, RoutineSyncFollowUp.STOP, apply(entry, RoutineSyncResultPayload(revision = 4, status = stop)).second)
        }
        assertEquals(RoutineSyncFollowUp.RETRY_LATER, apply(entry, RoutineSyncResultPayload(revision = 4, status = "rate_limited")).second)
        assertEquals(RoutineSyncFollowUp.RETRY_LATER, apply(entry, RoutineSyncResultPayload(revision = 4, status = "internal_error")).second)
        assertEquals(RoutineSyncFollowUp.IGNORED, apply(entry, RoutineSyncResultPayload(revision = 4, status = "from_the_future")).second)
    }

    @Test
    fun `an answer never rewinds the phone's revision, and an old one acks only what it covered`() {
        val entry = RoutineHostSync(localRevision = 9, ackedRevision = 2)
        // A stale answer to an older request while a newer edit is already queued.
        assertEquals(9L, apply(entry, RoutineSyncResultPayload(revision = 3, storedRevision = 5, status = "stale_revision")).first.localRevision)
        val acked = apply(entry, RoutineSyncResultPayload(revision = 5, storedRevision = 5, status = "ok")).first
        assertEquals(5L, acked.ackedRevision)
        assertEquals(42L, acked.historyAsOfUnixMs)
        // The PC holds more than this phone ever wrote (the phone store was reset): jump past it.
        val (reset, followUp) = apply(RoutineHostSync(localRevision = 1), RoutineSyncResultPayload(revision = 12, storedRevision = 12, status = "ok", unsolicited = true))
        assertEquals(13L, reset.localRevision)
        assertEquals(RoutineSyncFollowUp.RESEND, followUp)
    }

    @Test
    fun `set-level state carries pause, block and owner absence`() {
        val result =
            RoutineSyncResultPayload(revision = 3, storedRevision = 3, status = "ok", hostPaused = true, ownerPaused = true, ownerSuspended = "owner_absent")
        val entry = apply(RoutineHostSync(localRevision = 3), result).first
        val pc = RoutineSyncStates.pc(HOST, entry)
        assertTrue(pc.hostPaused)
        assertTrue(pc.ownerPaused)
        assertTrue(pc.ownerSuspended)
        assertFalse(pc.blocked)
        assertFalse(pc.pending)
        // No idle source reported: the idle trigger is unavailable on this PC; before any answer it is unknown.
        assertEquals(false, RoutineSyncStates.triggerAvailable("pc.idle", pc))
        assertNull(RoutineSyncStates.triggerAvailable("pc.idle", RoutineSyncStates.pc(HOST, null)))
    }

    @Test
    fun `a phone routine has no sync state, and a routine not in the answer is still pending`() {
        val entry =
            apply(
                RoutineHostSync(localRevision = 1),
                RoutineSyncResultPayload(revision = 1, storedRevision = 1, status = "ok", results = listOf(RoutineSyncItemResult("other", accepted = true))),
            ).first
        assertNull(RoutineSyncStates.routine(manualRoutine(lock()), entry))
        assertEquals(RoutineSyncState.PENDING, RoutineSyncStates.routine(pcIdleRoutine(), entry)?.state)
    }

    @Test
    fun `the store keeps the As of time across a write`() {
        val doc = RoutineStoreDocument(hostSync = mapOf(HOST to RoutineHostSync(localRevision = 2, historyAsOfUnixMs = 1234L)))
        assertEquals(1234L, RoutineStoreCodec.decode(RoutineStoreCodec.encode(doc))?.hostSync?.get(HOST)?.historyAsOfUnixMs)
    }

    @Test
    fun `PC run pages merge with the on-PC origin, advance the cursor and the As of time`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)
            val run = RoutineRun(runId = uuid(), seq = 4, routineId = id, hostIdentity = HOST, origin = "phone", outcome = "succeeded", triggeredAtUnixMs = h.clock.now)
            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(run)))
            val stored = repo.history.value.single()
            assertEquals("a PC report is always a PC run", "pc", stored.origin)
            assertEquals(4L, repo.hostSync.value[HOST]?.runCursor)
            assertEquals(h.clock.now, repo.hostSync.value[HOST]?.historyAsOfUnixMs)

            // A final record never goes back to running on a late live update.
            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(run.copy(outcome = "running")), live = true))
            assertEquals("succeeded", repo.history.value.single().outcome)
        }

    @Test
    fun `a PC run whose final report never came stops counting as running`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)
            val live = RoutineRun(runId = uuid(), routineId = id, hostIdentity = HOST, outcome = "running", startedAtUnixMs = h.clock.now)
            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(live), live = true))
            assertEquals(1, repo.pcActiveRuns.value.size)
            h.clock.now += 60L * 60L * 1000L
            repo.applyRunReport(RoutineRunReportPayload(runs = listOf(RoutineRun(runId = uuid(), routineId = "gone", hostIdentity = HOST, outcome = "failed")), live = true))
            assertTrue(repo.pcActiveRuns.value.isEmpty())
        }
}
