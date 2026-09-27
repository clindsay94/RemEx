package com.clindsay94.remex.routines

import android.content.Context

/**
 * The process-wide routine objects (RemEx-pp0rt.5). One repository per process, because its lock
 * is what makes every store edit and run record a single ordered write, and the notification Cancel,
 * the worker and the UI must all see the same in-flight runs.
 *
 * S1d obtains the UI's entry point with [repository].
 */
object Routines {
    @Volatile private var graph: Graph? = null

    /** The routine repository: flows for the list, history and pause state, plus edit and run calls. */
    fun repository(context: Context): RoutineRepository = graph(context).repository

    internal fun runner(context: Context): RoutineRunner = graph(context).runner

    private fun graph(context: Context): Graph =
        graph ?: synchronized(this) { graph ?: Graph(context.applicationContext).also { graph = it } }

    private class Graph(context: Context) {
        private val cipher = TinkRoutineCipherSource(context)
        private val presenter = RoutineNotificationPresenter(context)
        private val controls = RoutineRunControls()

        val repository =
            RoutineRepository(
                documents = RoutineDocumentStore(DataStoreRoutineKeyValueStore(context.routinesDataStore), cipher),
                historyStore = RoutineHistoryStore(DataStoreRoutineKeyValueStore(context.routineHistoryDataStore), cipher),
                cipherSource = cipher,
                scheduler = WorkManagerRoutineScheduler(context),
                observer = presenter,
                clock = SystemRoutineClock,
                controls = controls,
            )

        val runner =
            RoutineRunner(
                store = repository,
                link = RemexRoutineHostLink(context),
                phone = AndroidRoutinePhone(context, presenter),
                observer = presenter,
                clock = SystemRoutineClock,
                controls = controls,
            )
    }
}
