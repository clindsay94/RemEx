package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.NsdDiscoveryManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.RemexConnectionService
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONObject
import android.content.Context
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

    init {
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
        }
    }

    fun consumeDiscoveredHost() {
        _discoveredHost.value = null
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
