package com.clindsay94.remex.ui.screens

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * Tracks the phone-side wait for a full-browse consent answer (RemEx-c7v4n).
 *
 * The PC shows the approval prompt; the phone that asked for it has nothing to look at until either a
 * `file_volumes_response` arrives or the wait gives up. [pending] is what the browse UI reads to
 * render that wait honestly instead of leaving the screen looking merely idle or, worse, broken.
 *
 * EVERY WAIT THIS STARTS MUST END. [awaitResponse] races three ways out:
 *  - [onAnswered] is called (the host replied — approved, refused, or a request-level failure; the
 *    caller classifies which via `FileManagerLogic.classifyVolumesResponse`),
 *  - [disconnected] emits `false` (the transport dropped while the question was outstanding), or
 *  - [timeoutMs] elapses with neither of the above (the host never answered at all).
 *
 * `remex.agent/Services/FileTransfer/FileTrustService.cs:36` sets the host's `DefaultConsentTimeout`
 * to 60s before its own auto-deny fires and a `file_volumes_response` comes back, so [timeoutMs]
 * defaults to that plus slack for the round trip — grep that line if the host-side constant ever
 * moves. Firing it is a genuinely separate fact from a wire `REFUSED` answer — "never answered" is
 * not "said no" — so it is reported to a distinct outcome, never folded into one of the wire-classified
 * ones.
 *
 * NOT THREAD-SAFE BY DESIGN. [current] is a plain, non-volatile `var`: correct only because every
 * caller — [FileTransferViewModel]'s `Dispatchers.Main.immediate`-confined `viewModelScope`, both for
 * the [awaitResponse] launch and for the `handleFileTransferMessage` collector that calls [onAnswered]
 * (`FileTransferViewModel.kt:256`) — runs this on the main thread. `Main.immediate` also means the
 * [awaitResponse] call and the `_pending.value = true` write happen inline, before the caller's next
 * line, not on some later dispatch. Calling either method off the main thread breaks both the
 * happens-before guarantee [current] relies on and [pending]'s visibility to Compose.
 */
class VolumesWaitTracker(private val timeoutMs: Long = DEFAULT_TIMEOUT_MS) {

    enum class VolumesWaitOutcome {
        /** A `file_volumes_response` arrived. Caller classifies granted/refused/failed from its body. */
        ANSWERED,

        /** [disconnected] reported the transport was (or went) down while the request was outstanding. */
        DISCONNECTED,

        /** Neither of the above happened within [timeoutMs]. The host never answered at all. */
        TIMED_OUT,
    }

    private val _pending = MutableStateFlow(false)
    val pending: StateFlow<Boolean> = _pending.asStateFlow()

    private var current: CompletableDeferred<VolumesWaitOutcome>? = null

    /**
     * Call once, right after the `file_volumes_request` is sent. Suspends until the wait ends one of
     * the three [VolumesWaitOutcome] ways, then always clears [pending] before returning.
     *
     * Not reentrant: callers must not invoke this again while a previous call on the same instance is
     * still suspended. [FileTransferViewModel.loadVolumes] enforces that by checking [pending] first.
     */
    suspend fun awaitResponse(disconnected: Flow<Boolean>): VolumesWaitOutcome = coroutineScope {
        val deferred = CompletableDeferred<VolumesWaitOutcome>()
        current = deferred
        _pending.value = true
        val timeoutJob = launch {
            delay(timeoutMs)
            deferred.complete(VolumesWaitOutcome.TIMED_OUT)
        }
        val disconnectJob = launch {
            // Fires immediately if already disconnected at subscribe time — correct: there is nothing
            // to wait for in that case.
            disconnected.first { !it }
            deferred.complete(VolumesWaitOutcome.DISCONNECTED)
        }
        try {
            deferred.await()
        } finally {
            timeoutJob.cancel()
            disconnectJob.cancel()
            if (current === deferred) current = null
            _pending.value = false
        }
    }

    /** Call when a `file_volumes_response` arrives, however it later classifies. No-op if nothing is waiting. */
    fun onAnswered() {
        current?.complete(VolumesWaitOutcome.ANSWERED)
    }

    companion object {
        /** 60s host-side `DefaultConsentTimeout` (`FileTrustService.cs:36`) + slack for the round trip. */
        const val DEFAULT_TIMEOUT_MS = 70_000L
    }
}
