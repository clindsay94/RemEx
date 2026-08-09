package com.clindsay94.remex.service

import android.content.Context
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import org.json.JSONObject

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
    //
    // BACKED BY A STACK SINCE RemEx-hncl, because two prompts can be outstanding at once now that
    // RemEx-vyhm added the routed direction. The flow still holds exactly one — the dialog can only
    // show one — but dismissing it restores whatever is still waiting instead of clearing to null.
    private val prompts = ConsentPromptStack()
    private val _activePrompt = MutableStateFlow<FileConsentPrompt?>(null)
    val activePrompt: StateFlow<FileConsentPrompt?> = _activePrompt.asStateFlow()

    private var appContext: Context? = null
    private var coordinator: FileConsentCoordinator? = null
    private var remote: RemoteConsentResponder? = null

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
                    _activePrompt.value = prompts.push(prompt)
                    FileTransferNotificationManager.showConsentRequest(ctx, prompt)
                }

                override fun dismiss(consentId: String) {
                    // The one still waiting comes BACK, rather than the dialog clearing to nothing.
                    // Answering the prompt that interrupted you should not silently retire the
                    // question you were already being asked.
                    _activePrompt.value = prompts.remove(consentId)
                    FileTransferNotificationManager.cancelConsent(ctx, consentId)
                }
            }
        coordinator = FileConsentCoordinator(store, prompter)
        // The routed half (RemEx-vyhm) shares the prompter, so a question from the PC reaches the same
        // notification and the same foreground dialog as a locally served one. Only the wording and
        // where the answer goes differ.
        // Named, and not a trailing lambda: the responder's last parameter is its clock, so a trailing
        // lambda binds there instead of to the sender and the answer never reaches the wire.
        remote =
            RemoteConsentResponder(
                prompter = prompter,
                sender = ConsentResponseSender { consentId, granted, remember ->
                    RemexCoreClient.SendMessage(
                        JSONObject()
                            .put("type", "file_consent_response")
                            .put("protocolVersion", 3)
                            .put(
                                "fileConsentResponse",
                                JSONObject()
                                    .put("consentId", consentId)
                                    .put("granted", granted)
                                    .put("remember", remember),
                            )
                            .toString()
                    )
                },
            )
    }

    /**
     * Renders a consent question the paired PC routed to this phone and answers it on the wire
     * (RemEx-vyhm). A no-op before [start], which is a clean deny by omission: with no serving context
     * there is nothing to prompt with, and staying silent lets the host's own timeout decide.
     */
    suspend fun handleRemoteRequest(
        deviceId: String,
        consentId: String,
        kind: String,
        detail: String?,
        expiresAtUnixMs: Long?,
    ) {
        remote?.handle(deviceId, consentId, kind, detail, expiresAtUnixMs)
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

    /**
     * Resolves an outstanding prompt (notification action, dialog button, or dismissal).
     *
     * Offered to BOTH halves rather than routed by inspecting the active prompt: the dialog and the
     * notification receiver know only a consent id, ids from the two halves cannot collide, and each
     * half ignores one it is not holding. Asking the wrong one first is free; guessing wrong would
     * drop a real answer.
     */
    fun resolve(consentId: String, granted: Boolean, remember: Boolean) {
        coordinator?.resolve(consentId, granted, remember)
        remote?.resolve(consentId, granted, remember)
    }
}
