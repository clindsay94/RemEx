package com.clindsay94.remex.service

import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Per-device file-sharing trust store (plan §2). Mirrors the PC's
 * `Remex.Agent.Services.FileTransfer.FileTrustService` semantics: a full-browse grant and an
 * auto-accept-incoming grant, keyed by the peer device id. On Android the backing store is DataStore
 * (`fileTrust_<deviceId>_fullBrowse` / `_autoAccept`), fronted by [SettingsFileTrustStore]; this
 * interface is the seam that keeps [FileConsentCoordinator] unit-testable without an Android context.
 */
interface FileTrustStore {
    suspend fun isFullBrowseGranted(deviceId: String): Boolean

    suspend fun isAutoAcceptIncoming(deviceId: String): Boolean

    suspend fun setFullBrowseGranted(deviceId: String, granted: Boolean)

    suspend fun setAutoAcceptIncoming(deviceId: String, enabled: Boolean)
}

/**
 * Raises / dismisses the OS-level consent UI for a prompt. In production this is a high-priority
 * notification with Accept/Decline actions (see [FileTransferNotificationManager.showConsentRequest]);
 * a foreground Compose dialog ([com.clindsay94.remex.ui.components.FileConsentDialogHost]) mirrors the
 * same prompt when the app is visible. Both resolve the prompt through [FileConsentManager.resolve].
 */
interface ConsentPrompter {
    fun show(prompt: FileConsentPrompt)

    fun dismiss(consentId: String)
}

/**
 * One outstanding consent prompt raised on this (serving) device. [kind] is a wire value from
 * [FileConsentKinds] ("full_browse" / "incoming_push"); [detail] is the human-readable file summary
 * for an incoming push. [expiresAtUnixMs] drives the dialog countdown; the authoritative timeout is
 * enforced by [FileConsentCoordinator].
 */
data class FileConsentPrompt(
    val consentId: String,
    val deviceId: String,
    val kind: String,
    val detail: String?,
    val expiresAtUnixMs: Long,
)

/** The user's consent decision. [remember] persists the grant so future requests auto-accept. */
data class FileConsentDecision(val granted: Boolean, val remember: Boolean)

/**
 * Serving-side consent flow for sensitive file-sharing access (plan §2, WP9). This is the Android
 * mirror of the PC's `FileTrustService.RequestConsentAsync`:
 *  - a remembered grant (auto-accept / full-browse already granted for the device) accepts immediately
 *    with no prompt;
 *  - otherwise a prompt is raised via [ConsentPrompter] and held until the user resolves it or the
 *    [timeoutMs] window elapses into a clean deny (default 60 s);
 *  - an accepted decision with `remember` persists the matching grant.
 *
 * Pure logic over injected seams ([FileTrustStore], [ConsentPrompter], a clock) so both the trust-state
 * and timeout behaviour are unit-testable without an Android context. The Android wiring
 * (notifications, DataStore, dialog) lives in [FileConsentManager].
 */
class FileConsentCoordinator(
    private val trustStore: FileTrustStore,
    private val prompter: ConsentPrompter,
    private val timeoutMs: Long = DEFAULT_TIMEOUT_MS,
    private val now: () -> Long = System::currentTimeMillis,
) {
    private val pending = ConcurrentHashMap<String, CompletableDeferred<FileConsentDecision>>()

    /**
     * Requests consent for [kind] access from [deviceId]. Returns immediately with an accept when the
     * device already holds the matching grant; otherwise raises a prompt and suspends until the user
     * resolves it or the timeout auto-denies.
     */
    suspend fun requestConsent(
        deviceId: String,
        kind: String,
        detail: String? = null,
    ): FileConsentDecision {
        // A remembered grant already covers this kind of access → immediate accept, no prompt.
        if (isAlreadyGranted(deviceId, kind)) return FileConsentDecision(granted = true, remember = true)

        val consentId = UUID.randomUUID().toString().replace("-", "")
        val deferred = CompletableDeferred<FileConsentDecision>()
        pending[consentId] = deferred

        val prompt =
            FileConsentPrompt(
                consentId = consentId,
                deviceId = deviceId,
                kind = kind,
                detail = detail,
                expiresAtUnixMs = now() + timeoutMs,
            )

        val decision =
            try {
                prompter.show(prompt)
                // 60-second (or injected) window with no user response → clean deny.
                withTimeoutOrNull(timeoutMs) { deferred.await() }
                    ?: FileConsentDecision(granted = false, remember = false)
            } finally {
                pending.remove(consentId)
                prompter.dismiss(consentId)
            }

        if (decision.granted && decision.remember) persistGrant(deviceId, kind)
        return decision
    }

    /**
     * Resolves an outstanding prompt (from the notification actions or the foreground dialog). Keyed
     * solely by [consentId]; an unknown / already-resolved id is a no-op, so whichever responder
     * arrives first wins (mirrors the PC's `FileTrustService.ResolveConsent`).
     */
    fun resolve(consentId: String, granted: Boolean, remember: Boolean) {
        pending[consentId]?.complete(FileConsentDecision(granted, remember))
    }

    private suspend fun isAlreadyGranted(deviceId: String, kind: String): Boolean =
        when (kind) {
            FileConsentKinds.FULL_BROWSE -> trustStore.isFullBrowseGranted(deviceId)
            FileConsentKinds.INCOMING_PUSH -> trustStore.isAutoAcceptIncoming(deviceId)
            else -> false
        }

    private suspend fun persistGrant(deviceId: String, kind: String) {
        when (kind) {
            FileConsentKinds.FULL_BROWSE -> trustStore.setFullBrowseGranted(deviceId, true)
            FileConsentKinds.INCOMING_PUSH -> trustStore.setAutoAcceptIncoming(deviceId, true)
        }
    }

    companion object {
        /** Wall-clock time an unanswered prompt waits before auto-denying (plan §2). */
        const val DEFAULT_TIMEOUT_MS = 60_000L
    }
}
