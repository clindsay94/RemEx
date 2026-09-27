package com.clindsay94.remex.routines.manual

import android.content.Context
import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.RoutineRepository
import com.clindsay94.remex.routines.RoutineRunObserver
import com.clindsay94.remex.routines.RoutineStoreHealth
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.widget.RoutineWidget
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.launch

/** Several run observers as one; each is isolated from the others' failures. */
internal class CompositeRoutineRunObserver(private vararg val observers: RoutineRunObserver) : RoutineRunObserver {
    override fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?) = each { it.onProgress(run, routine, stepIndex) }

    override fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long) =
        each { it.onCountdown(run, routine, stepIndex, endsAtUnixMs) }

    override fun onFinished(run: RoutineRun, routine: Routine?) = each { it.onFinished(run, routine) }

    private inline fun each(block: (RoutineRunObserver) -> Unit) {
        for (observer in observers) {
            try {
                block(observer)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                RoutineLog.w("A routine run observer failed.", e)
            }
        }
    }
}

/**
 * Keeps the home-screen surfaces in step with the routines (spec §8.3.3, R-UX-25, R-UX-26): the
 * widgets redraw when a run starts or ends, and the shortcuts (dynamic "recently run" list, pinned
 * labels, disabled-when-deleted) follow every run and every edit.
 *
 * Nothing happens until the store has been READ: an empty list before loading must never disable
 * every pinned shortcut as "deleted".
 */
internal class RoutineSurfaces(context: Context) : RoutineRunObserver {
    private val appContext = context.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    @Volatile private var repository: RoutineRepository? = null

    fun watch(repository: RoutineRepository) {
        this.repository = repository
        scope.launch {
            combine(repository.routines, repository.status) { items, status ->
                if (status.health != RoutineStoreHealth.OK) null else items.map { Triple(it.routine.id, it.routine.name, it.routine.trigger?.type to it.routine.steps) }
            }.filterNotNull()
                .distinctUntilChanged()
                .collect { refresh() }
        }
    }

    override fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?) {
        if (stepIndex == null || stepIndex == 0) RoutineWidget.refreshAll(appContext)
    }

    override fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long) = Unit

    override fun onFinished(run: RoutineRun, routine: Routine?) {
        refresh()
    }

    private fun refresh() {
        RoutineWidget.refreshAll(appContext)
        val repo = repository ?: return
        scope.launch {
            if (repo.status.value.health != RoutineStoreHealth.OK) return@launch
            RoutineShortcuts.refresh(appContext, repo.routines.value.map { it.routine }, repo.history.value)
        }
    }
}
