package com.clindsay94.remex.ui.screens

import android.app.Application
import android.os.SystemClock
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
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
     * BE CLEAR ABOUT WHAT THIS DOES NOT BUY, because it is easy to read as more than it is. Ordering
     * is preserved only UP TO THE JNI CALL. `desktop_input` does not go through the outbound queue:
     * `AndroidNativeExports.HandleDesktopMessage` hands it to a fire-and-forget `Task.Run`, which
     * re-parallelises onto the .NET thread pool, and `RemexDesktopClient.SendMessageAsync` takes no
     * lock. So two sends issued in order here can still reach the socket out of order, or overlap
     * and have one dropped by the resulting swallowed exception. That is PRE-EXISTING and unchanged
     * by this — the point of the single-threaded dispatcher is to avoid adding a second, much wider
     * race on top of it, not to claim the first one is gone. Tracked as RemEx-krvz. (RemEx-3uhp)
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val sendDispatcher = Dispatchers.IO.limitedParallelism(1)

    private fun sendInput(input: JSONObject) {
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
