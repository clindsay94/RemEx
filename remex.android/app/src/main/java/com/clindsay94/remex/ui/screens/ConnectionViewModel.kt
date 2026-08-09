package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.EstablishedConnection
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.CertRepairMigration
import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.data.NsdDiscoveryManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.RemexConnectionService
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONObject
import android.content.Context
import com.clindsay94.remex.security.HostCertificateProbe
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import com.clindsay94.remex.security.SpkiFingerprint

/** What the certificate-change dialog knows about the host it is asking the user about. */
enum class CertRepairState {
    /** The probe has not come back yet. Nothing is offered while this is true. */
    Checking,

    /** The host is presenting a different certificate. This is the case re-pairing is for. */
    Changed,

    /**
     * The host is presenting the certificate this phone already trusts.
     *
     * REACHABLE, AND THE REASON THIS STATE EXISTS. The error card turns on for any failure whose
     * text mentions SSL — a timeout mid-handshake, a proxy resetting the connection — not only a
     * genuine pin mismatch. Offering "clear my pin" for those would make an unrelated network
     * blip a one-tap way to discard the protection, so nothing is offered here.
     */
    Unchanged,

    /** Nothing answered, so the presented certificate could not be read. */
    Unknown
}

/**
 * The state behind the cert-change confirmation dialog (RemEx-vnps).
 *
 * Everything here is derived, so the dialog cannot disagree with itself about whether the
 * certificate changed while showing fingerprints that say otherwise.
 */
data class CertRepairPrompt(
        val hostId: String,
        /** What this phone pinned at pairing time. */
        val pinnedPin: String? = null,
        /** What answered at the address just now, per [HostCertificateProbe]. */
        val presentedPin: String? = null,
        /** False while the probe is still running. */
        val probeFinished: Boolean = false,
        /**
         * Identifies one opening of the dialog, so a probe that finishes after the user cancelled
         * cannot write its answer into a dialog that is gone — or worse, back onto the screen.
         */
        val requestId: Long = 0L
) {
    val state: CertRepairState
        get() =
                when {
                    !probeFinished -> CertRepairState.Checking
                    presentedPin.isNullOrBlank() -> CertRepairState.Unknown
                    SpkiFingerprint.isSameCertificate(pinnedPin, presentedPin) ->
                            CertRepairState.Unchanged
                    else -> CertRepairState.Changed
                }

    /** The fingerprint this phone trusts, ready to render. */
    val pinnedFingerprint: String
        get() = SpkiFingerprint.forDisplay(pinnedPin)

    /** The fingerprint being presented now, ready to render. */
    val presentedFingerprint: String
        get() = SpkiFingerprint.forDisplay(presentedPin)

    /**
     * Whether clearing the pin may be offered at all.
     *
     * FAIL CLOSED IN BOTH DIRECTIONS. While the probe is running there is nothing to decide on, and
     * when the certificate turns out to be unchanged there is nothing to repair — in neither case
     * does the user get a button that discards their pin.
     */
    val canRepair: Boolean
        get() = state == CertRepairState.Changed || state == CertRepairState.Unknown
}

