package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONObject

class ConnectionViewModel(application: Application) : AndroidViewModel(application) {
    private val settingsManager = SettingsManager(application)

    val host = settingsManager.hostFlow.stateIn(viewModelScope, SharingStarted.WhileSubscribed(), "")
    val port = settingsManager.portFlow.stateIn(viewModelScope, SharingStarted.WhileSubscribed(), 5005)

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting: StateFlow<Boolean> = _isConnecting.asStateFlow()

    private val _connectionStatus = MutableStateFlow("Disconnected")
    val connectionStatus: StateFlow<String> = _connectionStatus.asStateFlow()

    fun connect(newHost: String, newPort: Int) {
        viewModelScope.launch {
            settingsManager.saveSettings(newHost, newPort)
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
}
