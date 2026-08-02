package com.clindsay94.remex.ui.screens

import android.app.Application
import android.os.SystemClock
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONObject

private const val TAG = "RemoteControlVM"

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
                _commandStatus.value = getApplication<Application>().getString(R.string.status_native_lib_not_loaded)
                return@launch
            }

            try {
                val mac = settingsManager.macAddressFlow.first()
                val broadcast = settingsManager.broadcastIpFlow.first()
                if (mac.isNotBlank()) {
                    val responseJson = RemexCoreClient.WakePc(mac, broadcast, 9).getOrNull() ?: ""
                    val response = JSONObject(responseJson)
                    val success = response.optBoolean("success", false)
                    val message = response.optString("message", getApplication<Application>().getString(R.string.widget_toast_wol_sent))
                    _commandStatus.value = if (success) {
                        getApplication<Application>().getString(R.string.rc_success_format, message)
                    } else {
                        getApplication<Application>().getString(R.string.rc_failed_format, message)
                    }
                } else {
                    _commandStatus.value = getApplication<Application>().getString(R.string.rc_failed_mac_not_configured)
                }
            } catch (e: Exception) {
                Log.w(TAG, "Sending a power command failed", e)
                _commandStatus.value = getApplication<Application>().getString(R.string.rc_error_format)
            }
        }
    }

    private fun sendWholePixelMouseMove(deltaX: Int, deltaY: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseMove")
                    put("deltaX", deltaX)
                    put("deltaY", deltaY)
                }
        )
    }

    private val mouseMoveThrottle = MouseMoveThrottle()

    /**
     * Feeds one frame of trackpad movement.
     *
     * Takes floats on purpose. The caller used to truncate each frame's delta to an Int before it got
     * here, which silently discarded any drag slower than one pixel per frame — see
     * [MouseMoveThrottle] for why that is the common case rather than an edge one.
     */
    fun sendMouseMove(deltaX: Float, deltaY: Float) {
        mouseMoveThrottle.onDelta(deltaX, deltaY, SystemClock.uptimeMillis())?.let {
            sendWholePixelMouseMove(it.x, it.y)
        }
    }

    /**
     * Sends whatever movement is still accumulated when a drag ends, so a gesture does not stop
     * short of where the user left it.
     */
    fun flushPendingMouseMove() {
        mouseMoveThrottle.flush(SystemClock.uptimeMillis())?.let {
            sendWholePixelMouseMove(it.x, it.y)
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

    fun sendKeyPress(keyCode: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "keyDown")
                    put("keyCode", keyCode)
                }
        )
        sendInput(
                JSONObject().apply {
                    put("eventType", "keyUp")
                    put("keyCode", keyCode)
                }
        )
    }

    fun clearCommandStatus() {
        _commandStatus.value = null
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
                _commandStatus.value = getApplication<Application>().getString(R.string.status_native_lib_not_loaded)
                return@launch
            }

            try {
                val parameters = JSONObject()
                if (delaySeconds > 0) {
                    parameters.put("DelaySeconds", delaySeconds.toString())
                }

                val request =
                        JSONObject().apply {
                            put("action", action)
                            put("parameters", parameters)
                        }

                val responseJson = RemexCoreClient.SendCommand(request.toString()).getOrNull() ?: "{}"
                val response = JSONObject(responseJson)
                val message = response.optString("message", getApplication<Application>().getString(R.string.rc_command_sent))
                val success = response.optBoolean("success", false)
                _commandStatus.value =
                        if (success) {
                            getApplication<Application>().getString(R.string.rc_success_format, message)
                        } else {
                            getApplication<Application>().getString(R.string.rc_failed_format, message)
                        }
            } catch (e: Exception) {
                // rc_failed_format keeps its placeholder because its other two uses interpolate
                // the HOST's own message, which is worth showing. Only this site had an exception
                // in it, so it moves to the placeholder-free key instead (RemEx-poj6).
                Log.w(TAG, "Sending a remote-control command failed", e)
                _commandStatus.value = getApplication<Application>().getString(R.string.rc_error_format)
            }
        }
    }

    /**
     * Asks the PC to capture its screen (RemEx-byij).
     *
     * **DELIBERATELY NOT [sendSystemCommand], THOUGH IT WOULD HAVE COMPILED.** That path reports the
     * `message` field the response carries, and for a command dispatch that field is
     * `"Command dispatched."` — a developer-English sentence minted by the native layer, not by the
     * host and never translated. Routed through it, a Spanish user would read "Éxito: Command
     * dispatched." for a brand-new feature. RemEx-66rf replaced that placeholder with the host's real
     * message, but the host does not translate its text either, so routing through [sendSystemCommand]
     * would still put English in front of eight of the nine languages. The other commands on this
     * screen still do that; this declines to join them.
     *
     * Says "taken", not "arrived": the PC answers once the PNG is written, and the file only reaches
     * this phone if the user then accepts the offer (RemEx-y7my).
     */
    fun takeScreenshot() {
        // No isLibraryLoaded early return, unlike [sendSystemCommand]. That branch reports
        // status_native_lib_not_loaded, which names an internal component; SendCommand already fails
        // closed in exactly that case, so the plainer failure message below covers it and says
        // something the person holding the phone can act on. Deliberate, not an omission.
        //
        // No dispatcher: SendCommand does its own thread switch (RemEx-66rf).
        viewModelScope.launch {
            val request =
                    JSONObject().apply {
                        put("action", "SCREENSHOT")
                        put("parameters", JSONObject())
                    }

            val captured =
                    runCatching {
                                JSONObject(
                                                RemexCoreClient.SendCommand(request.toString())
                                                        .getOrThrow()
                                        )
                                        .optBoolean("success", false)
                            }
                            .onFailure { Log.w(TAG, "Sending the screenshot command failed", it) }
                            .getOrDefault(false)

            _commandStatus.value =
                    getApplication<Application>()
                            .getString(
                                    if (captured) R.string.screenshot_taken
                                    else R.string.screenshot_failed
                            )
        }
    }

    /**
     * Off the main thread, but STRICTLY ONE AT A TIME.
     *
     * Building two JSONObjects, serialising them and crossing the JNI boundary is not main-thread
     * work, and at trackpad rates it happened often enough to be felt. But plain `Dispatchers.IO`
     * would be a correctness bug, not just a dispatch change: [sendKeyPress] issues keyDown and
     * keyUp as two SEPARATE sends, and on a 64-thread dispatcher they would race through tens of
     * microseconds of JSON build and JNI marshalling. This is why it was safe before:
     * `viewModelScope.launch {}` defaults to `Dispatchers.Main.immediate`, and
     * `RemexCoreClient.SendMessage` does not suspend, so each body ran inline and in order.
     * `limitedParallelism(1)` keeps that submission order while still taking the work off the main
     * thread.
     *
     * THIS IS ONE HALF OF THE ORDERING GUARANTEE, and it works only because of the other half.
     * `desktop_input` does not go through the outbound queue — `AndroidNativeExports` used to hand it
     * to a fire-and-forget `Task.Run` that re-parallelised onto the .NET thread pool, so two sends
     * issued in order here could still reach the socket inverted, or overlap and have one dropped.
     * RemEx-krvz replaced that with a single-consumer queue, so the native side now preserves
     * whatever order it is handed. This dispatcher is what makes the order it is handed the order the
     * user actually pressed the keys in. Neither half is sufficient alone. (RemEx-3uhp)
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val sendDispatcher = Dispatchers.IO.limitedParallelism(1)

    /**
     * The host's advertised capabilities, latest wins.
     *
     * DECLARED AFTER [sendDispatcher] AND AS A PROPERTY RATHER THAN IN AN `init` BLOCK, both on
     * purpose. This class has no `init` block, and `SendDispatcherDeclarationOrderTest` exists
     * because one that reached the send path synchronously was a real defect in the SIBLING view
     * model, RemoteDesktopViewModel - this class has never had one. Adding one above the dispatcher
     * would import that hazard rather than reintroduce it. A property initializer sidesteps the
     * question entirely.
     *
     * `SharingStarted.Eagerly`, NOT the `WhileSubscribed` used by the settings flows above, and the
     * difference is the whole thing working. Nothing ever collects this - it is read as `.value` at
     * send time - so under `WhileSubscribed` the upstream would never be subscribed to, `.value`
     * would sit at the initial state forever, and the gate below would be permanently open while
     * looking correct. The settings flows can use `WhileSubscribed` because Compose collects them.
     *
     * On a parse failure the state falls back to refusing input rather than to the default. That is
     * the opposite of the ABSENT-key rule, and deliberately: an absent key means an older host that
     * should keep working, whereas an unparseable payload means we know nothing, and the sibling
     * view model treats it the same way (RemEx-i8ty).
     */
    private val capabilityState =
            RemexClientManager.hostCapabilities
                    .map { hostInfo ->
                        try {
                            parseHostCapabilities(hostInfo)
                        } catch (e: Exception) {
                            Log.w(TAG, "Failed to parse host capabilities", e)
                            RemoteDesktopCapabilityState(
                                    supportsRemoteDesktop = false,
                                    supportsInputSimulation = false
                            )
                        }
                    }
                    .stateIn(
                            viewModelScope,
                            SharingStarted.Eagerly,
                            RemoteDesktopCapabilityState()
                    )

    /**
     * Whether the host will act on key presses, for the UI to reflect (RemEx-hulc).
     *
     * Exposed because [sendInput] DROPS every event when this is false. A media button that looks
     * live but is silently discarded is worse than one that is visibly unavailable: the phone still
     * buzzes on tap, so the user gets positive feedback for a command the PC never receives, and
     * nothing anywhere reports an error.
     *
     * THE INITIAL VALUE IS READ FROM THE SAME DEFAULT THE GATE USES, never written as a literal.
     * `RemoteDesktopCapabilityState.supportsInputSimulation` defaults to TRUE on purpose — an absent
     * key means an older host that should keep working — and `hostCapabilities` does not emit until
     * the first `host_info` arrives. A hardcoded `false` here therefore did not merely flash: until
     * that first message the gate was open while the UI told the user their PC could not accept key
     * presses, which is exactly the drift this flow exists to prevent. Reachable on any cold launch
     * with the PC asleep — and that is precisely when this screen gets opened, because Wake PC lives
     * on it.
     */
    val supportsInputSimulation: StateFlow<Boolean> =
            capabilityState
                    .map { it.supportsInputSimulation }
                    .stateIn(
                            viewModelScope,
                            SharingStarted.Eagerly,
                            RemoteDesktopCapabilityState().supportsInputSimulation
                    )

    private fun sendInput(input: JSONObject) {
        // THE ONLY WAY INPUT LEAVES THIS CLASS, so gating here covers mouseMove, mouseClick,
        // mouseScroll, typeText, keyDown and keyUp at once. This screen needs no video stream, so it
        // is reachable on a host where remote desktop is off entirely - which made it a WIDER
        // exposure than the remote-desktop path it was omitted from, not a narrower one (RemEx-i8ty,
        // found by the review of RemEx-q9zw).
        if (!capabilityState.value.supportsInputSimulation) return
        viewModelScope.launch(sendDispatcher) {
            if (RemexCoreClient.isLibraryLoaded) {
                val message =
                        JSONObject().apply {
                            put("type", "desktop_input")
                            put("inputEvent", input)
                        }
                RemexCoreClient.SendMessage(message.toString()).getOrNull()
            }
        }
    }
}
