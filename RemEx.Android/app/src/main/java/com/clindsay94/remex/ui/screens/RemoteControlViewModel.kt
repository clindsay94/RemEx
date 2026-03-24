package com.clindsay94.remex.ui.screens

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import org.json.JSONObject

class RemoteControlViewModel : ViewModel() {

    private val _commandStatus = MutableStateFlow<String?>(null)
    val commandStatus: StateFlow<String?> = _commandStatus.asStateFlow()

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
