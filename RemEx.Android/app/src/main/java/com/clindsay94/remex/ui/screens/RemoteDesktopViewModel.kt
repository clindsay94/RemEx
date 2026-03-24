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

class RemoteDesktopViewModel : ViewModel() {

    private val _currentFrame = MutableStateFlow<Bitmap?>(null)
    val currentFrame: StateFlow<Bitmap?> = _currentFrame.asStateFlow()

    private val _isStreaming = MutableStateFlow(false)
    val isStreaming: StateFlow<Boolean> = _isStreaming.asStateFlow()

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
    }

    fun startStreaming() {
        if (RemexCoreClient.isLibraryLoaded) {
            val config = JSONObject().apply {
                put("quality", 50)
                put("scale", 0.5)
                put("targetFps", 15)
            }
            RemexCoreClient.StartDesktopStream(config.toString())
            _isStreaming.value = true
        }
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
