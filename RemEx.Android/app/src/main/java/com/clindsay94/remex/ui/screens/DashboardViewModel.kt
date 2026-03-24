package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import kotlin.math.roundToInt
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
                    val sensors = json.optJSONArray("sensors")
                    _telemetryState.update {
                        it.copy(
                            cpuUsage = sensors.extractPercent("CPU", listOf("cpu", "usage")),
                            gpuUsage = sensors.extractPercent("GPU", listOf("gpu", "usage")),
                            ramUsage = sensors.extractPercent("Memory", listOf("memory", "load"))
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

    private fun org.json.JSONArray?.extractPercent(category: String, preferredTokens: List<String>): Int {
        if (this == null) {
            return 0
        }

        var fallback = Double.NaN
        for (index in 0 until length()) {
            val sensor = optJSONObject(index) ?: continue
            val sensorCategory = sensor.optString("category")
            if (!sensorCategory.equals(category, ignoreCase = true)) {
                continue
            }

            val unit = sensor.optString("unit")
            val value = sensor.optDouble("value", Double.NaN)
            if (value.isNaN()) {
                continue
            }

            if (unit == "%") {
                val name = sensor.optString("name").lowercase()
                if (preferredTokens.all(name::contains)) {
                    return value.roundToInt().coerceIn(0, 100)
                }

                if (fallback.isNaN()) {
                    fallback = value
                }
            }
        }

        return if (fallback.isNaN()) 0 else fallback.roundToInt().coerceIn(0, 100)
    }
}
