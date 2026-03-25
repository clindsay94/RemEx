package com.clindsay94.remex.ui.screens

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.json.JSONObject

data class RemoteDesktopCapabilityState(
    val supportsRemoteDesktop: Boolean = true,
    val unavailableReason: String? = null
)

data class RemoteDesktopConfigState(
    val quality: Int = 50,
    val targetFps: Int = 30,
    val scale: Float = 0.6f
)

class RemoteDesktopViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)
    
    private var hostScreenWidth: Int = 1920
    private var hostScreenHeight: Int = 1080

    private val _currentFrame = MutableStateFlow<Bitmap?>(null)
    val currentFrame: StateFlow<Bitmap?> = _currentFrame.asStateFlow()

    private val _isStreaming = MutableStateFlow(false)
    val isStreaming: StateFlow<Boolean> = _isStreaming.asStateFlow()

    private val _capabilityState = MutableStateFlow(RemoteDesktopCapabilityState())
    val capabilityState: StateFlow<RemoteDesktopCapabilityState> = _capabilityState.asStateFlow()

    private val _desktopError = MutableStateFlow<String?>(null)
    val desktopError: StateFlow<String?> = _desktopError.asStateFlow()

    private val _configState = MutableStateFlow(RemoteDesktopConfigState())
    val configState: StateFlow<RemoteDesktopConfigState> = _configState.asStateFlow()

    val savedDesktopDefaults = settingsManager.remoteDesktopPreferencesFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), SettingsManager.RemoteDesktopPreferences())

    init {
        viewModelScope.launch {
            settingsManager.remoteDesktopPreferencesFlow.collect { prefs ->
                _configState.value = RemoteDesktopConfigState(
                    quality = prefs.quality.coerceIn(1, 100),
                    targetFps = prefs.targetFps.coerceIn(1, 120),
                    scale = prefs.scale.coerceIn(0.25f, 1.0f)
                )
            }
        }

        viewModelScope.launch {
            RemexClientManager.frames.collect { bytes ->
                try {
                    _currentFrame.value = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                } catch (_: Exception) {
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.hostCapabilities.collect { hostInfo ->
                try {
                    val json = JSONObject(hostInfo)
                    _capabilityState.value = RemoteDesktopCapabilityState(
                        supportsRemoteDesktop = json.optBoolean("supportsRemoteDesktop", false),
                        unavailableReason = json.optString("remoteDesktopUnavailableReason").takeIf { it.isNotBlank() }
                    )
                } catch (_: Exception) {
                    _capabilityState.value = RemoteDesktopCapabilityState(
                        supportsRemoteDesktop = false,
                        unavailableReason = "Host metadata unavailable"
                    )
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopErrors.collect { errorText ->
                _desktopError.value = errorText
                _isStreaming.value = false
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopMeta.collect { metaData ->
                try {
                    val json = JSONObject(metaData)
                    hostScreenWidth = json.optInt("screenWidth", 1920)
                    hostScreenHeight = json.optInt("screenHeight", 1080)
                } catch (_: Exception) { }
            }
        }

        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (!connected) {
                    _isStreaming.value = false
                    _currentFrame.value = null
                }
            }
        }
    }

    fun updateQuality(value: Int) {
        _configState.update { it.copy(quality = value.coerceIn(1, 100)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateTargetFps(value: Int) {
        _configState.update { it.copy(targetFps = value.coerceIn(1, 120)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateScale(value: Float) {
        _configState.update { it.copy(scale = value.coerceIn(0.25f, 1.0f)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateDirectTouch(enabled: Boolean) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopDirectTouch(enabled)
        }
    }

    fun startStreaming() {
        if (!RemexCoreClient.isLibraryLoaded) {
            _desktopError.value = "Native library not loaded"
            return
        }

        if (!_capabilityState.value.supportsRemoteDesktop) {
            _desktopError.value = _capabilityState.value.unavailableReason
                ?: "Remote desktop is unavailable on this host"
            return
        }

        val config = buildConfigJson()

        _desktopError.value = null
        RemexCoreClient.StartDesktopStream(config.toString())
        _isStreaming.value = true
    }

    fun stopStreaming() {
        if (RemexCoreClient.isLibraryLoaded) {
            RemexCoreClient.StopDesktopStream()
            _isStreaming.value = false
            _currentFrame.value = null
        }
    }

    private var accumulatedX = 0f
    private var accumulatedY = 0f

    fun sendMouseMove(deltaX: Float, deltaY: Float) {
        accumulatedX += deltaX
        accumulatedY += deltaY
        
        val intX = accumulatedX.toInt()
        val intY = accumulatedY.toInt()
        
        if (intX != 0 || intY != 0) {
            sendInput(JSONObject().apply {
                put("eventType", "mouseMove")
                put("deltaX", intX)
                put("deltaY", intY)
            })
            accumulatedX -= intX
            accumulatedY -= intY
        }
    }

    fun sendMouseScroll(deltaX: Int, deltaY: Int) {
        sendInput(JSONObject().apply {
            put("eventType", "mouseScroll")
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

    fun sendMouseAbsolute(x: Int, y: Int) {
        sendInput(JSONObject().apply {
            put("eventType", "mouseMove")
            put("x", x)
            put("y", y)
        })
    }

    fun sendMouseAbsoluteClick(button: Int, x: Int, y: Int) {
        sendInput(JSONObject().apply {
            put("eventType", "mouseClick")
            put("button", button)
            put("x", x)
            put("y", y)
        })
    }

    fun getHostScreenSize(): Pair<Int, Int> = Pair(hostScreenWidth, hostScreenHeight)

    private fun pushConfigIfStreaming() {
        if (!_isStreaming.value || !RemexCoreClient.isLibraryLoaded) {
            return
        }

        try {
            val message = JSONObject().apply {
                put("type", "desktop_config")
                put("desktopConfig", buildConfigJson())
            }
            RemexCoreClient.SendMessage(message.toString())
        } catch (_: Exception) {
        }
    }

    private fun persistDesktopDefaults() {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopDefaults(
                quality = _configState.value.quality,
                targetFps = _configState.value.targetFps,
                scale = _configState.value.scale
            )
        }
    }

    private fun buildConfigJson(): JSONObject {
        return JSONObject().apply {
            put("quality", _configState.value.quality)
            put("scale", _configState.value.scale)
            put("targetFps", _configState.value.targetFps)
        }
    }

    private fun sendInput(input: JSONObject) {
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                return@launch
            }

            val message = JSONObject().apply {
                put("type", "desktop_input")
                put("inputEvent", input)
            }
            RemexCoreClient.SendMessage(message.toString())
        }
    }

    override fun onCleared() {
        super.onCleared()
        stopStreaming()
    }
}
