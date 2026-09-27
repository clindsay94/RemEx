package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.Home
import com.clindsay94.remex.routines.home.HomeCodec
import com.clindsay94.remex.routines.home.HomeFacts
import com.clindsay94.remex.routines.home.HomeTokenizer
import com.clindsay94.remex.routines.home.PendingFire
import com.clindsay94.remex.routines.home.PendingFires
import com.clindsay94.remex.routines.home.PresenceEvent
import com.clindsay94.remex.routines.home.PresenceFiring
import com.clindsay94.remex.routines.home.PresenceRecord
import com.clindsay94.remex.routines.home.PresenceState
import com.clindsay94.remex.routines.model.RoutineTrigger
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The S3 review's "lost arrive/leave": the starts a transition decides are stored in the same write
 * as the transition, survive a worker that dies before making them, are replayed and made exactly
 * once, and are never honoured once stale.
 */
class PresenceFiringTest {
    private val facts = HomeFacts(listOf("192.168.1.1"), listOf("192.168.1.0/24"), domain = "lan")
    private val home =
        Home("b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11", HOST, 1, facts, HomeTokenizer(ByteArray(32)).tokens(facts), PresenceRecord(PresenceState.AWAY))
    private val arriveRoutine =
        manualRoutine(lock()).copy(trigger = RoutineTrigger(type = "home.arrive", homeId = home.id))

    private suspend fun seeded(h: RepositoryHarness): RoutineRepository {
        val repo = h.repository()
        repo.updateHome { home }
        check(repo.save(arriveRoutine) is RoutineSaveResult.Saved)
        return repo
    }

    @Test
    fun `an arrive owed by a worker that died before firing is replayed once`() =
        runTest {
            val h = RepositoryHarness()
            val repo = seeded(h)
            val now = h.clock.now
            val owed = PendingFires.of(listOf(PresenceEvent.Arrive(false)), listOf(arriveRoutine.id!!), "home.arrive", "home.leave", now)
            // The transition and the owed start land in ONE write; then the worker is "cancelled".
            assertTrue(PresenceFiring.record(repo, home.id, PresenceRecord(PresenceState.HOME, now, now, pendingFire = owed)))
            assertEquals(0, h.scheduler.enqueued.size)

            // A later evaluation, in a fresh process, finds it still owed and makes it.
            val later = h.repository().also { it.load() }
            assertEquals(owed, later.home.value?.presence?.pendingFire)
            assertEquals(1, PresenceFiring.fireOwed(later, home.id, now + 5_000))
            assertEquals(1, h.scheduler.enqueued.size)
            assertEquals("home.arrive", h.scheduler.enqueued.single().source)
            assertTrue(later.home.value?.presence?.pendingFire.orEmpty().isEmpty())

            // Replaying again makes nothing.
            assertEquals(0, PresenceFiring.fireOwed(h.repository().also { it.load() }, home.id, now + 6_000))
            assertEquals(1, h.scheduler.enqueued.size)
        }

    @Test
    fun `a stale owed start is dropped, not run`() =
        runTest {
            val h = RepositoryHarness()
            val repo = seeded(h)
            val decided = h.clock.now - PendingFires.MAX_AGE_MS - 1
            PresenceFiring.record(repo, home.id, PresenceRecord(PresenceState.HOME, pendingFire = listOf(PendingFire(arriveRoutine.id!!, "home.arrive", false, decided))))
            assertEquals(0, PresenceFiring.fireOwed(repo, home.id, h.clock.now))
            assertEquals(0, h.scheduler.enqueued.size)
            assertTrue(repo.home.value?.presence?.pendingFire.orEmpty().isEmpty())
        }

    @Test
    fun `a suppressed start is recorded as flap_suppressed and runs nothing`() =
        runTest {
            val h = RepositoryHarness()
            val repo = seeded(h)
            PresenceFiring.record(repo, home.id, PresenceRecord(PresenceState.HOME, pendingFire = listOf(PendingFire(arriveRoutine.id!!, "home.arrive", true, h.clock.now))))
            PresenceFiring.fireOwed(repo, home.id, h.clock.now)
            assertEquals(0, h.scheduler.enqueued.size)
            assertEquals("flap_suppressed", repo.history.value.single().reasonCode)
        }

    @Test
    fun `owed starts merge without duplicates and round-trip through the store`() {
        val a = PendingFire("r1", "home.leave", false, 10)
        val merged = PendingFires.merge(listOf(a), listOf(a.copy(decidedAtUnixMs = 20), PendingFire("r2", "home.leave", false, 20)), 30)
        assertEquals(listOf("r1", "r2"), merged.map { it.routineId })
        val withPending = home.copy(presence = home.presence.copy(pendingFire = merged))
        assertEquals(withPending, HomeCodec.decode(HomeCodec.encode(withPending)))
    }
}
