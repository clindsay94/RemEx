package com.clindsay94.remex.ui.screens

import android.util.Log
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

data class AppEntry(
    val name: String,
    val path: String,
    val iconBase64: String? = null
)

class AppLauncherViewModel(
    private val settingsManager: SettingsManager,
    remexClientManager: RemexClientManager,
    private val remexCoreClient: RemexCoreClient
) : ViewModel() {

    val appLauncherCardShapePreset: StateFlow<String> =
        settingsManager.appLauncherCardShapePresetFlow
            .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), "rounded")

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 20)

    val apps: StateFlow<List<AppEntry>> = remexClientManager.launcherEntries
        .map { data -> parseLauncherEntries(data) }
        .flowOn(Dispatchers.Default)
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = emptyList()
        )

    private fun parseLauncherEntries(data: String): List<AppEntry> {
        return try {
            val array = JSONArray(data)
            List(array.length()) { i ->
                val obj = array.getJSONObject(i)
                AppEntry(
                    name = obj.getString("displayName"),
                    path = obj.getString("targetPath"),
                    iconBase64 = obj.optString("iconBase64").takeIf { it.isNotEmpty() }
                )
            }
        } catch (e: JSONException) {
            Log.e("AppLauncherViewModel", "Failed to parse launcher entries", e)
            emptyList()
        }
    }

    fun launchApp(app: AppEntry) {
        viewModelScope.launch(Dispatchers.IO) {
            if (remexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("action", "LaunchApp")
                    put("parameters", JSONObject().apply {
                        put("TargetPath", app.path)
                    })
                }
                remexCoreClient.SendCommand(request.toString())
            } else {
                Log.w(
                    "AppLauncherViewModel",
                    "Attempted to launch app '${app.name}' at path '${app.path}' " +
                            "but the Remex library is not loaded."
                )
            }
        }
    }

    fun refreshApps() {
        viewModelScope.launch(Dispatchers.IO) {
            if (remexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("type", "launcher_sync_request")
                }
                remexCoreClient.SendMessage(request.toString())
            }
        }
    }

    // Companion object to provide the Factory
    companion object {
        fun provideFactory(
            settingsManager: SettingsManager,
            remexClientManager: RemexClientManager,
            remexCoreClient: RemexCoreClient
        ): ViewModelProvider.Factory = viewModelFactory {
            initializer {
                AppLauncherViewModel(settingsManager, remexClientManager, remexCoreClient)
            }
        }
    }
}
