package com.clindsay94.remex.ui.screens

import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.getAndUpdate
import kotlinx.coroutines.flow.update

/**
 * Owns the certificate-change dialog's state and the cancel-is-final guard (RemEx-ivkq).
 *
 * RemEx-vnps' acceptance criterion (4) — cancel clears NOTHING — rests entirely on a requestId
 * comparison: a probe that completes after the user dismissed the dialog must not write its answer
 * back, and must not put the dialog back on screen carrying an answer to a question the user already
 * declined. That invariant lived inside `ConnectionViewModel`, which is an `AndroidViewModel`, so
 * nothing exercised it and a future edit dropping either comparison would have broken it silently.
 *
 * Extracted rather than reached with Robolectric, per Connor's decision on the bead: no permanent new
 * Android test dependency. The split is by what the logic actually needs — this class holds the state,
 * the counter and the guard and touches nothing Android; the view model keeps `Context`,
 * `PinnedHostStore`, `HostCertificateProbe` and `viewModelScope`.
 *
 * DELIBERATELY SYNCHRONOUS. Every method returns having already applied its change, so a JVM test
 * needs no dispatcher, no `runTest` and no virtual time — it can interleave "the user cancels" and
 * "the probe lands" in whatever order it likes and simply read the flow. Making the seam suspend
 * would have re-imported the timing problem the guard exists to solve into the test that checks it.
 */
class CertRepairPromptController {

    private val _prompt = MutableStateFlow<CertRepairPrompt?>(null)

    /** The dialog to show, or null for none. */
    val prompt: StateFlow<CertRepairPrompt?> = _prompt.asStateFlow()

    /**
     * Monotonic, and never reset. Reuse is what the guard is defending against, so an id must not
     * come round again while a probe from its previous life could still be in flight.
     *
     * ATOMIC, THOUGH EVERY CALLER IS CURRENTLY MAIN-CONFINED. A plain `var` would be correct today -
     * the callers are Compose callbacks, and the view model's suspend work resumes on Main - but this
     * class advertises itself as plain Kotlin that touches nothing Android, which reads as an
     * invitation to call it from anywhere. If two `begin` calls ever interleaved, both openings would
     * receive the same id and a stale probe would then pass the comparison and write into the wrong
     * prompt: the exact failure the guard exists to prevent, arriving silently and untestably.
     * AtomicLong costs nothing and removes the constraint rather than documenting it (found in review).
     */
    private val requests = AtomicLong(0L)

    /**
     * Opens a prompt for [hostId] and returns the id that identifies THIS opening, or null when the
     * host is blank and there is nothing to open.
     *
     * The caller must carry the returned id into [applyPinnedPin] and [applyProbeResult]. That is the
     * whole contract: an id captured at open time is the only thing that can tell "my answer" from
     * "an answer to a dialog that has since been dismissed or replaced".
     */
    fun begin(hostId: String): Long? {
        val host = hostId.trim()
        if (host.isEmpty()) return null

        val requestId = requests.incrementAndGet()
        _prompt.value = CertRepairPrompt(hostId = host, requestId = requestId)
        return requestId
    }

    /** Records what this phone pinned at pairing time, if [requestId] still owns the prompt. */
    fun applyPinnedPin(requestId: Long, pinnedPin: String?) {
        _prompt.update { if (it?.requestId == requestId) it.copy(pinnedPin = pinnedPin) else it }
    }

    /**
     * Records what the address presented and marks the probe finished, if [requestId] still owns the
     * prompt.
     *
     * The `else it` branch is the entire point of this class. It covers two cases that look different
     * and are the same mistake: the user dismissed (state is null, so a write would RESURRECT the
     * dialog), and the user opened a second prompt (state belongs to a newer id, so a write would put
     * one host's fingerprint under another host's name).
     */
    fun applyProbeResult(requestId: Long, presentedPin: String?) {
        _prompt.update {
            if (it?.requestId == requestId) {
                it.copy(presentedPin = presentedPin, probeFinished = true)
            } else {
                it
            }
        }
    }

    /** Closes the dialog, changing nothing. Cancel must never cost the user their pin. */
    fun dismiss() {
        _prompt.value = null
    }

    /**
     * Closes the dialog and hands back what it was showing, for a caller that is about to act on it.
     *
     * Clearing before the caller acts is deliberate: the repair is asynchronous, and leaving the
     * dialog up while it runs invites a second confirm on the same prompt.
     */
    fun takeForConfirm(): CertRepairPrompt? {
        // getAndUpdate, not read-then-write: the same reasoning as the counter above. Two confirms
        // racing would otherwise both see the prompt and both act on it.
        return _prompt.getAndUpdate { null }
    }
}
