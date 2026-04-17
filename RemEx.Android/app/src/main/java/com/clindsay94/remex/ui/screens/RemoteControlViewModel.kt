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
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONObject

class RemoteControlViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val remoteControlCardShapePreset =
            settingsManager.remoteControlCardShapePresetFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    0f
            )

    val remoteMouseCardShapePreset =
            settingsManager.remoteMouseCardShapePresetFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    0f
            )

    val cardCornerRadius =
            settingsManager.cardCornerRadiusFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    20
            )

    val fabPositionX =
            settingsManager.floatingMouseIslandXFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    Float.NaN
            )

    val fabPositionY =
            settingsManager.floatingMouseIslandYFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    Float.NaN
            )

    val mouseFabX =
            settingsManager.mouseFabXFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    Float.NaN
            )

    val mouseFabY =
            settingsManager.mouseFabYFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    Float.NaN
            )

    val verticalScrollSensitivity =
            settingsManager.remoteDesktopPreferencesFlow
                    .map { it.verticalScrollSensitivity }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val horizontalScrollSensitivity =
            settingsManager.remoteDesktopPreferencesFlow
                    .map { it.horizontalScrollSensitivity }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

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
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseMove")
                    put("deltaX", deltaX)
                    put("deltaY", deltaY)
                }
        )
    }

    private var mouseMoveAccumX = 0f
    private var mouseMoveAccumY = 0f

    fun sendMouseMove(deltaX: Float, deltaY: Float) {
        mouseMoveAccumX += deltaX
        mouseMoveAccumY += deltaY
        val intX = mouseMoveAccumX.toInt()
        val intY = mouseMoveAccumY.toInt()
        if (intX != 0 || intY != 0) {
            sendMouseMove(intX, intY)
            mouseMoveAccumX -= intX
            mouseMoveAccumY -= intY
        }
    }

    fun sendMouseClick(button: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseClick")
                    put("button", button)
                }
        )
    }

    fun sendScroll(deltaY: Int, deltaX: Int = 0) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseScroll")
                    put("deltaY", deltaY)
                    put("deltaX", deltaX)
                }
        )
    }

    fun sendText(text: String) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "typeText")
                    put("text", text)
                }
        )
    }

    fun clearCommandStatus() {
        _commandStatus.value = null
    }

    fun saveFloatingMouseIslandPosition(x: Float, y: Float) {
        viewModelScope.launch { settingsManager.saveFloatingMouseIslandPosition(x, y) }
    }

    fun saveMouseFabPosition(x: Float, y: Float) {
        viewModelScope.launch { settingsManager.saveMouseFabPosition(x, y) }
    }

    fun updateScrollSensitivity(vertical: Float, horizontal: Float) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopScrollSensitivity(
                    vertical.coerceIn(0.1f, 5.0f),
                    horizontal.coerceIn(0.1f, 5.0f)
            )
        }
    }

    fun sendSystemCommand(action: String, delaySeconds: Int = 0) {
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                _commandStatus.value = "Native library not loaded"
                return@launch
            }

            try {
                val accessKey = settingsManager.accessKeyFlow.first()
                val parameters = JSONObject()
                if (delaySeconds > 0) {
                    parameters.put("DelaySeconds", delaySeconds.toString())
                }
                if (accessKey.isNotBlank()) {
                    parameters.put("AccessKey", accessKey)
                }

                val request =
                        JSONObject().apply {
                            put("action", action)
                            put("parameters", parameters)
                        }

                val responseJson = RemexCoreClient.SendCommand(request.toString())
                val response = JSONObject(responseJson)
                val message = response.optString("message", "Command sent")
                val success = response.optBoolean("success", false)
                _commandStatus.value =
                        if (success) {
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
                val message =
                        JSONObject().apply {
                            put("type", "desktop_input")
                            put("inputEvent", input)
                        }
                RemexCoreClient.SendMessage(message.toString())
            }
        }
    }
}
