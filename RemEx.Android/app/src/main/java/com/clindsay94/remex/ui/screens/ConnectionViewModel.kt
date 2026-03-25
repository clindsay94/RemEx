package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONObject

class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val settingsManager = SettingsManager(application)

    val connectionPreferences: StateFlow<SettingsManager.ConnectionPreferences?> = settingsManager.connectionPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)

    val remoteDesktopPreferences: StateFlow<SettingsManager.RemoteDesktopPreferences?> = settingsManager.remoteDesktopPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting: StateFlow<Boolean> = _isConnecting.asStateFlow()

    private val _connectionStatus = MutableStateFlow("Disconnected")
    val connectionStatus: StateFlow<String> = _connectionStatus.asStateFlow()

    private val _connectionError = MutableStateFlow<String?>(null)
    val connectionError: StateFlow<String?> = _connectionError.asStateFlow()

    private val _capabilitySummary = MutableStateFlow("Awaiting host metadata")
    val capabilitySummary: StateFlow<String> = _capabilitySummary.asStateFlow()

    init {
        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (connected) {
                    _connectionStatus.value = "Connected"
                } else if (!_isConnecting.value) {
                    _connectionStatus.value = "Disconnected"
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
                subnetMask = subnetMask
            )
            settingsManager.saveRemoteDesktopDefaults(
                quality = desktopQuality,
                targetFps = desktopTargetFps,
                scale = desktopScale
            )

            _isConnecting.value = true
            _connectionStatus.value = "Connecting to $newHost:$newPort..."

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
                            _connectionStatus.value = "Error: $msg"
                        }
                    } catch (_: Exception) { /* non-JSON result is fine */ }
                } else {
                    _connectionError.value = "Native library not loaded"
                    _connectionStatus.value = "Native library not loaded"
                }
            } catch (e: Exception) {
                _connectionError.value = e.message
                _connectionStatus.value = "Error: ${e.message}"
            } finally {
                _isConnecting.value = false
            }
        }
    }

    fun updateStatus(isConnected: Boolean) {
        _connectionStatus.value = if (isConnected) "Connected" else "Disconnected"
    }

    private fun buildCapabilitySummary(hostInfo: String): String {
        return try {
            val json = JSONObject(hostInfo)
            val runtimeMode = json.optString("runtimeMode", "unknown")
            val platform = json.optString("platform", "unknown")
            val supportsRemoteDesktop = json.optBoolean("supportsRemoteDesktop", false)
            val remoteDesktopText = if (supportsRemoteDesktop) {
                "desktop available"
            } else {
                json.optString("remoteDesktopUnavailableReason", "desktop unavailable")
            }

            "$platform / $runtimeMode / $remoteDesktopText"
        } catch (_: Exception) {
            "Host metadata unavailable"
        }
    }
}
