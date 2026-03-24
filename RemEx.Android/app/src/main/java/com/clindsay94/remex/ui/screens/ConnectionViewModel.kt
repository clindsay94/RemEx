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

    val connectionPreferences = settingsManager.connectionPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), SettingsManager.ConnectionPreferences())

    val remoteDesktopPreferences = settingsManager.remoteDesktopPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), SettingsManager.RemoteDesktopPreferences())

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting: StateFlow<Boolean> = _isConnecting.asStateFlow()

    private val _connectionStatus = MutableStateFlow("Disconnected")
    val connectionStatus: StateFlow<String> = _connectionStatus.asStateFlow()

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
                if (RemexCoreClient.isLibraryLoaded) {
                    val initRequest = JSONObject().apply {
                        put("host", newHost)
                        put("port", newPort)
                        put("startTelemetryPolling", true)
                    }
                    RemexCoreClient.InitRemex(initRequest.toString())
                } else {
                    _connectionStatus.value = "Native library not loaded"
                }
            } catch (e: Exception) {
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
