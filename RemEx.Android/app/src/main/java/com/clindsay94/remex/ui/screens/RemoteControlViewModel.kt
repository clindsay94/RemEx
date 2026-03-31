package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONObject

class RemoteControlViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val remoteControlCardShapePreset = settingsManager.remoteControlCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val remoteMouseCardShapePreset = settingsManager.remoteMouseCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 20)

    private val _commandStatus = MutableStateFlow<String?>(null)
    val commandStatus: StateFlow<String?> = _commandStatus.asStateFlow()

    fun wakePc() {
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                _commandStatus.value = "Native library not loaded"
                return@launch
            }

            try {
                val mac = settingsManager.macAddressFlow.first()
                val broadcast = settingsManager.broadcastIpFlow.first()
                if (mac.isNotBlank()) {
                    val responseJson = RemexCoreClient.WakePc(mac, broadcast, 9)
                    val response = JSONObject(responseJson)
                    val success = response.optBoolean("success", false)
                    val message = response.optString("message", "Wake packet sent")
                    _commandStatus.value = if (success) "Success: $message" else "Failed: $message"
                } else {
                    _commandStatus.value = "Failed: MAC address not configured in settings"
                }
            } catch (e: Exception) {
                _commandStatus.value = "Error: ${e.message ?: "Unknown error"}"
            }
        }
    }

    fun sendMouseMove(deltaX: Int, deltaY: Int) {
        sendInput(JSONObject().apply {
            put("eventType", "mouseMove")
            put("deltaX", deltaX)
            put("deltaY", deltaY)
        })
    }

    fun sendMouseClick(button: Int) {
        sendInput(JSONObject().apply {
            put("eventType", "mouseClick")
            put("button", button)
        })
    }

    fun sendScroll(deltaY: Int) {
        sendInput(JSONObject().apply {
            put("eventType", "mouseScroll")
            put("deltaY", deltaY)
        })
    }

    fun sendText(text: String) {
        sendInput(JSONObject().apply {
            put("eventType", "typeText")
            put("text", text)
        })
    }

    fun clearCommandStatus() {
        _commandStatus.value = null
    }

    fun sendSystemCommand(action: String, delaySeconds: Int = 0) {
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                _commandStatus.value = "Native library not loaded"
                return@launch
            }

            try {
                val parameters = JSONObject()
                if (delaySeconds > 0) {
                    parameters.put("DelaySeconds", delaySeconds.toString())
                }

                val request = JSONObject().apply {
                    put("action", action)
                    if (parameters.length() > 0) {
                        put("parameters", parameters)
                    }
                }

                val responseJson = RemexCoreClient.SendCommand(request.toString())
                val response = JSONObject(responseJson)
                val message = response.optString("message", "Command sent")
                val success = response.optBoolean("success", false)
                _commandStatus.value = if (success) {
                    "Success: $message"
                } else {
                    "Failed: $message"
                }
            } catch (e: Exception) {
                _commandStatus.value = "Failed: ${e.message ?: "Unknown error"}"
            }
        }
    }

    private fun sendInput(input: JSONObject) {
        viewModelScope.launch {
            if (RemexCoreClient.isLibraryLoaded) {
                val message = JSONObject().apply {
                    put("type", "desktop_input")
                    put("inputEvent", input)
                }
                RemexCoreClient.SendMessage(message.toString())
            }
        }
    }
}
