package com.clindsay94.remex

import android.util.Log
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Where [UserDisconnectLatch] keeps its value across process death: DataStore in the app, a fake in tests. */
internal interface UserDisconnectStore {
    suspend fun read(): Boolean

    suspend fun write(disconnected: Boolean)
}

/**
 * Home's Disconnect, held until the person connects again (RemEx-172ba). PERSISTED, NOT PROCESS-ONLY:
 * Android kills a backgrounded app, and the next start (a routine worker, a widget tap, the share
 * sheet) used to come up with the flag cleared and reconnect on its own.
 *
 * Nothing that reconnects may run before [awaitRestored] returns, and a choice made in this process
 * outranks whatever [restore] reads, so a Connect tapped during start-up is never undone by it.
 */
internal class UserDisconnectLatch {
    private val _state = MutableStateFlow(false)
    val state: StateFlow<Boolean> = _state.asStateFlow()

    private val restored = CompletableDeferred<Unit>()
    private val writeLock = Mutex()
    @Volatile private var store: UserDisconnectStore? = null
    @Volatile private var chosenThisProcess = false

    /** Disconnect (true), or a connect the person asked for (false). Call [persist] after. */
    fun choose(disconnected: Boolean) {
        chosenThisProcess = true
        _state.value = disconnected
    }

    /** Reads the saved choice once per process. A failed read fails open, as the gate does elsewhere. */
    suspend fun restore(store: UserDisconnectStore) {
        this.store = store
        try {
            val saved =
                    runCatching { store.read() }
                            .onFailure { Log.w(TAG, "Could not read the saved Disconnect; assuming connected", it) }
                            .getOrDefault(false)
            if (chosenThisProcess) persist() else if (saved) _state.value = true
        } finally {
            restored.complete(Unit)
        }
    }

    suspend fun awaitRestored() = restored.await()

    /** Writes the CURRENT value under a lock, so racing writes always settle on the latest choice. */
    suspend fun persist() {
        val target = store ?: return // restore() writes it once the store is attached
        writeLock.withLock {
            runCatching { target.write(_state.value) }
                    .onFailure { Log.w(TAG, "Could not save the Disconnect choice", it) }
        }
    }

    private companion object {
        const val TAG = "UserDisconnectLatch"
    }
}
