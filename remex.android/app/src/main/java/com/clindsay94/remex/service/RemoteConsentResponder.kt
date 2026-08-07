package com.clindsay94.remex.service

import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Sends this device's answer to a routed consent prompt back to the serving device
 * (`file_consent_response`). A seam so [RemoteConsentResponder] stays free of Android and JSON and can
 * be unit-tested; the production implementation lives in [FileConsentManager].
 */
fun interface ConsentResponseSender {
    fun send(consentId: String, granted: Boolean, remember: Boolean)
}

/**
 * Answers a consent question the paired PC routed to this phone (RemEx-vyhm, the phone half of
 * RemEx-220r).
 *
 * The mirror image of [FileConsentCoordinator]: there, this phone owns the files and decides locally;
 * here the PC owns them and asks the phone user — because the phone user is the one who made the
 * request, and a dialog on a monitor in another room waits in front of nobody until it times out.
 * The host's `ConsentRoutePolicy` picks between the two and only sends a `file_consent_request` to a
 * client that advertised `supportsConsentPrompt`.
 *
 * **FAIL-CLOSED IS THE WHOLE CONTRACT.** A dismissed sheet, an expired one, and a torn-down service
 * all end as a deny or as silence, never as a grant. Silence is safe because the serving device runs
 * its own clean-deny timeout regardless of what this phone does — which is also why the local window
 * here is a backstop against a sheet that lingers forever, not the authority on the answer.
 */
class RemoteConsentResponder(
    private val prompter: ConsentPrompter,
    private val sender: ConsentResponseSender,
    private val fallbackTimeoutMs: Long = FileConsentCoordinator.DEFAULT_TIMEOUT_MS,
    private val now: () -> Long = System::currentTimeMillis,
) {
    private val pending = ConcurrentHashMap<String, CompletableDeferred<FileConsentDecision>>()

    /**
     * Raises the routed prompt for [consentId] and suspends until the user answers or the window
     * closes, then sends exactly one response.
     *
     * [expiresAtUnixMs] is the serving device's absolute deadline, or null from a host that predates
     * the field — see [plausibleDeadline] for what this device is willing to believe about it.
     */
    suspend fun handle(
        deviceId: String,
        consentId: String,
        kind: String,
        detail: String?,
        expiresAtUnixMs: Long?,
    ) {
        if (consentId.isBlank()) return

        // ONE PROMPT AND ONE ANSWER PER ID. A duplicate request — a host retry, or the same message
        // observed twice — must not raise a second sheet over the first, because dismissing the top one
        // would send a deny for a question the user is still looking at.
        val deferred = CompletableDeferred<FileConsentDecision>()
        if (pending.putIfAbsent(consentId, deferred) != null) return

        val deadline = plausibleDeadline(expiresAtUnixMs, now())
        // No believable deadline → the local backstop. It cannot grant anything the host would not:
        // the host is already counting down on its own clock and ignores an answer that arrives after
        // it gave up (`ConsentRoutePolicy.ShouldApplyResponse` sees no pending prompt).
        val windowMs = deadline?.minus(now()) ?: fallbackTimeoutMs

        val decision =
            try {
                prompter.show(
                    FileConsentPrompt(
                        consentId = consentId,
                        deviceId = deviceId,
                        kind = kind,
                        detail = detail,
                        expiresAtUnixMs = deadline,
                        origin = FileConsentOrigin.PAIRED_PC,
                    )
                )
                withTimeoutOrNull(windowMs) { deferred.await() }
                    ?: FileConsentDecision(granted = false, remember = false)
            } finally {
                pending.remove(consentId)
                prompter.dismiss(consentId)
            }

        sender.send(consentId, decision.granted, decision.remember)
    }

    /**
     * Resolves an outstanding routed prompt (dialog button, notification action, or dismissal). An
     * unknown or already-resolved id is a no-op, so whichever responder arrives first wins — the same
     * rule [FileConsentCoordinator.resolve] follows for locally served prompts.
     */
    fun resolve(consentId: String, granted: Boolean, remember: Boolean) {
        pending[consentId]?.complete(FileConsentDecision(granted, remember))
    }

    companion object {
        /**
         * The longest countdown this device will believe from a serving peer.
         *
         * The host's own window is 60 s; anything approaching this is a clock, not a policy.
         */
        const val MAX_PLAUSIBLE_WINDOW_MS = 5L * 60_000L

        /**
         * The deadline to render, or null for "show none".
         *
         * **THE DELTA IS COMPUTED ACROSS TWO DIFFERENT CLOCKS AND CAN COME BACK ABSURD.** The host
         * sends an absolute instant precisely so arrival delay does not eat into the window, but a
         * phone whose wall clock is wrong turns that into a countdown that is already negative or
         * runs for an hour. Neither is information, and both are worse than no countdown: one tells
         * the user the request is dead when it is live, the other tells them they have time they do
         * not have. Null is honest — the serving device is the one that denies, and it is denying on
         * schedule either way.
         */
        fun plausibleDeadline(expiresAtUnixMs: Long?, now: Long): Long? {
            val remaining = (expiresAtUnixMs ?: return null) - now
            if (remaining <= 0L || remaining > MAX_PLAUSIBLE_WINDOW_MS) return null
            return expiresAtUnixMs
        }
    }
}
