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

data class TelemetryState(
    val cpuUsage: Int = 0,
    val gpuUsage: Int = 0,
    val ramUsage: Int = 0
)

class DashboardViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val isConnected: StateFlow<Boolean> = RemexClientManager.isConnected

    private val _telemetryState = MutableStateFlow(TelemetryState())
    val telemetryState: StateFlow<TelemetryState> = _telemetryState.asStateFlow()

    init {
        viewModelScope.launch {
            RemexClientManager.telemetry.collect { telemetryData ->
                try {
                    val json = JSONObject(telemetryData)
                    _telemetryState.update {
                        it.copy(
                            cpuUsage = json.optInt("cpu", 0),
                            gpuUsage = json.optInt("gpu", 0),
                            ramUsage = json.optInt("ram", 0)
                        )
                    }
                } catch (e: Exception) {
                    e.printStackTrace()
                }
            }
        }
    }

    fun wakePc() {
        viewModelScope.launch {
            try {
                if (RemexCoreClient.isLibraryLoaded) {
                    val mac = settingsManager.macAddressFlow.first()
                    val broadcast = settingsManager.broadcastIpFlow.first()
                    if (mac.isNotEmpty()) {
                        RemexCoreClient.WakePc(mac, broadcast, 9)
                    }
                }
            } catch (e: Throwable) {
                e.printStackTrace()
            }
        }
    }

    fun sendCommand(command: String) {
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("action", command)
                }
                RemexCoreClient.SendCommand(request.toString())
            }
        } catch (e: Throwable) {
            e.printStackTrace()
        }
    }
}
