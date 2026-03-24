package com.clindsay94.remex.ui.screens

import android.util.Log.e
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

data class AppEntry(
    val name: String,
    val path: String,
    val iconBase64: String? = null
)

class AppLauncherViewModel : ViewModel() {

    // Converts the flow directly into a StateFlow, parsing JSON on a background thread.
    val apps: StateFlow<List<AppEntry>> = RemexClientManager.launcherEntries
        .map { data ->
            try {
                val array = JSONArray(data)
                // Idiomatic list creation
                List(array.length()) { i ->
                    val obj = array.getJSONObject(i)
                    AppEntry(
                        name = obj.getString("name"),
                        path = obj.getString("path"),
                        iconBase64 = obj.optString("iconBase64").takeIf { it.isNotEmpty() }
                    )
                }
            } catch (e: Exception) {
                e("AppLauncherViewModel", "Failed to parse launcher entries", e)
                emptyList() // Return empty list or handle error state
            }
        }
        .flowOn(Dispatchers.Default) // Offload JSON parsing to the Default dispatcher
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000), // Keeps flow active for 5s after last subscriber leaves
            initialValue = emptyList()
        )

    init {
        refreshApps()
    }

    fun refreshApps() {
        // If SendMessage is a blocking IO operation, consider launching on Dispatchers.IO
        viewModelScope.launch(Dispatchers.IO) {
            if (RemexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("type", "layout_request")
                }
                RemexCoreClient.SendMessage(request.toString())
            }
        }
    }

    fun launchApp(app: AppEntry) {
        // If SendCommand is a blocking IO operation, consider launching on Dispatchers.IO
        viewModelScope.launch(Dispatchers.IO) {
            if (RemexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("action", "LaunchApp")
                    put("parameters", JSONObject().apply {
                        put("Path", app.path)
                    })
                }
                RemexCoreClient.SendCommand(request.toString())
            }
        }
    }
}