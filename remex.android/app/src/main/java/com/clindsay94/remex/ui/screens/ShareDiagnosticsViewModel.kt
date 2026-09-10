package com.clindsay94.remex.ui.screens

import android.app.Application
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.diagnostics.DiagnosticsBundle
import com.clindsay94.remex.diagnostics.DiagnosticsShare
import com.clindsay94.remex.diagnostics.LogcatCollector
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Collects the diagnostics environment once, then re-renders the bundle whenever the network toggle
 * moves (RemEx-0iww).
 *
 * COLLECTED ONCE, RENDERED MANY TIMES, and the split is the point. The log is gathered a single time
 * per visit and held; flipping the toggle re-runs only [DiagnosticsBundle.build], which is pure and
 * deterministic. Re-collecting per toggle would make the preview a moving target — the user would be
 * approving one set of log lines and sending a different one, and the lines that appeared in between
 * are exactly the ones nobody looked at.
 *
 * The same reason keeps the render synchronous: what [uiState] holds is the finished, scrubbed text,
 * so the screen cannot accidentally preview one string and share another. There is one string.
 */
class ShareDiagnosticsViewModel(application: Application) : AndroidViewModel(application) {

    /**
     * @property bundle the SCRUBBED text, which is both what the screen previews and what gets sent.
     *   Empty only while [collecting] is true.
     * @property sharing a share is being staged right now. Disables the button, so a second tap
     *   cannot stack a second chooser sheet behind the first.
     */
    data class UiState(
            val collecting: Boolean = true,
            val includeNetworkInfo: Boolean = false,
            val bundle: String = "",
            val sharing: Boolean = false,
    )

    /**
     * The outcome of [requestShare].
     *
     * Three cases rather than a nullable Intent, because "could not be staged" and "you already
     * pressed this" need opposite responses: the first is a failure the user has to be told about,
     * and the second is a double tap, where a snackbar would be the app complaining about something
     * only it did.
     */
    sealed interface ShareRequest {
        data class Ready(val chooser: Intent) : ShareRequest

        data object Failed : ShareRequest

        data object AlreadyInFlight : ShareRequest
    }

    private val _uiState = MutableStateFlow(UiState())
    val uiState: StateFlow<UiState> = _uiState.asStateFlow()

    /** Held so the toggle can re-render without going back to logcat. */
    private var environment: DiagnosticsBundle.Environment? = null

    init {
        viewModelScope.launch {
            val collected = gatherEnvironment()
            environment = collected
            _uiState.value =
                    UiState(
                            collecting = false,
                            includeNetworkInfo = false,
                            bundle = render(collected, includeNetworkInfo = false),
                    )
        }
    }

    fun setIncludeNetworkInfo(include: Boolean) {
        val current = environment ?: return
        _uiState.value =
                _uiState.value.copy(
                        includeNetworkInfo = include,
                        bundle = render(current, include),
                )
    }

    /**
     * Stages the text currently on screen and returns a chooser for it.
     *
     * Takes the bundle from [uiState] rather than rebuilding it, so that what leaves is the exact
     * string the user was shown — including the case where the toggle moved after collection.
     *
     * Suspending because staging writes the report to disk: up to 128KB, on a phone that is already
     * misbehaving badly enough for its owner to be filing a report about it.
     *
     * THE RE-ENTRANCY GUARD IS SAFE WITHOUT A LOCK because the check and the set happen before the
     * first suspension point, and every caller is on the main dispatcher — so a second tap either
     * arrives before the first call started (and finds `sharing` false, correctly) or after it set
     * the flag. Nothing can interleave between the two lines.
     */
    suspend fun requestShare(context: Context): ShareRequest {
        val current = _uiState.value
        if (current.sharing) return ShareRequest.AlreadyInFlight
        if (current.bundle.isEmpty()) return ShareRequest.Failed
        _uiState.value = current.copy(sharing = true)

        return try {
            val chooser =
                    withContext(Dispatchers.IO) {
                        DiagnosticsShare.buildSendIntent(context, current.bundle)
                    }
            if (chooser == null) ShareRequest.Failed else ShareRequest.Ready(chooser)
        } finally {
            // Re-read rather than reusing `current`: the toggle may have moved while the write was
            // in flight, and clearing the flag must not also roll the report back to the version
            // the user was looking at when they pressed the button.
            _uiState.value = _uiState.value.copy(sharing = false)
        }
    }

    private fun render(environment: DiagnosticsBundle.Environment, includeNetworkInfo: Boolean) =
            DiagnosticsBundle.build(environment, includeNetworkInfo)

    /**
     * Assembles everything [DiagnosticsBundle] needs. Only the log step leaves the process.
     *
     * `replayCache` rather than a collector: `hostCapabilities` is a `replay = 1` SharedFlow, so the
     * last `host_info` the phone heard is already sitting there and reading it is what makes this a
     * snapshot. Collecting would instead leave the bundle able to change under the preview.
     */
    private suspend fun gatherEnvironment(): DiagnosticsBundle.Environment =
            DiagnosticsBundle.Environment(
                    appVersion = BuildConfig.VERSION_NAME,
                    deviceManufacturer = Build.MANUFACTURER,
                    deviceModel = Build.MODEL,
                    androidApiLevel = Build.VERSION.SDK_INT,
                    hostInfoJson = RemexClientManager.hostCapabilities.replayCache.firstOrNull(),
                    connectionPhase = describeConnection(),
                    logcat = LogcatCollector.collect(),
            )

    private fun describeConnection(): String =
            when {
                RemexClientManager.isConnected.value -> "connected"
                RemexClientManager.isConnecting.value -> "connecting"
                else -> "disconnected"
            }
}
