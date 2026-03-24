package com.clindsay94.remex.ui.screens

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import org.json.JSONObject

data class RemoteDesktopCapabilityState(
    val supportsRemoteDesktop: Boolean = true,
    val unavailableReason: String? = null
)

class RemoteDesktopViewModel : ViewModel() {

    private val _currentFrame = MutableStateFlow<Bitmap?>(null)
    val currentFrame: StateFlow<Bitmap?> = _currentFrame.asStateFlow()

    private val _isStreaming = MutableStateFlow(false)
    val isStreaming: StateFlow<Boolean> = _isStreaming.asStateFlow()

    private val _capabilityState = MutableStateFlow(RemoteDesktopCapabilityState())
    val capabilityState: StateFlow<RemoteDesktopCapabilityState> = _capabilityState.asStateFlow()

    private val _desktopError = MutableStateFlow<String?>(null)
    val desktopError: StateFlow<String?> = _desktopError.asStateFlow()

    init {
        viewModelScope.launch {
            RemexClientManager.frames.collect { bytes ->
                try {
                    val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    _currentFrame.value = bitmap
                } catch (e: Exception) {
                    e.printStackTrace()
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
            RemexClientManager.isConnected.collect { connected ->
                if (!connected) {
                    _isStreaming.value = false
                    _currentFrame.value = null
                }
            }
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

        val config = JSONObject().apply {
            put("quality", 50)
            put("scale", 0.5)
            put("targetFps", 15)
        }

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

    override fun onCleared() {
        super.onCleared()
        stopStreaming()
    }
}
