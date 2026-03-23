package com.clindsay94.remex

import androidx.lifecycle.ViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONObject

data class TelemetryState(
    val cpuUsage: Int = 0,
    val gpuUsage: Int = 0,
    val ramUsage: Int = 0
)

class DashboardViewModel : ViewModel(), RemexCoreClient.RemexCallback {

    private val _isConnected = MutableStateFlow(false)
    val isConnected: StateFlow<Boolean> = _isConnected.asStateFlow()

    private val _telemetryState = MutableStateFlow(TelemetryState())
    val telemetryState: StateFlow<TelemetryState> = _telemetryState.asStateFlow()

    init {
        // Register the callback when ViewModel is created
        RemexCoreClient.setCallback(this)

        try {
            if (RemexCoreClient.isLibraryLoaded) {
                // Initialize connection logic in native client
                // TODO: Pass actual initialization JSON if required
                RemexCoreClient.InitRemex("{}")
            } else {
                android.util.Log.e("DashboardViewModel", "Native library RemexCore not loaded!")
            }
        } catch (e: Throwable) {
            e.printStackTrace()
        }
    }

    override fun onTelemetryUpdate(telemetryData: String) {
        try {
            val json = JSONObject(telemetryData)
            _telemetryState.update {
                it.copy(
                    cpuUsage = json.optInt("cpu", 0),
                    gpuUsage = json.optInt("gpu", 0),
                    ramUsage = json.optInt("ram", 0)
                )
            }
        } catch (e: Throwable) {
            e.printStackTrace()
        }
    }

    override fun onConnectionStateChanged(isConnected: Boolean) {
        _isConnected.value = isConnected
    }

    fun wakePc() {
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                // TODO: Provide actual MAC address, broadcast IP and port
                RemexCoreClient.WakePc("00:00:00:00:00:00", "255.255.255.255", 9)
            }
        } catch (e: Throwable) {
            e.printStackTrace()
        }
    }

    fun sendCommand(command: String) {
        try {
            if (RemexCoreClient.isLibraryLoaded) {
                RemexCoreClient.SendCommand(command)
            }
        } catch (e: Throwable) {
            e.printStackTrace()
        }
    }

    override fun onCleared() {
        super.onCleared()
        // Unregister the callback to prevent memory leaks
        RemexCoreClient.setCallback(null)
    }
}