class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val settingsManager = SettingsManager(application)
    private val nsdDiscoveryManager = NsdDiscoveryManager(application)
    private val res = application.resources

    val connectionPreferences: StateFlow<SettingsManager.ConnectionPreferences?> =
            settingsManager.connectionPreferencesFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    null
            )

    val remoteDesktopPreferences: StateFlow<SettingsManager.RemoteDesktopPreferences?> =
            settingsManager.remoteDesktopPreferencesFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    null
            )

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting: StateFlow<Boolean> = _isConnecting.asStateFlow()

    private val _connectionStatus = MutableStateFlow(res.getString(R.string.status_disconnected))
    val connectionStatus: StateFlow<String> = _connectionStatus.asStateFlow()

    private val _connectionError = MutableStateFlow<String?>(null)
    val connectionError: StateFlow<String?> = _connectionError.asStateFlow()

    private val _isCertMismatch = MutableStateFlow(false)
    val isCertMismatch: StateFlow<Boolean> = _isCertMismatch.asStateFlow()

    private val _capabilitySummary =
            MutableStateFlow(res.getString(R.string.status_awaiting_metadata))
    val capabilitySummary: StateFlow<String> = _capabilitySummary.asStateFlow()

    private val _isDiscovering = MutableStateFlow(false)
    val isDiscovering: StateFlow<Boolean> = _isDiscovering.asStateFlow()

    // Tracks the in-flight discovery so a new request cancels the previous one instead of stacking
    // overlapping NSD resolves / multicast-lock cycles (RemEx-4bb). _isDiscovering is set inside the
    // coroutine, so it can't be used as a pre-launch guard without a race; the job handle can.
    private var discoveryJob: Job? = null

    private val _discoveredHost = MutableStateFlow<DiscoveredHost?>(null)
    val discoveredHost: StateFlow<DiscoveredHost?> = _discoveredHost.asStateFlow()

    // The paired-host map is a suspend read of a separate DataStore, not a flow, so it is snapshotted
    // here and refreshed on the events that can change it: a connection, an unpair, a screen resume.
    private val _pairedByAddress = MutableStateFlow<Map<String, String>>(emptyMap())

    /** The Known PCs list: one row per physical PC, most recently connected first (RemEx-k62t). */
    val knownHosts: StateFlow<List<KnownHost>> =
            combine(_pairedByAddress, settingsManager.knownHostRecordsFlow) { paired, records ->
                        KnownHosts.build(paired, records)
                    }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

    init {
        refreshKnownHosts()

        viewModelScope.launch {
            RemexClientManager.isConnecting.collect { connecting ->
                _isConnecting.value = connecting
            }
        }

        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (connected) {
                    // Clear any stale error once a (re)connection succeeds, otherwise the UI
                    // shows "Connected" alongside an outdated error card after auto-reconnect.
                    _connectionError.value = null
                    _isCertMismatch.value = false
                    _connectionStatus.value = res.getString(R.string.status_connected)
                } else if (!_isConnecting.value) {
                    _connectionStatus.value = res.getString(R.string.status_disconnected)
                }
            }
        }

        // Stamped off the PC that connected, NOT off the connected flag — see
        // recordHostAsLastConnected. A new subscriber gets the current value, so a screen created
        // while already connected still stamps.
        viewModelScope.launch {
            RemexClientManager.connectedHost.collect { established ->
                if (established != null) recordHostAsLastConnected(established)
            }
        }

        viewModelScope.launch {
            RemexClientManager.hostCapabilities.collect { hostInfo ->
                _capabilitySummary.value = buildCapabilitySummary(hostInfo)
            }
        }

        viewModelScope.launch {
            RemexClientManager.connectionError.collect { error ->
                _connectionError.value = error
                _isCertMismatch.value = error.contains("SPKI", ignoreCase = true) ||
                                         error.contains("certificate", ignoreCase = true) ||
                                         error.contains("SSL", ignoreCase = true)
                _connectionStatus.value = res.getString(R.string.status_disconnected)
            }
        }
    }

    fun connect(
            newHost: String,
            newPort: Int,
            macAddress: String,
            broadcastIp: String,
            subnetMask: String,
            pairingPin: String,
            desktopQuality: Int,
            desktopTargetFps: Int,
            desktopScale: Float
    ) {
        viewModelScope.launch {
            _connectionError.value = null
            _isCertMismatch.value = false
            _isConnecting.value = true
            settingsManager.saveConnectionSettings(
                    host = newHost,
                    port = newPort,
                    mac = macAddress,
                    broadcast = broadcastIp,
                    subnetMask = subnetMask
            )
            settingsManager.saveRemoteDesktopDefaults(
                    quality = desktopQuality,
                    targetFps = desktopTargetFps,
                    scale = desktopScale
            )

            _connectionStatus.value = res.getString(R.string.status_connecting, newHost, newPort)
            RemexClientManager.toggleConnection(pairingPin.ifBlank { null })

            // Start foreground service to keep connection alive
            try {
                RemexConnectionService.start(getApplication())
            } catch (e: Exception) {
                android.util.Log.w("ConnectionVM", "Foreground service could not be started", e)
            }
        }
    }

    fun updateStatus(isConnected: Boolean) {
        _connectionStatus.value =
                if (isConnected) res.getString(R.string.status_connected)
                else res.getString(R.string.status_disconnected)
    }

    fun clearError() {
        _connectionError.value = null
        _isCertMismatch.value = false
    }

    /**
     * The dialog's state, its request counter and the cancel-is-final guard all live here now
     * (RemEx-ivkq). Extracted so a plain JVM test can drive the guard: this is an AndroidViewModel,
     * so nothing could reach the invariant, and a future edit dropping either requestId comparison
     * would have broken RemEx-vnps' "cancel clears NOTHING" in silence.
     */
    private val certRepairController = CertRepairPromptController()

    /**
     * The certificate repair the user confirmed, waiting for the re-pair to land (RemEx-bye7).
     *
     * Plain state rather than a StateFlow: nothing observes it and no UI shows it.
     *
     * WHAT THE THREADING ACTUALLY GUARANTEES, stated carefully because the first version of this
     * comment overclaimed it. Writes and reads are all on Main (viewModelScope), and the read and
     * the clear in [recordHostAsLastConnected] have no suspension between them, so at most one
     * coroutine can ever see a non-null value and it cannot be migrated twice. What is NOT
     * guaranteed is ORDER: that coroutine suspends in `PinnedHostStore.getPin` before reaching
     * here, so two connections close together resume in IO-completion order rather than emission
     * order. That is exactly why the clear is conditional on the host matching — an out-of-order
     * arrival now passes through instead of consuming a repair meant for another PC.
     *
     * Lost on process death, which is the right failure: the migration only makes sense for a
     * re-pair the user is in the middle of, and inheriting a nickname onto a connection made after a
     * restart would be guessing.
     */
    private var pendingCertRepairMigration: CertRepairMigration? = null

    /** Non-null while the certificate-change dialog is up. */
    val certRepair: StateFlow<CertRepairPrompt?> = certRepairController.prompt

    /**
     * Opens the certificate-change dialog and starts gathering what it shows.
     *
     * **THIS CLEARS NOTHING.** It reads the pinned fingerprint and looks at what the address is
     * presenting; the pin survives until [confirmCertRepair]. That split is the bead: the repair
     * used to be a bare tap that discarded the user's pin on the spot, so someone dismissing an
     * error had already disabled their own protection by the time they understood the message.
     */
    fun beginCertRepair(context: Context, hostId: String, port: Int) {
        // Validation belongs to the controller, which already trims and rejects blank - duplicating
        // it here made that check dead code and left the test covering it unable to fail (review).
        val host = hostId.trim()
        val requestId = certRepairController.begin(host) ?: return

        viewModelScope.launch {
            certRepairController.applyPinnedPin(requestId, PinnedHostStore.getPin(context, host))

            // Not a connection: the probe rejects every certificate it is shown and only reports
            // what it saw. See HostCertificateProbe.
            //
            // Carrying requestId through both calls is what makes cancel final. The guard itself now
            // lives in CertRepairPromptController, where a test can reach it (RemEx-ivkq).
            certRepairController.applyProbeResult(requestId, HostCertificateProbe.observedPin(host, port))
        }
    }

    /** Closes the dialog, changing nothing. Cancel must never cost the user their pin. */
    fun dismissCertRepair() {
        certRepairController.dismiss()
    }

    /**
     * The user read the comparison and accepted the new certificate: forget this PC and pair again.
     *
     * Re-checks [CertRepairPrompt.canRepair] rather than trusting the screen not to have offered
     * the button. The UI hides it, but the decision to discard a certificate pin should not depend
     * on a composable's `if`.
     */
    fun confirmCertRepair(context: Context) {
        val prompt = certRepairController.takeForConfirm() ?: return
        if (!prompt.canRepair) return

        // CAPTURED BEFORE THE PIN IS DESTROYED, because the old identity is derived FROM the pin and
        // is unrecoverable a line later (RemEx-bye7). Accepting this dialog is the user asserting the
        // PC is the same machine with a new certificate, and it is the only signal that says so — the
        // address cannot, since re-pairing a different computer at an address an old one used is an
        // ordinary new pairing. Consumed by recordHostAsLastConnected once a connection actually
        // succeeds; abandoning the re-pair leaves it unused and migrates nothing.
        pendingCertRepairMigration =
                HostIdentity.keyFor(prompt.pinnedPin)?.let {
                    CertRepairMigration(host = prompt.hostId, oldIdentity = it)
                }

        viewModelScope.launch {
            // Both, not just the pin: a stale reconnect secret makes the very next connect fail the
            // proof-of-possession challenge, so "repair" would leave the user exactly as stuck
            // (RemEx-j9ei).
            PinnedHostStore.forgetHost(context, prompt.hostId)
            _isCertMismatch.value = false
            _connectionError.value = null
            // That address may have been one of a Known PCs row's; the list is a snapshot and would
            // otherwise keep offering a PC this phone no longer trusts.
            _pairedByAddress.value = PinnedHostStore.listPaired(getApplication())

            // Into a fresh pairing flow, which is the half of "repair" that was missing: with the
            // pin gone, connect() finds nothing pinned and emits pairingRequired, and AppNavigation
            // walks the user to the PIN screen. No new route — the same one an unpaired host takes.
            RemexClientManager.toggleConnection()
        }
    }

    fun consumeDiscoveredHost() {
        _discoveredHost.value = null
    }

    /** Re-reads the pinned-host store behind the Known PCs list. */
    fun refreshKnownHosts() {
        viewModelScope.launch {
            _pairedByAddress.value = PinnedHostStore.listPaired(getApplication())
        }
    }

    /**
     * Connects to a row in the Known PCs list.
     *
     * Goes through [connect] rather than straight to the client, so the tapped PC also becomes the
     * stored host. That is what re-points everything else that reads `hostFlow` — the Quick Settings
     * tiles, the keepalive service, the widgets, file transfer — at the PC the user last chose,
     * instead of leaving them aimed at whatever was typed into the form once.
     */
    fun connectToKnownHost(knownHost: KnownHost) {
        val cp = connectionPreferences.value
        val dp = remoteDesktopPreferences.value
        connect(
                newHost = knownHost.preferredAddress,
                newPort = knownHost.port,
                macAddress = cp?.macAddress ?: "",
                broadcastIp = cp?.broadcastIp ?: "255.255.255.255",
                subnetMask = cp?.subnetMask ?: "255.255.255.0",
                // Already paired by definition — a PIN is for a PC this phone has never met, and
                // sending a stale one would fail the pairing exchange instead of reconnecting.
                pairingPin = "",
                desktopQuality = dp?.quality ?: 50,
                desktopTargetFps = dp?.targetFps ?: 120,
                desktopScale = dp?.scale ?: 1.0f
        )
    }

    fun renameKnownHost(identity: String, nickname: String) {
        viewModelScope.launch { settingsManager.setKnownHostNickname(identity, nickname) }
    }

    /**
     * Unpairs one PC: every address it answers at, plus what the app remembered about it.
     *
     * ALL of its addresses, not just the one displayed. The row is one machine reached several ways
     * and the user asked to forget the machine; leaving the Tailscale pin behind would make the same
     * PC reappear as a second, half-forgotten row.
     */
    fun unpairKnownHost(context: Context, knownHost: KnownHost) {
        viewModelScope.launch {
            for (address in knownHost.addresses) {
                // Pin AND reconnect secret, via forgetHost — a surviving secret fails the next
                // proof-of-possession challenge and leaves the user stuck (RemEx-j9ei).
                PinnedHostStore.forgetHost(context, address)
            }
            settingsManager.forgetKnownHost(knownHost.identity)
            _pairedByAddress.value = PinnedHostStore.listPaired(getApplication())
        }
    }

    /**
     * Stamps the PC that just connected as the most recently used one.
     *
     * Keyed off the pin rather than the address, so the same machine reached over Tailscale today
     * and the LAN tomorrow keeps one timestamp instead of two. A host with no pin is skipped: it has
     * not finished pairing, so it has no identity to stamp.
     *
     * Driven by [RemexClientManager.connectedHost] rather than by the false-to-true edge of the
     * connected flag, and takes the PC from that event rather than reading it back out of settings.
     * Both halves of that are the fix for RemEx-bz9t:
     *
     * The edge was never safe to rely on. A host switch does reach the native client as a real
     * disconnect — `RemexNativeClient.ConnectAsync` awaits `DisconnectAsync` before it opens the new
     * socket, and `DisconnectAsync` raises `ConnectionStateChanged(false)` on its way out whether or
     * not anything was open — so the false is always SENT. But a StateFlow only guarantees the latest
     * value, and drops one that equals the last it delivered: a collector not scheduled between the
     * false and the true (the main thread is busy recomposing the screen the user just tapped, which
     * is exactly when a switch happens) sees `true` following `true` and never re-fires. The PC the
     * user is now sitting on would keep `lastConnectedAtMillis = 0`, read "Not connected yet", and
     * never become the most-recently-used default. Keying the value on the host and an incrementing
     * epoch removes the possibility rather than shortening the window: consecutive establishments
     * are never equal, so a slow collector can fall behind but cannot mistake one for another.
     *
     * Taking the host from the event closes the other half. The original stamped
     * `settingsManager.hostFlow`, which `connect` writes BEFORE it attempts the connection — so a
     * settings read at stamp time names the PC the user asked for, not the one that answered, and
     * on a switch it is briefly the wrong one. Stamping on a host change alone was rejected in
     * RemEx-k62t for exactly that reason: a missing stamp understates, but a false one corrupts the
     * ordering this whole list is sorted by. An event that arrives only on an established connection
     * and carries its own host has neither failure.
     */
    private fun recordHostAsLastConnected(connection: EstablishedConnection) {
        // Read before the launch. The pin lookup below suspends, so two connections close together
        // could otherwise reach the clock out of order and hand the earlier PC the later stamp —
        // inverting the one thing this list is sorted by.
        val connectedAtMillis = System.currentTimeMillis()
        viewModelScope.launch {
            val host = connection.host
            if (host.isBlank()) return@launch
            val identity =
                    HostIdentity.keyFor(PinnedHostStore.getPin(getApplication(), host))
                            ?: return@launch

            // THE CERTIFICATE-CHANGE INHERITANCE, and it happens BEFORE the record is written
            // (RemEx-bye7). The migration ends by forgetting the source row, so running it after
            // would delete the address, port and timestamp this connection just stored — the new row
            // would keep only the nickname and read "Not connected yet" on the PC being used.
            //
            // CLEARED ONLY WHEN THIS IS THE HOST THE REPAIR WAS FOR, which review caught the first
            // version getting wrong. It took and cleared on every successful connection and only
            // then asked whether to migrate — so confirming a repair for one PC, backing out of the
            // pairing flow it opens, and connecting to another PC first threw the token away, and
            // the eventual re-pair inherited nothing. The pure predicate declined correctly and the
            // thing it declined on was already gone.
            //
            // Taken rather than read once it does match: a repair is confirmed once and inherited
            // once. Leaving it set would re-run the move on every later reconnect — harmless the
            // second time and wrong if the user renamed the row in between.
            val migration = pendingCertRepairMigration
            if (KnownHosts.isAwaitingRepairOn(migration, host)) {
                pendingCertRepairMigration = null
                KnownHosts.identityToMigrateFrom(migration, host, identity)?.let { oldIdentity ->
                    settingsManager.migrateKnownHostIdentity(oldIdentity, identity)
                }
            }

            settingsManager.recordKnownHostConnection(
                    identity = identity,
                    address = host,
                    port = connection.port,
                    atMillis = connectedAtMillis
            )
            _pairedByAddress.value = PinnedHostStore.listPaired(getApplication())
        }
    }

    fun discoverHost() {
        // Don't run local-network discovery while a connection is already established — it's
        // unnecessary and, over a VPN such as Tailscale, only re-triggers Android's local-network
        // permission prompt on top of the active stream. (RemEx-fkz)
        if (RemexClientManager.isConnected.value) return
        // Cancel any still-running discovery before relaunching so overlapping manual + self-heal
        // calls don't stack NSD resolves or multicast-lock cycles (RemEx-4bb). One in-flight at a time.
        discoveryJob?.cancel()
        discoveryJob = viewModelScope.launch {
            _isDiscovering.value = true
            _discoveredHost.value = null
            _connectionError.value = null
            try {
                val result = nsdDiscoveryManager.discoverHost()
                _discoveredHost.value = result
                if (result == null) {
                    _connectionError.value = res.getString(R.string.error_no_host_found)
                }
            } catch (e: Exception) {
                _connectionError.value =
                        res.getString(R.string.error_discovery_failed, e.message ?: "")
            } finally {
                _isDiscovering.value = false
            }
        }
    }

    fun applyQrResultAndConnect(host: String, port: Int, _pin: String) {
        val cp = connectionPreferences.value
        val dp = remoteDesktopPreferences.value
        connect(
                newHost = host,
                newPort = port,
                macAddress = cp?.macAddress ?: "",
                broadcastIp = cp?.broadcastIp ?: "255.255.255.255",
                subnetMask = cp?.subnetMask ?: "255.255.255.0",
                pairingPin = _pin,
                desktopQuality = dp?.quality ?: 50,
                desktopTargetFps = dp?.targetFps ?: 120,
                desktopScale = dp?.scale ?: 1.0f
        )
    }

    private fun buildCapabilitySummary(hostInfo: String): String {
        return try {
            val json = JSONObject(hostInfo)
            val runtimeMode = json.optString("runtimeMode", "unknown")
            val platform = json.optString("platform", "unknown")
            val supportsRemoteDesktop = json.optBoolean("supportsRemoteDesktop", false)
            val remoteDesktopText =
                    if (supportsRemoteDesktop) {
                        res.getString(R.string.capability_desktop_available)
                    } else {
                        json.optString(
                                "remoteDesktopUnavailableReason",
                                res.getString(R.string.capability_desktop_unavailable)
                        )
                    }

            "$platform / $runtimeMode / $remoteDesktopText"
        } catch (_: Exception) {
            res.getString(R.string.status_metadata_unavailable)
        }
    }
}
