package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineSyncResultPayload
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * A confirmed certificate repair re-keys the PC's routines to its new identity (RemEx-pp0rt.15): the
 * routines and the sync books move in one write, the next sync sends the full set, and nothing is
 * duplicated.
 */
class RoutineRepositoryRekeyTest {
    private val repaired = "fedcba9876543210"

    private fun accepted(revision: Long) = RoutineSyncResultPayload(revision = revision, storedRevision = revision, status = "ok", results = emptyList())

    @Test
    fun `re-key moves the routines and the sync entry and asks for a full resync`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val pcRun = checkNotNull((repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine.id)
            val phoneRun = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            assertTrue(repo.markReachableAway(HOST))
            // The PC has acknowledged everything the phone sent under the old identity.
            repo.applySyncResult(HOST, accepted(repo.hostSync.value.getValue(HOST).localRevision))
            val before = repo.hostSync.value.getValue(HOST)
            assertEquals(before.localRevision, before.ackedRevision)

            assertTrue(repo.rekeyHost(HOST, repaired))

            assertEquals(listOf(repaired, repaired), repo.routines.value.map { it.routine.hostIdentity })
            assertFalse(repo.hasRoutinesFor(HOST))
            assertNull(repo.hostSync.value[HOST])
            val moved = repo.hostSync.value.getValue(repaired)
            assertTrue("revision goes past what the PC holds", moved.localRevision > before.localRevision)
            assertEquals(0L, moved.ackedRevision)
            assertNull(moved.lastResultJson)
            assertEquals(before.reachableAwayAtUnixMs, moved.reachableAwayAtUnixMs)

            // What the next routines_sync carries: the full PC-run set at the new revision.
            val snapshot = checkNotNull(repo.syncSnapshot(repaired))
            assertEquals(moved.localRevision, snapshot.revision)
            assertEquals(listOf(pcRun), snapshot.routines.map { it.id })
            assertTrue(checkNotNull(repo.syncSnapshot(HOST)).routines.isEmpty())

            // Persisted in the same write: a fresh repository over the same stores reads it back.
            val reread = h.repository()
            assertEquals(repaired, reread.routine(pcRun)?.hostIdentity)
            assertEquals(repaired, reread.routine(phoneRun)?.hostIdentity)
            assertNotNull(reread.syncSnapshot(repaired))
        }

    @Test
    fun `re-key never duplicates and leaves other PCs alone`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            repo.save(pcIdleRoutine())
            repo.save(manualRoutine(lock()))
            val other = checkNotNull((repo.save(manualRoutine(lock(), name = "Other").copy(hostIdentity = OTHER_HOST)) as RoutineSaveResult.Saved).routine.id)
            val ids = repo.routines.value.map { it.routine.id }

            assertTrue(repo.rekeyHost(HOST, repaired))
            val revision = repo.hostSync.value.getValue(repaired).localRevision
            // A second call has nothing left to move and changes nothing.
            assertTrue(repo.rekeyHost(HOST, repaired))

            assertEquals(ids, repo.routines.value.map { it.routine.id })
            assertEquals(OTHER_HOST, repo.routine(other)?.hostIdentity)
            assertEquals(revision, repo.hostSync.value.getValue(repaired).localRevision)
            assertEquals(setOf(repaired), repo.hostSync.value.keys - OTHER_HOST)
        }

    @Test
    fun `an entry already under the new identity is merged into one`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            repo.save(pcIdleRoutine())
            // The re-pair connection may already have made books for the new identity.
            assertTrue(repo.markReachableAway(repaired))
            val old = repo.hostSync.value.getValue(HOST)

            assertTrue(repo.rekeyHost(HOST, repaired))

            assertEquals(setOf(repaired), repo.hostSync.value.keys)
            val merged = repo.hostSync.value.getValue(repaired)
            assertTrue(merged.localRevision > old.localRevision)
            assertNotNull(merged.reachableAwayAtUnixMs)
        }

    @Test
    fun `phone-only routines move without inventing sync books`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val id = checkNotNull((repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine.id)
            assertNull(repo.hostSync.value[HOST])

            assertTrue(repo.rekeyHost(HOST, repaired))

            assertEquals(repaired, repo.routine(id)?.hostIdentity)
            assertTrue(repo.hostSync.value.isEmpty())
        }

    @Test
    fun `blank or equal identities change nothing`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            repo.save(pcIdleRoutine())
            val before = repo.hostSync.value

            assertFalse(repo.rekeyHost(HOST, HOST))
            assertFalse(repo.rekeyHost("", repaired))
            assertFalse(repo.rekeyHost(HOST, " "))

            assertEquals(before, repo.hostSync.value)
            assertEquals(listOf(HOST), repo.routines.value.map { it.routine.hostIdentity })
        }
}
