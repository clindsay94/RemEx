package com.clindsay94.remex.ui.screens

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.json.JSONObject

private const val TAG = "RemoteDesktopVM"

data class DesktopFrame(val bitmap: Bitmap, val timestamp: Long = System.nanoTime())

data class RemoteDesktopCapabilityState(
        val supportsRemoteDesktop: Boolean = true,
        val unavailableReason: String? = null,
        val supportsCursorQuery: Boolean = false,
        val supportsAdvancedWindowControl: Boolean = false,
        val inputBackend: String? = null,
        val windowBackend: String? = null
)

data class RemoteDesktopConfigState(
        val quality: Int = 50,
        val targetFps: Int = 30,
        val scale: Float = 0.6f
)

data class DesktopWindowModel(
        val id: String,
        val title: String,
        val className: String? = null,
        val desktopNumber: Int? = null,
        val isActive: Boolean = false
)

class RemoteDesktopViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    private var hostScreenWidth: Int = 1920
    private var hostDesktopLeft: Int = 0
    private var hostDesktopTop: Int = 0
    private var hostScreenHeight: Int = 1080

    /** Reusable BitmapFactory options to reduce allocation pressure. */
    private val decodeOptions =
            BitmapFactory.Options().apply {
                inMutable = true
                inPreferredConfig = Bitmap.Config.RGB_565 // Half the memory of ARGB_8888
            }

    /** Previous frame bitmap kept for inBitmap reuse (avoids GC churn). */
    private var reusableBitmap: Bitmap? = null

    private val _currentFrame = MutableStateFlow<DesktopFrame?>(null)
    val currentFrame: StateFlow<DesktopFrame?> = _currentFrame.asStateFlow()

    private val _isStreaming = MutableStateFlow(false)
    val isStreaming: StateFlow<Boolean> = _isStreaming.asStateFlow()

    // Cursor position from host (for trackpad mode visibility).
    // Sentinel -1f means "not yet reported" so (0,0) is a valid visible position.
    private val _hostCursorX = MutableStateFlow(-1f)
    val hostCursorX: StateFlow<Float> = _hostCursorX.asStateFlow()

    private val _hostCursorY = MutableStateFlow(-1f)
    val hostCursorY: StateFlow<Float> = _hostCursorY.asStateFlow()

    private val _capabilityState = MutableStateFlow(RemoteDesktopCapabilityState())
    val capabilityState: StateFlow<RemoteDesktopCapabilityState> = _capabilityState.asStateFlow()

    private val _desktopError = MutableStateFlow<String?>(null)
    val desktopError: StateFlow<String?> = _desktopError.asStateFlow()

    private val _windowResults = MutableStateFlow<List<DesktopWindowModel>>(emptyList())
    val windowResults: StateFlow<List<DesktopWindowModel>> = _windowResults.asStateFlow()

    private val _windowActionError = MutableStateFlow<String?>(null)
    val windowActionError: StateFlow<String?> = _windowActionError.asStateFlow()

    private val frameTimestampsMs = ArrayDeque<Long>()
    private val _fps = MutableStateFlow(0f)
    val fps: StateFlow<Float> = _fps.asStateFlow()

    private fun recordFrameTimestamp() {
        val now = System.currentTimeMillis()
        frameTimestampsMs.addLast(now)
        while (frameTimestampsMs.size > 1 && now - frameTimestampsMs.first() > 2000L) {
            frameTimestampsMs.removeFirst()
        }
        if (frameTimestampsMs.size >= 2) {
            val elapsedSec = (now - frameTimestampsMs.first()) / 1000f
            _fps.value = (frameTimestampsMs.size - 1) / elapsedSec
        }
    }

    private val _configState = MutableStateFlow(RemoteDesktopConfigState())
    val configState: StateFlow<RemoteDesktopConfigState> = _configState.asStateFlow()

    val savedDesktopDefaults =
            settingsManager.remoteDesktopPreferencesFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    SettingsManager.RemoteDesktopPreferences()
            )

    val directTouch: StateFlow<Boolean> =
            settingsManager
                    .remoteDesktopPreferencesFlow
                    .map { it.directTouch }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), false)

    val pointerSpeed: StateFlow<Float> =
            settingsManager
                    .remoteDesktopPreferencesFlow
                    .map { it.pointerSpeed }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val verticalScrollSensitivity: StateFlow<Float> =
            settingsManager
                    .remoteDesktopPreferencesFlow
                    .map { it.verticalScrollSensitivity }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val horizontalScrollSensitivity: StateFlow<Float> =
            settingsManager
                    .remoteDesktopPreferencesFlow
                    .map { it.horizontalScrollSensitivity }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    /** Tracks reconnection attempts to avoid stacking. */
    private var reconnectJob: Job? = null
    private var reconnectAttempts = 0
    private val maxReconnectAttempts = 5

    init {
        viewModelScope.launch {
            settingsManager.remoteDesktopPreferencesFlow.collect { prefs ->
                _configState.value =
                        RemoteDesktopConfigState(
                                quality = prefs.quality.coerceIn(1, 100),
                                targetFps = prefs.targetFps.coerceIn(1, 360),
                                scale = prefs.scale.coerceIn(0.25f, 1.0f)
                        )
            }
        }

        viewModelScope.launch(Dispatchers.Default) {
            RemexClientManager.frames.collect { bytes -> decodeFrame(bytes) }
        }

        viewModelScope.launch {
            RemexClientManager.hostCapabilities.collect { hostInfo ->
                try {
                    val json = JSONObject(hostInfo)
                    _capabilityState.value =
                            RemoteDesktopCapabilityState(
                                    supportsRemoteDesktop =
                                            json.optBoolean("supportsRemoteDesktop", false),
                                    supportsCursorQuery =
                                            json.optBoolean("supportsCursorQuery", false),
                                    supportsAdvancedWindowControl =
                                            json.optBoolean("supportsAdvancedWindowControl", false),
                                    inputBackend =
                                            json.optString("inputBackend").takeIf { it.isNotBlank() },
                                    windowBackend =
                                            json.optString("windowControlBackend")
                                                    .takeIf { it.isNotBlank() },
                                    unavailableReason =
                                            json.optString("remoteDesktopUnavailableReason")
                                                    .takeIf { it.isNotBlank() }
                            )
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to parse host capabilities", e)
                    _capabilityState.value =
                            RemoteDesktopCapabilityState(
                                    supportsRemoteDesktop = false,
                                    supportsCursorQuery = false,
                                    supportsAdvancedWindowControl = false,
                                    unavailableReason = "Host metadata unavailable"
                            )
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopErrors.collect { errorText ->
                Log.e(TAG, "Desktop stream error: $errorText")
                _desktopError.value = errorText
                _isStreaming.value = false
                attemptReconnect()
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopMeta.collect { metaData ->
                try {
                    val json = JSONObject(metaData)
                    hostScreenWidth = json.optInt("screenWidth", 1920)
                    hostScreenHeight = json.optInt("screenHeight", 1080)
                    hostDesktopLeft = json.optInt("desktopLeft", 0)
                    hostDesktopTop = json.optInt("desktopTop", 0)
                    // Parse cursor position from host (for trackpad mode)
                    if (json.has("cursorX") && json.has("cursorY")) {
                        _hostCursorX.value = json.optDouble("cursorX", 0.0).toFloat()
                        _hostCursorY.value = json.optDouble("cursorY", 0.0).toFloat()
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to parse desktop meta", e)
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopWindowResults.collect { resultJson ->
                try {
                    val json = JSONObject(resultJson)
                    val success = json.optBoolean("success", false)
                    if (!success) {
                        _windowActionError.value =
                                json.optString("errorText").takeIf { it.isNotBlank() }
                                        ?: "Desktop window action failed"
                        return@collect
                    }

                    _windowActionError.value = null
                    val windowsArray = json.optJSONArray("windows")
                    if (windowsArray != null) {
                        val windows =
                                buildList {
                                    for (index in 0 until windowsArray.length()) {
                                        val item = windowsArray.optJSONObject(index) ?: continue
                                        add(
                                                DesktopWindowModel(
                                                        id = item.optString("id"),
                                                        title =
                                                                item.optString("title")
                                                                        .ifBlank { "(untitled)" },
                                                        className =
                                                                item.optString("className")
                                                                        .takeIf {
                                                                            it.isNotBlank()
                                                                        },
                                                        desktopNumber =
                                                                item.optInt("desktopNumber")
                                                                        .takeIf { item.has("desktopNumber") },
                                                        isActive =
                                                                item.optBoolean("isActive", false)
                                                )
                                        )
                                    }
                                }
                        _windowResults.value = windows
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to parse desktop window result", e)
                    _windowActionError.value = "Desktop window metadata unavailable"
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (!connected) {
                    _isStreaming.value = false
                    recycleCurrentFrame()
                    attemptReconnect()
                } else {
                    // Connection restored — reset reconnect counter
                    reconnectAttempts = 0
                    reconnectJob?.cancel()
                    reconnectJob = null
                }
            }
        }
    }

    /**
     * Decodes a JPEG frame using bitmap pooling to avoid OOM. Uses inBitmap for memory reuse when
     * dimensions match.
     */
    private fun decodeFrame(bytes: ByteArray) {
        if (bytes.isEmpty()) {
            Log.w(TAG, "decodeFrame: Received empty byte array")
            return
        }

        try {
            // Try to reuse the previous bitmap buffer
            val existing = reusableBitmap
            if (existing != null && !existing.isRecycled) {
                decodeOptions.inBitmap = existing
            } else {
                decodeOptions.inBitmap = null
            }

            val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, decodeOptions)
            if (decoded != null) {
                if (System.currentTimeMillis() % 1000 < 50) {
                    Log.d(
                            TAG,
                            "decodeFrame: Decoded frame, size: ${bytes.size} bytes, reused: ${decodeOptions.inBitmap != null}"
                    )
                }
                reusableBitmap = decoded
                // Wrap bitmap with a unique timestamp to bypass StateFlow equality checks
                _currentFrame.value = DesktopFrame(decoded)
                recordFrameTimestamp()
            } else {
                Log.e(TAG, "decodeFrame: BitmapFactory returned null for ${bytes.size} bytes")
                // Fallback: Reset reuse if decoding failed
                decodeOptions.inBitmap = null
            }
        } catch (e: IllegalArgumentException) {
            // inBitmap reuse failed (dimensions changed) — decode without reuse
            decodeOptions.inBitmap = null
            try {
                val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, decodeOptions)
                if (decoded != null) {
                    reusableBitmap?.takeIf { !it.isRecycled }?.recycle()
                    reusableBitmap = decoded
                    _currentFrame.value = DesktopFrame(decoded)
                    recordFrameTimestamp()
                }
            } catch (e2: Exception) {
                Log.e(TAG, "Frame decode failed after fallback", e2)
            }
        } catch (e: OutOfMemoryError) {
            Log.e(TAG, "OOM during frame decode — recycling buffers", e)
            recycleCurrentFrame()
            System.gc()
        } catch (e: Exception) {
            Log.w(TAG, "Frame decode failed", e)
        }
    }

    private fun recycleCurrentFrame() {
        _currentFrame.value = null
        // Don't recycle the bitmap eagerly — Compose may still be rendering the
        // previous frame.  Clearing the StateFlow reference lets the GC collect it
        // once the recomposition is done.
        reusableBitmap = null
    }

    /**
     * Attempts to restart the stream after transient network errors with exponential backoff (1s,
     * 2s, 4s, 8s, 16s).
     */
    private fun attemptReconnect() {
        if (!_capabilityState.value.supportsRemoteDesktop) return
        if (reconnectJob?.isActive == true) return
        if (reconnectAttempts >= maxReconnectAttempts) {
            _desktopError.value = "Connection lost. Tap Start to reconnect."
            return
        }

        reconnectJob =
                viewModelScope.launch {
                    val backoffMs = (1000L shl reconnectAttempts).coerceAtMost(16_000L)
                    reconnectAttempts++
                    Log.i(TAG, "Reconnect attempt $reconnectAttempts in ${backoffMs}ms")
                    delay(backoffMs)

                    if (RemexClientManager.isConnected.value &&
                                    _capabilityState.value.supportsRemoteDesktop
                    ) {
                        startStreaming()
                    }
                }
    }

    fun updateQuality(value: Int) {
        _configState.update { it.copy(quality = value.coerceIn(1, 100)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateTargetFps(value: Int) {
        _configState.update { it.copy(targetFps = value.coerceIn(1, 360)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateScale(value: Float) {
        _configState.update { it.copy(scale = value.coerceIn(0.25f, 1.0f)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateDirectTouch(enabled: Boolean) {
        viewModelScope.launch { settingsManager.saveRemoteDesktopDirectTouch(enabled) }
    }

    fun updatePointerSpeed(speed: Float) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopPointerSpeed(speed.coerceIn(0.25f, 3.0f))
        }
    }

    fun updateScrollSensitivity(vertical: Float, horizontal: Float) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopScrollSensitivity(
                    vertical.coerceIn(0.1f, 5.0f),
                    horizontal.coerceIn(0.1f, 5.0f)
            )
        }
    }

    fun startStreaming() {
        if (!RemexCoreClient.isLibraryLoaded) {
            _desktopError.value = "Native library not loaded"
            return
        }

        if (!_capabilityState.value.supportsRemoteDesktop) {
            _desktopError.value =
                    _capabilityState.value.unavailableReason
                            ?: "Remote desktop is unavailable on this host"
            return
        }

        val config = buildConfigJson()

        _desktopError.value = null
        reconnectAttempts = 0
        RemexCoreClient.StartDesktopStream(config.toString()).getOrNull()
        _isStreaming.value = true
    }

    fun stopStreaming() {
        reconnectJob?.cancel()
        reconnectJob = null
        reconnectAttempts = maxReconnectAttempts // Prevent auto-reconnect after manual stop

        // Set streaming to false FIRST so the UI stops referencing the frame,
        // then clear the frame reference.
        _isStreaming.value = false
        recycleCurrentFrame()

        if (RemexCoreClient.isLibraryLoaded) {
            RemexCoreClient.StopDesktopStream().getOrNull()
        }
    }

    private var accumulatedX = 0f
    private var accumulatedY = 0f

    fun sendMouseMove(deltaX: Float, deltaY: Float) {
        accumulatedX += deltaX
        accumulatedY += deltaY

        val intX = accumulatedX.toInt()
        val intY = accumulatedY.toInt()

        if (intX != 0 || intY != 0) {
            sendInput(
                    JSONObject().apply {
                        put("eventType", "mouseMove")
                        put("deltaX", intX)
                        put("deltaY", intY)
                    }
            )
            accumulatedX -= intX
            accumulatedY -= intY
        }
    }

    fun sendMouseScroll(deltaX: Int, deltaY: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseScroll")
                    put("deltaX", deltaX)
                    put("deltaY", deltaY)
                }
        )
    }

    fun sendMouseClick(button: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseClick")
                    put("button", button)
                }
        )
    }

    fun sendMouseDown(button: Int, x: Int? = null, y: Int? = null) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseDown")
                    put("button", button)
                    if (x != null) put("x", x)
                    if (y != null) put("y", y)
                }
        )
    }

    fun sendMouseUp(button: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseUp")
                    put("button", button)
                }
        )
    }

    fun sendMouseAbsolute(x: Int, y: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseMove")
                    put("x", x)
                    put("y", y)
                }
        )
    }

    fun sendMouseAbsoluteClick(button: Int, x: Int, y: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", "mouseClick")
                    put("button", button)
                    put("x", x)
                    put("y", y)
                }
        )
    }

    fun sendText(text: String) {
        if (text.isEmpty()) return
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

    fun getHostScreenSize(): Pair<Int, Int> = Pair(hostScreenWidth, hostScreenHeight)
    fun getHostDesktopOffset(): Pair<Int, Int> = Pair(hostDesktopLeft, hostDesktopTop)

    fun queryWindows(searchText: String = "", includeAllDesktops: Boolean = true, limit: Int = 25) {
        if (!_capabilityState.value.supportsAdvancedWindowControl) {
            _windowActionError.value = "Advanced window control is unavailable on this host"
            return
        }

        viewModelScope.launch(Dispatchers.IO) {
            val message =
                    JSONObject().apply {
                        put("type", "desktop_window_query")
                        put(
                                "desktopWindowQuery",
                                JSONObject().apply {
                                    put("requestId", java.util.UUID.randomUUID().toString().replace("-", ""))
                                    put("searchText", searchText)
                                    put("limit", limit.coerceIn(1, 100))
                                    put("includeAllDesktops", includeAllDesktops)
                                }
                        )
                    }
            RemexCoreClient.SendMessage(message.toString()).getOrNull()
        }
    }

    fun activateWindow(windowId: String) = sendWindowAction("activate", windowId)
    fun raiseWindow(windowId: String) = sendWindowAction("raise", windowId)
    fun minimizeWindow(windowId: String) = sendWindowAction("minimize", windowId)
    fun closeWindow(windowId: String) = sendWindowAction("close", windowId)

    fun resizeWindow(windowId: String, width: Int, height: Int) =
            sendWindowAction("resize", windowId) {
                put("width", width)
                put("height", height)
            }

    fun moveWindowToDesktop(windowId: String, desktopNumber: Int) =
            sendWindowAction("move_to_desktop", windowId) {
                put("desktopNumber", desktopNumber)
            }

    private fun pushConfigIfStreaming() {
        if (!_isStreaming.value || !RemexCoreClient.isLibraryLoaded) {
            return
        }

        try {
            val message =
                    JSONObject().apply {
                        put("type", "desktop_config")
                        put("desktopConfig", buildConfigJson())
                    }
            RemexCoreClient.SendMessage(message.toString()).getOrNull()
        } catch (e: Exception) {
            Log.w(TAG, "Failed to push config update", e)
        }
    }

    private fun persistDesktopDefaults() {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopDefaults(
                    quality = _configState.value.quality,
                    targetFps = _configState.value.targetFps,
                    scale = _configState.value.scale
            )
        }
    }

    private fun buildConfigJson(): JSONObject {
        return JSONObject().apply {
            put("quality", _configState.value.quality)
            put("scale", _configState.value.scale)
            put("targetFps", _configState.value.targetFps)
        }
    }

    private fun sendInput(input: JSONObject) {
        viewModelScope.launch(Dispatchers.IO) {
            if (!RemexCoreClient.isLibraryLoaded) {
                return@launch
            }

            val message =
                    JSONObject().apply {
                        put("type", "desktop_input")
                        put("inputEvent", input)
                    }
            RemexCoreClient.SendMessage(message.toString()).getOrNull()
        }
    }

    private fun sendWindowAction(
            action: String,
            windowId: String,
            configure: JSONObject.() -> Unit = {}
    ) {
        if (!_capabilityState.value.supportsAdvancedWindowControl) {
            _windowActionError.value = "Advanced window control is unavailable on this host"
            return
        }

        viewModelScope.launch(Dispatchers.IO) {
            val actionJson =
                    JSONObject().apply {
                        put("requestId", java.util.UUID.randomUUID().toString().replace("-", ""))
                        put("action", action)
                        put("windowId", windowId)
                        configure()
                    }

            val message =
                    JSONObject().apply {
                        put("type", "desktop_window_action")
                        put("desktopWindowAction", actionJson)
                    }

            RemexCoreClient.SendMessage(message.toString()).getOrNull()
        }
    }

    override fun onCleared() {
        super.onCleared()
        reconnectJob?.cancel()
        stopStreaming()
        recycleCurrentFrame()
    }
}
