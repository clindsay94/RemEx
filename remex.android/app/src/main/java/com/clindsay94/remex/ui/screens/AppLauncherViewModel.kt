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
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
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
    val iconBase64: String? = null,
)

class AppLauncherViewModel(
    private val settingsManager: SettingsManager,
    remexClientManager: RemexClientManager,
    private val remexCoreClient: RemexCoreClient
) : ViewModel() {

    val appLauncherCardShapePreset: StateFlow<Float> =
        settingsManager.appLauncherCardShapePresetFlow
            .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), com.clindsay94.remex.ui.theme.CardShapes.DEFAULT_CORNER_RADIUS_DP)

    val apps: StateFlow<List<AppEntry>> = remexClientManager.launcherEntries
        .map { data -> parseLauncherEntries(data) }
        .flowOn(Dispatchers.Default)
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = emptyList()
        )

    // In-memory recents (most-recently-launched first). Resets on process death; feeds the
    // App Launcher "Recent" carousel. Persisting across restarts can be added via DataStore later.
    private val _recentPaths = kotlinx.coroutines.flow.MutableStateFlow<List<String>>(emptyList())
    val recentApps: StateFlow<List<AppEntry>> =
        combine(_recentPaths, apps) { paths, all ->
            paths.mapNotNull { p -> all.firstOrNull { it.path == p } }
        }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

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
            }.distinctBy { "${it.name}|${it.path}" }
        } catch (e: JSONException) {
            Log.e("AppLauncherViewModel", "Failed to parse launcher entries", e)
            emptyList()
        }
    }

    fun launchApp(app: AppEntry) {
        // Record as most-recent (dedup, cap at 8) for the Recent carousel.
        _recentPaths.value = (listOf(app.path) + _recentPaths.value).distinct().take(8)
        viewModelScope.launch(Dispatchers.IO) {
            if (remexCoreClient.isLibraryLoaded) {
                val request = JSONObject().apply {
                    put("action", "LaunchApp")
                    put(
                        "parameters",
                        JSONObject().apply {
                            put("TargetPath", app.path)
                        }
                    )
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

    private val refreshTracker = LauncherRefreshTracker()

    /** Where the latest refresh stands: running, answered, or unanswered (RemEx-wqo7a.6). */
    val refreshState: StateFlow<LauncherRefreshState> = refreshTracker.state

    init {
        viewModelScope.launch {
            remexClientManager.launcherEntries.collect {
                refreshTracker.onEntriesArrived()
            }
        }
    }

    fun refreshApps() {
        if (!remexCoreClient.isLibraryLoaded) return
        val token = refreshTracker.begin()
        viewModelScope.launch(Dispatchers.IO) {
            // Must stay spelled exactly as MessageTypes.LauncherSyncRequest in Remex.Core —
            // Kotlin cannot reference the C# constants, so the host answers this only because
            // the two literals agree. A typo here is silent: the host logs one "Unknown message
            // type" warning and drops it, and the screen just sits on the timeout below.
            // remex.agent.tests/LauncherSyncRequestTests.cs guards the pair (RemEx-vpxx).
            val request = JSONObject().apply {
                put("type", "launcher_sync_request")
            }
            remexCoreClient.SendMessage(request.toString())
        }
        viewModelScope.launch {
            // The launcherEntries collector ends the refresh when the host's launcher_sync
            // arrives. If nothing has come back by now, the screen says so instead of quietly
            // dropping the spinner.
            kotlinx.coroutines.delay(REFRESH_TIMEOUT_MS)
            refreshTracker.onTimedOut(token)
        }
    }

    /** Hides the "your PC didn't answer" message without asking again. */
    fun dismissRefreshError() {
        refreshTracker.dismissNoAnswer()
    }

    // Companion object to provide the Factory
    companion object {
        /** How long a refresh waits for the PC's app list before saying it didn't answer. */
        private const val REFRESH_TIMEOUT_MS = 5_000L

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

/** Where the Apps screen's latest refresh stands (RemEx-wqo7a.6). */
enum class LauncherRefreshState {
    /** No refresh running, and the last one (if any) was answered. */
    Idle,

    /** A request for the app list is out and the PC hasn't answered yet. */
    Refreshing,

    /** The last request timed out with no app list from the PC. */
    NoAnswer,
}

/**
 * The refresh lifecycle, kept free of coroutines so a plain JVM test can drive it.
 *
 * Each [begin] hands back a token, and [onTimedOut] only acts on the token of the newest refresh.
 * Without that, pulling to refresh twice in a row let the first request's timer fire in the middle
 * of the second one and report "no answer" for a request that was still waiting. Any app list that
 * arrives ends the refresh, whichever request it answers.
 */
internal class LauncherRefreshTracker {
    private val _state = MutableStateFlow(LauncherRefreshState.Idle)
    val state: StateFlow<LauncherRefreshState> = _state.asStateFlow()

    private val generation = java.util.concurrent.atomic.AtomicLong(0L)

    /** Starts a refresh and returns the token its timeout must present. */
    fun begin(): Long {
        val token = generation.incrementAndGet()
        _state.value = LauncherRefreshState.Refreshing
        return token
    }

    /** The PC sent its app list: the refresh is over and any earlier "no answer" is stale. */
    fun onEntriesArrived() {
        _state.value = LauncherRefreshState.Idle
    }

    /** The wait for [token]'s answer ran out. Ignored if a newer refresh started or it was answered. */
    fun onTimedOut(token: Long) {
        if (token != generation.get()) return
        _state.compareAndSet(LauncherRefreshState.Refreshing, LauncherRefreshState.NoAnswer)
    }

    /** The person closed the "no answer" message. */
    fun dismissNoAnswer() {
        _state.compareAndSet(LauncherRefreshState.NoAnswer, LauncherRefreshState.Idle)
    }
}
