package com.clindsay94.remex.service

import android.content.Context
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first

/**
 * Stable identity of the paired peer PC as seen from the Android (client) side. RemEx connects to a
 * single configured host, keyed by its address exactly as [com.clindsay94.remex.security.PinnedHostStore]
 * keys its SPKI pin, so the host address is the natural per-device trust id. Falls back to a constant
 * when the host is not yet configured so trust keys are never blank.
 */
object FilePeerIdentity {
    const val DEFAULT_PEER_DEVICE_ID = "pc"

    fun deviceId(host: String?): String = host?.takeIf { it.isNotBlank() } ?: DEFAULT_PEER_DEVICE_ID
}

/**
 * DataStore-backed [FileTrustStore] (plan §2). Reads/writes the per-device
 * `fileTrust_<deviceId>_fullBrowse` / `_autoAccept` keys through [SettingsManager], mirroring the
 * `sharedFolderUrisFlow` persistence pattern. The full-browse SAF root URI is set/cleared in lock-step
 * with the full-browse grant by the settings toggle, so the host-side `isFullBrowseGranted()` check
 * (SAF root present) and this trust record stay consistent.
 */
class SettingsFileTrustStore(private val settings: SettingsManager) : FileTrustStore {
    override suspend fun isFullBrowseGranted(deviceId: String): Boolean =
        settings.fileTrustFullBrowseFlow(deviceId).first()

    override suspend fun isAutoAcceptIncoming(deviceId: String): Boolean =
        settings.fileTrustAutoAcceptFlow(deviceId).first()

    override suspend fun setFullBrowseGranted(deviceId: String, granted: Boolean) =
        settings.setFileTrustFullBrowse(deviceId, granted)

    override suspend fun setAutoAcceptIncoming(deviceId: String, enabled: Boolean) =
        settings.setFileTrustAutoAccept(deviceId, enabled)
}

/**
 * Process-scoped façade that wires the testable [FileConsentCoordinator] to Android (plan WP9): a
 * DataStore trust store, a notification-backed [ConsentPrompter], and an [activePrompt] flow the
 * foreground Compose dialog observes. The serving-side host mirror ([AndroidFileTransferHost]) calls
 * [requestConsent] before honouring a sensitive request; the notification receiver and the dialog both
 * call [resolve].
 */
object FileConsentManager {

    // Single UI source of truth for the foreground dialog; the notification is a parallel channel. The
    // production prompter drives this flow, so the dialog reflects exactly what was raised.
    private val _activePrompt = MutableStateFlow<FileConsentPrompt?>(null)
    val activePrompt: StateFlow<FileConsentPrompt?> = _activePrompt.asStateFlow()

    private var appContext: Context? = null
    private var coordinator: FileConsentCoordinator? = null

    /** Idempotently wires the coordinator to the application context. Safe to call on every host start. */
    @Synchronized
    fun start(context: Context) {
        if (coordinator != null) return
        val ctx = context.applicationContext
        appContext = ctx
        val store = SettingsFileTrustStore(SettingsManager(ctx))
        val prompter =
            object : ConsentPrompter {
                override fun show(prompt: FileConsentPrompt) {
                    _activePrompt.value = prompt
                    FileTransferNotificationManager.showConsentRequest(ctx, prompt)
                }

                override fun dismiss(consentId: String) {
                    if (_activePrompt.value?.consentId == consentId) _activePrompt.value = null
                    FileTransferNotificationManager.cancelConsent(ctx, consentId)
                }
            }
        coordinator = FileConsentCoordinator(store, prompter)
    }

    /**
     * Requests consent for [kind] access from [deviceId]. Returns a clean deny when the manager has not
     * been started yet (no serving context) rather than blocking forever.
     */
    suspend fun requestConsent(
        deviceId: String,
        kind: String,
        detail: String? = null,
    ): FileConsentDecision =
        coordinator?.requestConsent(deviceId, kind, detail)
            ?: FileConsentDecision(granted = false, remember = false)

    /** Resolves an outstanding prompt (notification action or dialog button). */
    fun resolve(consentId: String, granted: Boolean, remember: Boolean) {
        coordinator?.resolve(consentId, granted, remember)
    }
}
