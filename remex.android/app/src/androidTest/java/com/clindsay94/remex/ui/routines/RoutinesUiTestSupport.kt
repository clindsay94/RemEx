package com.clindsay94.remex.ui.routines

import android.app.Application
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.junit4.AndroidComposeTestRule
import androidx.test.ext.junit.rules.ActivityScenarioRule
import com.clindsay94.remex.routines.RoutineCipher
import com.clindsay94.remex.routines.RoutineCipherSource
import com.clindsay94.remex.routines.RoutineClock
import com.clindsay94.remex.routines.RoutineDocumentStore
import com.clindsay94.remex.routines.RoutineHistoryStore
import com.clindsay94.remex.routines.RoutineKeyValueStore
import com.clindsay94.remex.routines.RoutineRepository
import com.clindsay94.remex.routines.RoutineRunControls
import com.clindsay94.remex.routines.RoutineRunObserver
import com.clindsay94.remex.routines.RoutineRunScheduler
import com.clindsay94.remex.routines.RoutineRunTicket
import com.clindsay94.remex.routines.RoutineSaveResult
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineTrigger
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertTrue

// Hermetic stand-ins for the Routines UI tests (RemEx-pp0rt.13): in-memory stores, a scheduler that
// never starts work, no PC and no network. Mirrors the unit-test fakes in src/test (RoutineTestFakes).

internal const val TEST_HOST = "9f2c4be07a1d33e5"

internal class MemoryKeyValueStore : RoutineKeyValueStore {
    private val map = LinkedHashMap<String, String>()

    override suspend fun get(key: String): String? = synchronized(map) { map[key] }

    override suspend fun getAll(): Map<String, String> = synchronized(map) { LinkedHashMap(map) }

    override suspend fun put(key: String, value: String) {
        synchronized(map) { map[key] = value }
    }

    override suspend fun remove(key: String) {
        synchronized(map) { map.remove(key) }
    }
}

/** Reversible and bound to its associated data, like an AEAD. */
internal class PlainCipherSource : RoutineCipherSource {
    private val cipher =
        object : RoutineCipher {
            override fun seal(plainText: String, associatedData: String): String =
                Base64.getEncoder().encodeToString("$associatedData\u0000$plainText".toByteArray(Charsets.UTF_8))

            override fun open(sealed: String, associatedData: String): String? {
                val text = runCatching { String(Base64.getDecoder().decode(sealed), Charsets.UTF_8) }.getOrNull() ?: return null
                val prefix = "$associatedData\u0000"
                return if (text.startsWith(prefix)) text.removePrefix(prefix) else null
            }
        }

    override suspend fun cipher(): RoutineCipher = cipher

    override suspend fun pendingKeyLossAtUnixMs(): Long? = null

    override suspend fun clearKeyLossMarker() {}
}

/** Accepts nothing, so no test ever starts a run. */
internal object IdleScheduler : RoutineRunScheduler {
    override fun enqueue(ticket: RoutineRunTicket): Boolean = false

    override fun cancel(routineId: String) {}

    override suspend fun isActive(routineId: String): Boolean = false
}

internal object SilentObserver : RoutineRunObserver {
    override fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?) {}

    override fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long) {}

    override fun onFinished(run: RoutineRun, routine: Routine?) {}
}

internal object WallClock : RoutineClock {
    override fun nowUnixMs(): Long = System.currentTimeMillis()

    override fun elapsedRealtimeMs(): Long = android.os.SystemClock.elapsedRealtime()
}

internal fun manualRoutine(vararg steps: RoutineStep, name: String = "Game time"): Routine =
    Routine(
        id = UUID.randomUUID().toString(),
        name = name,
        hostIdentity = TEST_HOST,
        enabled = true,
        trigger = RoutineTrigger(type = "manual"),
        steps = steps.toList(),
    )

internal fun lockStep() = RoutineStep(type = "power", verb = "LOCK")

internal fun shutdownStep() = RoutineStep(type = "power", verb = "SHUTDOWN")

/**
 * Installs a fresh in-memory repository holding [seed] as the process's routine store, then builds the
 * screen's [RoutinesViewModel] over it on the main thread, with the coach marks already seen.
 */
internal fun AndroidComposeTestRule<ActivityScenarioRule<ComponentActivity>, ComponentActivity>.routinesViewModel(
    vararg seed: Routine,
): Pair<RoutinesViewModel, RoutineRepository> {
    val app = activity.application as Application
    val repository =
        RoutineRepository(
            documents = RoutineDocumentStore(MemoryKeyValueStore(), PlainCipherSource()),
            historyStore = RoutineHistoryStore(MemoryKeyValueStore(), PlainCipherSource()),
            cipherSource = PlainCipherSource(),
            scheduler = IdleScheduler,
            observer = SilentObserver,
            clock = WallClock,
            controls = RoutineRunControls(),
        )
    runBlocking {
        repository.load()
        seed.forEach { routine -> assertTrue("seed ${routine.name} saves", repository.save(routine) is RoutineSaveResult.Saved) }
    }
    Routines.installForTest(app, repository)
    lateinit var viewModel: RoutinesViewModel
    runOnUiThread { viewModel = RoutinesViewModel(app) }
    // The coach overlay would sit over the list on a first open; these tests are not about it.
    runOnUiThread { viewModel.setCoachSeen(true) }
    waitUntil(5_000) { viewModel.coachSeen.value == true }
    return viewModel to repository
}
