package com.clindsay94.remex.ui.screens

import android.app.Application
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.HomePinsState
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.ui.routines.RoutinePowerVerbsCapability
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

/**
 * Home's state holder (RemEx-wqo7a.6): the PC card, the pinned sensors' live readings and the quick
 * actions. The Known PCs rows for the connect card and Recent activity come from the app-wide
 * [ConnectionViewModel], which already owns that join; this holds nothing it would duplicate.
 *
 * **ONE TELEMETRY COLLECTOR, ACTIVE ONLY WHILE HOME IS ON SCREEN.** [setVisible] is driven by the
 * screen's lifecycle and pager visibility, exactly like the Sensors grid's; while it is false the
 * collector skips the parse. That single gate is where a "telemetry wanted" lease can hang later.
 */
class HomeViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val isConnected: StateFlow<Boolean> = RemexClientManager.isConnected
    val isConnecting: StateFlow<Boolean> = RemexClientManager.isConnecting

    /** The address of the PC that is connected, or null. */
    val connectedHost: StateFlow<String?> =
            RemexClientManager.connectedHost
                    .map { it?.host }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), RemexClientManager.connectedHost.value?.host)

    /** The machine name this connection's `host_info` reported, or blank. */
    val hostMachineName: StateFlow<String> =
            RemexClientManager.hostInfoForConnection
                    .map { KnownHosts.parseMachineName(it?.json) }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), "")

    /**
     * The power verbs this connection's PC advertised, or null when it did not say (then Lock and
     * Sleep both show, as on the Commands grid). Read per connection, never from a previous PC.
     */
    val powerVerbs: StateFlow<List<String>?> =
            RemexClientManager.hostInfoForConnection
                    .map { info -> info?.json?.let(RoutinePowerVerbsCapability::parse) }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)

    val homePins: StateFlow<HomePinsState> = RemexClientManager.homePins

    val cardCornerRadius: StateFlow<Int> =
            settingsManager.cardCornerRadiusFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    com.clindsay94.remex.ui.theme.CardShapes.DEFAULT_CORNER_RADIUS_DP
            )

    /** Newest first; Recent activity reads the last finished run from it. */
    val routineHistory: StateFlow<List<RoutineRun>> = Routines.repository(application).history

    private val _sensors = MutableStateFlow<List<TelemetrySensor>>(emptyList())
    val sensors: StateFlow<List<TelemetrySensor>> = _sensors.asStateFlow()

    private val _uptime = MutableStateFlow<PcUptime?>(null)
    val uptime: StateFlow<PcUptime?> = _uptime.asStateFlow()

    /** One-line results for the snackbar: Wake, Lock and Sleep. Shown only while Home is started. */
    private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val messages: SharedFlow<String> = _messages.asSharedFlow()

    private val _isVisible = MutableStateFlow(false)

    fun setVisible(visible: Boolean) {
        _isVisible.value = visible
    }

    init {
        viewModelScope.launch {
            RemexClientManager.telemetry.collect { telemetryData ->
                if (!_isVisible.value) return@collect
                val parsed =
                        withContext(Dispatchers.Default) {
                            runCatching {
                                val sensors = parseTelemetry(JSONObject(telemetryData).optJSONArray("sensors")).sensors
                                sensors to HomeLogic.parseUptime(HomeLogic.uptimeTextOf(telemetryData))
                            }
                                    .onFailure { Log.w(TAG, "Failed to parse telemetry", it) }
                                    .getOrNull()
                        }
                if (parsed != null) {
                    _sensors.value = parsed.first
                    _uptime.value = parsed.second
                }
            }
        }
        // The telemetry replay outlives the connection; a disconnected PC has no readings and no uptime.
        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (!connected) {
                    _sensors.value = emptyList()
                    _uptime.value = null
                }
            }
        }
    }

    fun toggleConnection() = RemexClientManager.toggleConnection()

    fun setHomePin(sensorName: String, pinned: Boolean) = RemexClientManager.setHomePin(sensorName, pinned)

    /**
     * Sends Lock or Sleep, reporting the PC's answer the way the Commands grid does. Confirmation,
     * when an action needs it, is the screen's job before calling this ([HomeLogic.needsConfirmation]).
     */
    fun sendQuickAction(action: String) {
        if (action !in HomeLogic.QUICK_ACTIONS) return
        val app = getApplication<Application>()
        viewModelScope.launch {
            if (!RemexCoreClient.isLibraryLoaded) {
                _messages.tryEmit(app.getString(R.string.status_native_lib_not_loaded))
                return@launch
            }
            try {
                val request = JSONObject().put("action", action).put("parameters", JSONObject())
                val response = JSONObject(RemexCoreClient.SendCommand(request.toString()).getOrNull() ?: "{}")
                val message = response.optString("message", app.getString(R.string.rc_command_sent))
                _messages.tryEmit(
                        if (response.optBoolean("success", false)) app.getString(R.string.rc_success_format, message)
                        else app.getString(R.string.rc_failed_format, message)
                )
            } catch (e: Exception) {
                Log.w(TAG, "Sending $action from Home failed", e)
                _messages.tryEmit(app.getString(R.string.rc_error_format))
            }
        }
    }

    /**
     * Wake-on-LAN from the connect card. Reports the native layer's actual result rather than an
     * optimistic "sent" (RemEx-nbfb), the same as the Commands grid's Wake.
     */
    fun wakePc() {
        val app = getApplication<Application>()
        viewModelScope.launch {
            try {
                if (!RemexCoreClient.isLibraryLoaded) {
                    _messages.tryEmit(app.getString(R.string.wake_pc_lib_not_loaded))
                    return@launch
                }
                val mac = settingsManager.macAddressFlow.first()
                val broadcast = settingsManager.broadcastIpFlow.first()
                if (mac.isEmpty()) {
                    _messages.tryEmit(app.getString(R.string.wake_pc_mac_not_configured))
                    return@launch
                }
                val responseJson = RemexCoreClient.WakePc(mac, broadcast, 9).getOrNull() ?: ""
                val success = runCatching { JSONObject(responseJson).optBoolean("success", false) }.getOrDefault(false)
                _messages.tryEmit(app.getString(if (success) R.string.wake_pc_sent else R.string.wake_pc_failed))
            } catch (e: Throwable) {
                Log.e(TAG, "Failed to send Wake-on-LAN packet", e)
                _messages.tryEmit(app.getString(R.string.wake_pc_failed))
            }
        }
    }

    private companion object {
        const val TAG = "HomeVM"
    }
}
