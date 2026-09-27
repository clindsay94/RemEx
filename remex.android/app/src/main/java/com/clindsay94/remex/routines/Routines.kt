package com.clindsay94.remex.routines

import android.content.Context
import android.widget.Toast
import com.clindsay94.remex.R
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.flow.map

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

    /** The PC-run routine sync client (RemEx-pp0rt.12): Run on PC, forget flush, sync answers. */
    internal fun syncClient(context: Context): RoutineSyncClient = graph(context).syncClient

    /**
     * Forgets [hostIdentity]'s routines before its pins are cleared (§7.4.5, T8). Call it FIRST in
     * every flow that forgets a PC ([com.clindsay94.remex.security.PinnedHostStore.forgetHost]): once
     * the pins are gone the phone can no longer reach the PC to tell it. Never throws.
     */
    suspend fun forgetPc(context: Context, hostIdentity: String?): RoutinePcForget {
        if (hostIdentity.isNullOrBlank()) return RoutinePcForget.NOTHING
        return try {
            syncClient(context).forgetPc(hostIdentity)
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Forgetting a PC's routines failed.", e)
            RoutinePcForget.NOTHING
        }
    }

    /** [forgetPc] for the PC pinned at [address]; reads its identity from the pin before it is cleared. */
    suspend fun forgetPcAt(context: Context, address: String): RoutinePcForget {
        val identity =
            try {
                HostIdentity.keyFor(PinnedHostStore.getPin(context, address))
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                RoutineLog.e("Reading a PC's pin before forgetting it failed.", e)
                null
            }
        return forgetPc(context, identity)
    }

    private fun graph(context: Context): Graph =
        graph ?: synchronized(this) { graph ?: Graph(context.applicationContext).also { graph = it } }

    private class Graph(context: Context) {
        private val cipher = TinkRoutineCipherSource(context)
        private val presenter = RoutineNotificationPresenter(context)
        private val controls = RoutineRunControls()
        private val link = RemexRoutineHostLink(context)

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
                link = link,
                phone = AndroidRoutinePhone(context, presenter),
                observer = presenter,
                clock = SystemRoutineClock,
                controls = controls,
            )

        val syncClient =
            RoutineSyncClient(
                store = repository,
                link = link,
                connections = com.clindsay94.remex.RemexClientManager.authenticatedConnection.map { it?.epoch },
                observer = presenter,
                clock = SystemRoutineClock,
                messages = presenter,
            )
    }
}

/**
 * What the forget-PC flow tells the person about that PC's routines (§7.4.5 "the phone's forget
 * screen states this"): nothing when there were none or the PC deleted them, and the residual when
 * the PC could not be told.
 */
object RoutineForgetNotice {
    fun show(context: Context, outcome: RoutinePcForget, pcName: String?) {
        if (outcome != RoutinePcForget.PHONE_ONLY) return
        val name = pcName?.takeIf { it.isNotBlank() } ?: context.getString(R.string.routine_pc_fallback_name)
        Toast.makeText(context.applicationContext, context.getString(R.string.routines_forget_pc_residual, name), Toast.LENGTH_LONG).show()
    }
}
