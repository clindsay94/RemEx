package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
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
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore

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
                    recordCurrentHostAsLastConnected()
                } else if (!_isConnecting.value) {
                    _connectionStatus.value = res.getString(R.string.status_disconnected)
                }
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

    fun clearPinForHost(context: Context, hostId: String) {
        viewModelScope.launch {
            // Both, not just the pin: a stale reconnect secret makes the very next connect fail the
            // proof-of-possession challenge, so "repair" would leave the user exactly as stuck
            // (RemEx-j9ei).
            PinnedHostStore.forgetHost(context, hostId)
            _isCertMismatch.value = false
            _connectionError.value = null
            // That address may have been one of a Known PCs row's; the list is a snapshot and would
            // otherwise keep offering a PC this phone no longer trusts.
            _pairedByAddress.value = PinnedHostStore.listPaired(getApplication())
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
     * Driven by the disconnected-to-connected edge of a StateFlow, which conflates equal values —
     * so a host switch the native client serviced WITHOUT ever reporting a disconnect would leave
     * the new PC unstamped. Stamping on the host changing while still connected would close that,
     * and was rejected: the host is saved BEFORE the connection is attempted, so it would stamp a
     * PC that had not connected yet. A missing stamp understates; a false one corrupts the ordering
     * this whole list is sorted by. Whether that edge actually fires on a switch needs a device to
     * answer (RemEx-bz9t).
     */
    private fun recordCurrentHostAsLastConnected() {
        viewModelScope.launch {
            val host = settingsManager.hostFlow.first()
            if (host.isBlank()) return@launch
            val identity =
                    HostIdentity.keyFor(PinnedHostStore.getPin(getApplication(), host))
                            ?: return@launch
            settingsManager.recordKnownHostConnection(
                    identity = identity,
                    address = host,
                    port = settingsManager.portFlow.first(),
                    atMillis = System.currentTimeMillis()
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
