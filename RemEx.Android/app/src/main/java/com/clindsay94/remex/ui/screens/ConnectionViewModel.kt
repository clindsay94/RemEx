package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.NsdDiscoveryManager
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.service.RemexConnectionService
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONObject

class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val settingsManager = SettingsManager(application)
    private val nsdDiscoveryManager = NsdDiscoveryManager(application)
    private val res = application.resources

    val connectionPreferences: StateFlow<SettingsManager.ConnectionPreferences?> = settingsManager.connectionPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)

    val remoteDesktopPreferences: StateFlow<SettingsManager.RemoteDesktopPreferences?> = settingsManager.remoteDesktopPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting: StateFlow<Boolean> = _isConnecting.asStateFlow()

    private val _connectionStatus = MutableStateFlow(res.getString(R.string.status_disconnected))
    val connectionStatus: StateFlow<String> = _connectionStatus.asStateFlow()

    private val _connectionError = MutableStateFlow<String?>(null)
    val connectionError: StateFlow<String?> = _connectionError.asStateFlow()

    private val _capabilitySummary = MutableStateFlow(res.getString(R.string.status_awaiting_metadata))
    val capabilitySummary: StateFlow<String> = _capabilitySummary.asStateFlow()

    private val _isDiscovering = MutableStateFlow(false)
    val isDiscovering: StateFlow<Boolean> = _isDiscovering.asStateFlow()

    private val _discoveredHost = MutableStateFlow<DiscoveredHost?>(null)
    val discoveredHost: StateFlow<DiscoveredHost?> = _discoveredHost.asStateFlow()

    init {
        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (connected) {
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
    }

    fun connect(
        newHost: String,
        newPort: Int,
        macAddress: String,
        broadcastIp: String,
        subnetMask: String,
        accessKey: String,
        desktopQuality: Int,
        desktopTargetFps: Int,
        desktopScale: Float
    ) {
        viewModelScope.launch {
            settingsManager.saveConnectionSettings(
                host = newHost,
                port = newPort,
                mac = macAddress,
                broadcast = broadcastIp,
                subnetMask = subnetMask,
                accessKey = accessKey
            )
            settingsManager.saveRemoteDesktopDefaults(
                quality = desktopQuality,
                targetFps = desktopTargetFps,
                scale = desktopScale
            )

            _isConnecting.value = true
            _connectionStatus.value = res.getString(R.string.status_connecting, newHost, newPort)

            try {
                _connectionError.value = null
                if (RemexCoreClient.isLibraryLoaded) {
                    val initRequest = JSONObject().apply {
                        put("host", newHost)
                        put("port", newPort)
                        put("startTelemetryPolling", true)
                    }
                    val result = RemexCoreClient.InitRemex(initRequest.toString())
                    try {
                        val json = JSONObject(result)
                        if (!json.optBoolean("success", false)) {
                            val msg = json.optString("message", "Connection failed")
                            _connectionError.value = msg
                            _connectionStatus.value = res.getString(R.string.status_error, msg)
                        } else {
                            // Start foreground service to keep connection alive
                            try {
                                RemexConnectionService.start(getApplication())
                            } catch (e: Exception) {
                                android.util.Log.w("ConnectionVM", "Foreground service could not be started, connection will work without background persistence", e)
                            }
                        }
                    } catch (e: Exception) {
                        android.util.Log.w("ConnectionVM", "InitRemex returned non-JSON result: $result", e)
                    }
                } else {
                    _connectionError.value = res.getString(R.string.status_native_lib_not_loaded)
                    _connectionStatus.value = res.getString(R.string.status_native_lib_not_loaded)
                }
            } catch (e: Exception) {
                _connectionError.value = e.message
                _connectionStatus.value = res.getString(R.string.status_error, e.message ?: "")
            } finally {
                _isConnecting.value = false
            }
        }
    }

    fun updateStatus(isConnected: Boolean) {
        _connectionStatus.value = if (isConnected) res.getString(R.string.status_connected) else res.getString(R.string.status_disconnected)
    }

    fun clearError() {
        _connectionError.value = null
    }

    fun discoverHost() {
        viewModelScope.launch {
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
                _connectionError.value = res.getString(R.string.error_discovery_failed, e.message ?: "")
            } finally {
                _isDiscovering.value = false
            }
        }
    }

    fun applyQrResultAndConnect(host: String, port: Int) {
        val cp = connectionPreferences.value
        val dp = remoteDesktopPreferences.value
        connect(
            newHost = host,
            newPort = port,
            macAddress = cp?.macAddress ?: "",
            broadcastIp = cp?.broadcastIp ?: "255.255.255.255",
            subnetMask = cp?.subnetMask ?: "255.255.255.0",
            accessKey = cp?.accessKey ?: "",
            desktopQuality = dp?.quality ?: 50,
            desktopTargetFps = dp?.targetFps ?: 30,
            desktopScale = dp?.scale ?: 0.6f
        )
    }

    private fun buildCapabilitySummary(hostInfo: String): String {
        return try {
            val json = JSONObject(hostInfo)
            val runtimeMode = json.optString("runtimeMode", "unknown")
            val platform = json.optString("platform", "unknown")
            val supportsRemoteDesktop = json.optBoolean("supportsRemoteDesktop", false)
            val remoteDesktopText = if (supportsRemoteDesktop) {
                res.getString(R.string.capability_desktop_available)
            } else {
                json.optString("remoteDesktopUnavailableReason", res.getString(R.string.capability_desktop_unavailable))
            }

            "$platform / $runtimeMode / $remoteDesktopText"
        } catch (_: Exception) {
            res.getString(R.string.status_metadata_unavailable)
        }
    }
}
