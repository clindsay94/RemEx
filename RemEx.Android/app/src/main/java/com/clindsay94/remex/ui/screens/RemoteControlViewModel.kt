package com.clindsay94.remex.ui.screens

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.launch
import org.json.JSONObject

class RemoteControlViewModel : ViewModel() {

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
