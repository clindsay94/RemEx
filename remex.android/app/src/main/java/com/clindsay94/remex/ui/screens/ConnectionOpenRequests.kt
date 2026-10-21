package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * "Open Connection with Add a PC showing" (sweep P3, RemEx-wqo7a.7): the Pair action on Home, Files,
 * Apps and the process list asks for it, then navigates to Connection, which opens the sheet once and
 * consumes the request.
 *
 * A StateFlow, not a one-shot event: the request is made before the Connection route exists, so it
 * has to wait for the screen to compose and read it. [consumeAddPc] clears it so coming back to
 * Connection later never reopens the sheet by itself.
 *
 * **A REQUEST ONLY COUNTS FOR [MAX_AGE_MS].** If the navigation that should follow never lands
 * (the app goes to the background, another route wins), the request must not sit there and pop the
 * sheet open on some unrelated visit to Connection minutes later. An older request is cleared
 * without opening anything. The clock is monotonic ([System.nanoTime]), so a wall-clock change
 * cannot make a stale request look fresh.
 *
 * It also carries "open the PCs page" from outside the app (the share target with nothing paired,
 * RemEx-172ba): MainActivity hands its intent to [offer], and AppNavigation navigates once the
 * splash has passed, the arrangement `SensorAlertOpenRequests` uses.
 */
object ConnectionOpenRequests {
    /** How long a Pair tap may take to reach Connection; a route change takes well under a second. */
    const val MAX_AGE_MS = 5_000L

    const val EXTRA_OPEN_PCS = "com.clindsay94.remex.OPEN_PCS"

    /** When the pending request was made (monotonic ms), or null when there is none. */
    private val _addPc = MutableStateFlow<Long?>(null)
    val addPc: StateFlow<Long?> = _addPc.asStateFlow()

    private val _openPcs = MutableStateFlow(false)
    val openPcs: StateFlow<Boolean> = _openPcs.asStateFlow()

    private fun nowMs(): Long = System.nanoTime() / 1_000_000L

    fun requestAddPc(atMs: Long = nowMs()) {
        _addPc.value = atMs
    }

    /**
     * Clears any pending request and says whether it should open the sheet: true only for one made
     * within the last [MAX_AGE_MS].
     */
    fun consumeAddPc(atMs: Long = nowMs()): Boolean {
        val requestedAt = _addPc.value ?: return false
        _addPc.value = null
        return atMs - requestedAt in 0..MAX_AGE_MS
    }

    /** True when [intent] asked for the PCs page (it is then remembered until [consumeOpenPcs]). */
    fun offer(intent: Intent?): Boolean {
        if (intent == null) return false
        // MainActivity is exported and reading any extra unparcels the whole bundle, which can throw
        // below API 33. Only this one boolean is read, so the intent chooses nothing else.
        val requested =
                try {
                    if (!intent.getBooleanExtra(EXTRA_OPEN_PCS, false)) return false
                    intent.removeExtra(EXTRA_OPEN_PCS)
                    true
                } catch (e: RuntimeException) {
                    Log.w("ConnectionOpenRequests", "Ignoring an intent whose extras could not be read.", e)
                    false
                }
        if (requested) _openPcs.value = true
        return requested
    }

    /** Takes the pending "open the PCs page" request, if any, so it navigates once. */
    fun consumeOpenPcs(): Boolean {
        var was: Boolean
        do {
            was = _openPcs.value
        } while (!_openPcs.compareAndSet(was, false))
        return was
    }
}
