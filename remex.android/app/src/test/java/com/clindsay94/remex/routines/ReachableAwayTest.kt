package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.Home
import com.clindsay94.remex.routines.home.HomeCodec
import com.clindsay94.remex.routines.home.HomeFacts
import com.clindsay94.remex.routines.home.HomeTokenizer
import com.clindsay94.remex.routines.home.PresenceRecord
import com.clindsay94.remex.routines.home.PresenceState
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.ui.routines.EditorEnvironment
import com.clindsay94.remex.ui.routines.EditorProblemCode
import com.clindsay94.remex.ui.routines.RoutineDrafts
import com.clindsay94.remex.ui.routines.RoutineEditorRules
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Spec §13.3 `ReachableAwayTest` (R-SYS-40, D6) and the home store: authenticating away sets
 * `reachableAwayAtUnixMs`, which silences the leave warning and picks `pc_unreachable` over
 * `pc_unreachable_away`; forgetting home leaves the home routines in place but unable to run.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class ReachableAwayTest {
    private val facts = HomeFacts(listOf("192.168.1.1"), listOf("192.168.1.0/24"), listOf("192.168.1.1"), "lan")
    private val home =
        Home(
            id = "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11",
            capturedWithHostIdentity = HOST,
            capturedAtUnixMs = 1_790_000_000_000L,
            facts = facts,
            tokens = HomeTokenizer(ByteArray(32)).tokens(facts),
            presence = PresenceRecord(PresenceState.HOME, 5, 5, awaySinceUnixMs = 7, firedLeave = setOf("x")),
        )

    private fun leaveRoutine(vararg steps: RoutineStep, homeId: String? = home.id) =
        manualRoutine(*steps).copy(trigger = RoutineTrigger(type = "home.leave", homeId = homeId, leaveDebounceSeconds = 180))

    @Test
    fun `authenticating away records reachableAway once`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            assertNull(repo.reachableAwayAtUnixMs(HOST))
            assertTrue(repo.markReachableAway(HOST))
            val first = repo.reachableAwayAtUnixMs(HOST)
            assertEquals(h.clock.now, first)
            h.clock.now += 60_000
            repo.markReachableAway(HOST)
            assertEquals(first, repo.reachableAwayAtUnixMs(HOST))
            // It survives a reload.
            assertEquals(first, h.repository().reachableAwayAtUnixMs(HOST))
        }

    @Test
    fun `away from home, a PC reached from away before fails pc_unreachable, never pc_unreachable_away`() =
        runTest {
            suspend fun failure(reachedAway: Boolean): String? {
                val h = RepositoryHarness(clock = FakeClock(elapsed = { testScheduler.currentTime }))
                val repo = h.repository()
                val link = FakeHostLink().apply { setAuthenticated(false); connectSucceeds = false }
                val phone = FakePhone().apply { away = true }
                if (reachedAway) repo.markReachableAway(HOST)
                val saved = (repo.save(manualRoutine(lock())) as RoutineSaveResult.Saved).routine
                check(repo.run(checkNotNull(saved.id), "manual.app") is RoutineRunStart.Started)
                return RoutineRunner(repo, link, phone, h.observer, h.clock, h.controls).execute(h.scheduler.enqueued.last())?.reasonCode
            }
            assertEquals("pc_unreachable_away", failure(reachedAway = false))
            assertEquals("pc_unreachable", failure(reachedAway = true))
        }

    @Test
    fun `the leave warning shows until the PC has been reached from away`() {
        val draft = RoutineDrafts.fromRoutine(leaveRoutine(lock()))
        val warned = RoutineEditorRules.problems(draft, EditorEnvironment(homeId = home.id, reachableAway = false))
        assertTrue(warned.any { it.code == EditorProblemCode.LEAVE_NEEDS_REACH })
        val quiet = RoutineEditorRules.problems(draft, EditorEnvironment(homeId = home.id, reachableAway = true))
        assertFalse(quiet.any { it.code == EditorProblemCode.LEAVE_NEEDS_REACH })
        // A leave routine with only phone steps needs no PC.
        val phoneOnly = RoutineDrafts.fromRoutine(leaveRoutine(notifyPhone()))
        assertFalse(RoutineEditorRules.problems(phoneOnly, EditorEnvironment(homeId = home.id)).any { it.code == EditorProblemCode.LEAVE_NEEDS_REACH })
    }

    @Test
    fun `a home trigger without the phone's home is an error`() {
        val draft = RoutineDrafts.fromRoutine(leaveRoutine(notifyPhone(), homeId = null))
        assertTrue(RoutineEditorRules.problems(draft, EditorEnvironment(homeId = home.id)).any { it.code == EditorProblemCode.HOME_NOT_SET })
        val bound = RoutineDrafts.fromRoutine(leaveRoutine(notifyPhone()))
        assertFalse(RoutineEditorRules.problems(bound, EditorEnvironment(homeId = home.id)).any { it.code == EditorProblemCode.HOME_NOT_SET })
        assertTrue(RoutineEditorRules.problems(bound, EditorEnvironment(homeId = null)).any { it.code == EditorProblemCode.HOME_NOT_SET })
    }

    @Test
    fun `the home round-trips through the store, and forgetting it strands its routines`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            assertTrue(repo.updateHome { home })
            assertEquals(home, repo.home.value)
            val saved = repo.save(leaveRoutine(notifyPhone()))
            assertTrue(saved is RoutineSaveResult.Saved)
            assertTrue(repo.routines.value.single().verdict.isValid)
            assertEquals(home, h.repository().also { it.load() }.home.value)

            assertTrue(repo.updateHome { null })
            assertNull(repo.home.value)
            val item = repo.routines.value.single()
            assertEquals("home_not_set", item.verdict.reasonCode)
        }

    @Test
    fun `the home codec never guesses`() {
        assertEquals(home, HomeCodec.decode(HomeCodec.encode(home)))
        assertNull(HomeCodec.decode(null))
        assertNull(HomeCodec.decode("{}"))
        assertNull(HomeCodec.decode("not json"))
        val encoded = HomeCodec.encode(home)
        assertFalse("the codec stores tokens beside the facts", !encoded.contains("\"tokens\""))
        assertNotNull(HomeCodec.decode(encoded)?.tokens?.gateways?.singleOrNull())
    }
}
