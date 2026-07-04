package com.clindsay94.remex.ui.screens

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Base64
import android.util.Log
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.R
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

/** Frame-arrival watchdog poll interval and stall threshold (RemEx-5t4). */
private const val FRAME_WATCHDOG_POLL_MS = 1000L
private const val FRAME_STALL_TIMEOUT_MS = 7000L

/**
 * Virtual-key codes for the latching PC-key modifiers on the "PC keys" bar (RemEx-yi8o).
 * `internal` (not `private`) so [RemoteDesktopChordTest] can assert directly against the real
 * constant set rather than a hand-copied literal that could silently drift (RemEx-9krr).
 */
internal const val VK_SHIFT = 16
internal const val VK_CTRL = 17
internal const val VK_ALT = 18
internal const val VK_WIN = 91
/** VK_RMENU — right-Alt only, distinct from generic VK_ALT (RemEx-9krr). */
internal const val VK_ALTGR = 165
internal val MODIFIER_VIRTUAL_KEY_CODES = setOf(VK_SHIFT, VK_CTRL, VK_ALT, VK_WIN, VK_ALTGR)

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
        val quality: Int = 95,
        val targetFps: Int = 120,
        val scale: Float = 0.5f,
        val codec: String = "H264",
        val preset: DesktopPreset = DesktopPreset.SMOOTH_SHARP
)

/**
 * Named {quality, targetFps, scale} bundles for the remote-desktop stream (RemEx-vj31). CUSTOM has
 * no fixed bundle — selecting it just reveals the raw sliders and leaves the current values as-is.
 */
enum class DesktopPreset(val id: String) {
        UNLIMITED("unlimited"),
        SMOOTH_SHARP("smooth_sharp"),
        BALANCED("balanced"),
        DATA_SAVER("data_saver"),
        CUSTOM("custom");

        companion object {
                fun fromId(id: String): DesktopPreset = entries.find { it.id == id } ?: SMOOTH_SHARP
        }
}

data class DesktopPresetBundle(
        val preset: DesktopPreset,
        val quality: Int,
        val targetFps: Int,
        val scale: Float
)

/**
 * The four fixed presets in display order. Capture SCALE is the real FPS lever — host encode time
 * scales with pixel count — so scale 0.5 reliably sustains 120fps while scale 1.0 caps well below
 * it on a full-res monitor. Unlimited deliberately requests the uncapped/full-res bundle that can't
 * actually sustain its own target; that mismatch is what the overflow warning explains.
 */
val DESKTOP_PRESET_BUNDLES =
        listOf(
                DesktopPresetBundle(DesktopPreset.UNLIMITED, quality = 100, targetFps = 120, scale = 1.0f),
                DesktopPresetBundle(DesktopPreset.SMOOTH_SHARP, quality = 95, targetFps = 120, scale = 0.5f),
                DesktopPresetBundle(DesktopPreset.BALANCED, quality = 85, targetFps = 60, scale = 0.75f),
                DesktopPresetBundle(DesktopPreset.DATA_SAVER, quality = 60, targetFps = 30, scale = 0.65f)
        )

/**
 * A selectable remote-display target. [token] is the persisted identity:
 * "virtual" for the combined (both-screens) view, or "monitor:<displayId>" for a single display.
 */
data class DisplayTargetOption(
        val token: String,
        val label: String,
        val captureMode: String, // "VirtualDesktop" | "Monitor"
        val displayId: String?,
        val isPrimary: Boolean
)

data class DesktopWindowModel(
        val id: String,
        val title: String,
        val className: String? = null,
        val desktopNumber: Int? = null,
        val isActive: Boolean = false
)

/**
 * Latch state of a PC-key modifier on the "PC keys" bar (RemEx-yi8o). OFF: inactive, no effect.
 * ARMED: applies to the very next non-modifier key sent via [RemoteDesktopViewModel.sendKeyPress],
 * then auto-releases back to OFF. LOCKED: held physically down on the host (keyDown already sent)
 * until the chip is cycled back around to OFF.
 */
enum class ModifierState { OFF, ARMED, LOCKED }

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

    // Cursor visibility is tracked separately from position. We must NOT encode "hidden" as a
    // negative coordinate: a monitor positioned left of/above the primary has legitimately
    // negative virtual-desktop coordinates, so a -1 sentinel collides with a real cursor there
    // (it silently vanished on such secondary displays). The host reports visibility explicitly
    // (IsCursorInRegion), so we carry that flag through and let the overlay gate on it.
    private val _hostCursorVisible = MutableStateFlow(false)
    val hostCursorVisible: StateFlow<Boolean> = _hostCursorVisible.asStateFlow()

    // Native cursor overlay: the host streams the real cursor SHAPE (BGRA bitmap) keyed by a shape
    // serial, plus lightweight desktop_cursor_state position/visibility updates that reference the
    // active serial. The client decodes the shape once and draws it at the live position.
    private val _cursorShapeBitmap = MutableStateFlow<ImageBitmap?>(null)
    val cursorShapeBitmap: StateFlow<ImageBitmap?> = _cursorShapeBitmap.asStateFlow()

    private val _cursorHotspotX = MutableStateFlow(0)
    val cursorHotspotX: StateFlow<Int> = _cursorHotspotX.asStateFlow()

    private val _cursorHotspotY = MutableStateFlow(0)
    val cursorHotspotY: StateFlow<Int> = _cursorHotspotY.asStateFlow()

    private data class CursorShapeEntry(val bitmap: ImageBitmap, val hotspotX: Int, val hotspotY: Int)

    // Decoded shapes cached by serial; cursor-state messages pick the active one. Touched from two
    // collector coroutines, so use a concurrent map.
    private val cursorShapeCache = java.util.concurrent.ConcurrentHashMap<Long, CursorShapeEntry>()
    @Volatile private var activeCursorShapeSerial = -1L

    private val _capabilityState = MutableStateFlow(RemoteDesktopCapabilityState())
    val capabilityState: StateFlow<RemoteDesktopCapabilityState> = _capabilityState.asStateFlow()

    private val _desktopError = MutableStateFlow<String?>(null)
    val desktopError: StateFlow<String?> = _desktopError.asStateFlow()

    private val _windowResults = MutableStateFlow<List<DesktopWindowModel>>(emptyList())
    val windowResults: StateFlow<List<DesktopWindowModel>> = _windowResults.asStateFlow()

    private val _windowActionError = MutableStateFlow<String?>(null)
    val windowActionError: StateFlow<String?> = _windowActionError.asStateFlow()

    private val _modifierStates = MutableStateFlow<Map<Int, ModifierState>>(emptyMap())
    /** Latch state of each PC-key modifier, keyed by virtual-key code. Absent from the map == OFF. */
    val modifierStates: StateFlow<Map<Int, ModifierState>> = _modifierStates.asStateFlow()

    /** Modifiers currently held physically down on the host (keyDown sent, no keyUp yet) (RemEx-yi8o). */
    private val physicallyDownModifiers = mutableSetOf<Int>()

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

    // --- Frame-arrival watchdog (RemEx-5t4) ---
    // A display/target switch — or a host slow to tear down the previous DXGI capture — can leave the
    // stream silently black: StartDesktopStream returns, but no frames arrive AND no desktopError is
    // emitted, so neither the error path nor reconnect ever fires (the unplugged-monitor case self-
    // heals because the host emits an error; the host-slow case does not). The watchdog arms on stream
    // start, is reset by every decoded frame, and triggers a reconnect if no frame arrives within the
    // stall timeout. It also backstops the H.264 decoder-init silent-death path (RemEx-x0b).
    @Volatile private var lastFrameArrivalMs = 0L
    private var frameWatchdogJob: Job? = null

    private fun onFrameArrived() {
        lastFrameArrivalMs = System.currentTimeMillis()
    }

    /**
     * Routes one frame's payload to the decoder matching [codec] ("H264" / "Mjpeg"). An H.264 frame
     * that arrives before its decoder surface exists (the brief codec-switch window) is dropped rather
     * than mis-fed to the JPEG path — subsequent frames decode once the surface is created. (RemEx-w5v)
     */
    private fun routeFrameByCodec(codec: String, payload: ByteArray) {
        if (codec == RemoteDesktopFrameEnvelope.CODEC_H264) {
            val decoder = activeH264Decoder
            if (decoder != null) {
                decoder.decodeFrame(payload)
                recordFrameTimestamp()
            }
        } else {
            decodeFrame(payload)
        }
    }

    private fun armFrameWatchdog() {
        frameWatchdogJob?.cancel()
        lastFrameArrivalMs = System.currentTimeMillis()
        frameWatchdogJob =
                viewModelScope.launch {
                    // delay() is cancellable, so cancelling the job (stop/clear) exits the loop.
                    while (true) {
                        delay(FRAME_WATCHDOG_POLL_MS)
                        if (!_isStreaming.value) continue
                        // A reconnect already in flight will re-arm the watchdog on the next start.
                        if (reconnectJob?.isActive == true) continue
                        val gap = System.currentTimeMillis() - lastFrameArrivalMs
                        if (gap >= FRAME_STALL_TIMEOUT_MS) {
                            Log.w(TAG, "No decoded frame in ${gap}ms; stream appears stalled — reconnecting.")
                            attemptReconnect()
                        }
                    }
                }
    }

    private fun cancelFrameWatchdog() {
        frameWatchdogJob?.cancel()
        frameWatchdogJob = null
    }

    /**
     * Maps a host desktop-error string to a localized message. The host may tag errors as
     * "code\u001Farg\u001FenglishFallback" (see Remex.Core DesktopErrorCodes); untagged/legacy errors
     * and unknown codes fall back to the host's plain English text. (RemEx-728)
     */
    private fun localizeDesktopError(raw: String?): String? {
        if (raw.isNullOrEmpty()) return raw
        val parts = raw.split('\u001F')
        if (parts.size < 3) return raw // untagged legacy text — show as-is
        val code = parts[0]
        val arg = parts[1]
        val fallback = parts.subList(2, parts.size).joinToString("\u001F")
        val app = getApplication<Application>()
        return when (code) {
            "capture_unavailable" -> app.getString(R.string.rd_err_capture_unavailable)
            "capture_stopped" ->
                    arg.toIntOrNull()?.let { app.getString(R.string.rd_err_capture_stopped, it) }
                            ?: fallback
            "target_unavailable" -> app.getString(R.string.rd_err_target_unavailable)
            "target_switch_unsupported" ->
                    app.getString(R.string.rd_err_target_switch_unsupported)
            "runtime_unavailable" -> app.getString(R.string.rd_err_runtime_unavailable)
            else -> fallback.ifEmpty { raw }
        }
    }

    private val _configState = MutableStateFlow(RemoteDesktopConfigState())
    val configState: StateFlow<RemoteDesktopConfigState> = _configState.asStateFlow()

    private val _activeCodecState = MutableStateFlow("Mjpeg")
    val activeCodecState: StateFlow<String> = _activeCodecState.asStateFlow()

    // Available remote-display targets (populated from the host display catalog) and the
    // currently selected target token. Empty until the first catalog arrives.
    private val _displayTargets = MutableStateFlow<List<DisplayTargetOption>>(emptyList())
    val displayTargets: StateFlow<List<DisplayTargetOption>> = _displayTargets.asStateFlow()

    private val _selectedDisplayToken = MutableStateFlow("")
    val selectedDisplayToken: StateFlow<String> = _selectedDisplayToken.asStateFlow()

    /** True once a display catalog has been received this connection. */
    private var displaysLoaded = false
    /** Guards against firing duplicate catalog queries while one is in flight. */
    private var catalogRequested = false
    /** Desired target token loaded from persisted prefs; resolved against the catalog on arrival. */
    private var desiredDisplayToken: String = ""
    /** Set when startStreaming() is waiting for the catalog before it can begin. */
    private var pendingStreamStart = false
    private var catalogTimeoutJob: Job? = null

    private val _streamPixelWidth = MutableStateFlow(1920)
    val streamPixelWidth: StateFlow<Int> = _streamPixelWidth.asStateFlow()

    private val _streamPixelHeight = MutableStateFlow(1080)
    val streamPixelHeight: StateFlow<Int> = _streamPixelHeight.asStateFlow()

    // False until the host delivers real desktop metadata (screen dimensions) for the CURRENT
    // stream. The initial fit/zoom must wait for this so it never computes the framing against the
    // 1920x1080 placeholders above — those exist only to give the H.264 decoder a valid surface
    // before metadata arrives. Reset on every fresh stream start, set true on first real meta. (RemEx-4k4)
    private val _desktopMetaReady = MutableStateFlow(false)
    val desktopMetaReady: StateFlow<Boolean> = _desktopMetaReady.asStateFlow()

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

    val cursorScale: StateFlow<Float> =
            settingsManager
                    .remoteDesktopPreferencesFlow
                    .map { it.cursorScale }
                    .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    /** Tracks reconnection attempts to avoid stacking. */
    private var reconnectJob: Job? = null
    private var reconnectAttempts = 0
    private val maxReconnectAttempts = 5

    var activeH264Decoder: H264StreamDecoder? = null

    /**
     * Clears the active H.264 decoder reference ONLY if it still points at [decoder].
     * Called when a TextureView surface is destroyed. During a key()-driven AndroidView
     * rebuild (stream resolution change), the old surface's destroy callback can fire after
     * the new surface has already published its decoder; an unconditional null-out would
     * clobber the live decoder and starve the stream. The identity guard prevents that.
     */
    fun onH264DecoderReleased(decoder: H264StreamDecoder) {
        if (activeH264Decoder === decoder) {
            activeH264Decoder = null
        }
    }

    /**
     * Called when an [H264StreamDecoder] fails to initialize its hardware MediaCodec. The decoder has
     * already released itself, so the stream would otherwise hang on a black screen with no recovery.
     * Mark streaming stopped and trigger a backoff reconnect (which, after maxReconnectAttempts,
     * surfaces a clear localized "connection lost" error). (RemEx-x0b)
     */
    fun onH264DecoderInitFailed() {
        Log.e(TAG, "H.264 decoder failed to initialize; triggering reconnect.")
        activeH264Decoder = null
        _isStreaming.value = false
        attemptReconnect()
    }

    init {
        viewModelScope.launch {
            settingsManager.remoteDesktopPreferencesFlow.collect { prefs ->
                _configState.value =
                        RemoteDesktopConfigState(
                                quality = prefs.quality.coerceIn(1, 100),
                                targetFps = prefs.targetFps.coerceIn(1, 120),
                                scale = prefs.scale.coerceIn(0.25f, 1.0f),
                                preset = DesktopPreset.fromId(prefs.preset)
                        )
                desiredDisplayToken = prefs.displayTarget
            }
        }

        viewModelScope.launch(Dispatchers.Default) {
            RemexClientManager.frames.collect { bytes ->
                // Prefer the per-frame codec tag from the host's frame envelope, so a frame is always
                // decoded by the codec that actually produced it — even mid codec-switch, before the
                // metadata flip arrives. Legacy hosts send raw, untagged bytes (tryRead == null);
                // route those by the negotiated codec as before. (RemEx-w5v)
                val env = RemoteDesktopFrameEnvelope.tryRead(bytes)
                if (env != null) {
                    routeFrameByCodec(env.codec, env.payload)
                } else {
                    routeFrameByCodec(_activeCodecState.value, bytes)
                }
                // Reset the stall watchdog on every arriving frame, regardless of codec. (RemEx-5t4)
                onFrameArrived()
            }
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
                                            json.optString("inputBackend").takeIf {
                                                it.isNotBlank()
                                            },
                                    windowBackend =
                                            json.optString("windowControlBackend").takeIf {
                                                it.isNotBlank()
                                            },
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
                                    unavailableReason = getApplication<Application>().getString(R.string.status_metadata_unavailable)
                            )
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopDisplayCatalog.collect { catalogJson ->
                handleDisplayCatalog(catalogJson)
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopCursorShape.collect { shapeJson ->
                handleCursorShape(shapeJson)
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopCursorState.collect { stateJson ->
                handleCursorState(stateJson)
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopCursorBinary.collect { packet ->
                handleCursorBinary(packet)
            }
        }

        viewModelScope.launch {
            RemexClientManager.desktopErrors.collect { errorText ->
                Log.e(TAG, "Desktop stream error: $errorText")
                _desktopError.value = localizeDesktopError(errorText)
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
                    // Real dimensions are now in hand for this stream — unblock the initial fit so it
                    // frames against the true monitor size instead of the 1920x1080 placeholder. (RemEx-4k4)
                    _desktopMetaReady.value = true
                    // Parse cursor position from host (for trackpad mode). When the host marks the
                    // cursor as outside the captured surface (e.g. on another monitor in single-
                    // display capture), hide the overlay via the hostCursorVisible flag. We keep the
                    // last raw coordinates (which may be negative on monitors left of/above primary)
                    // rather than overwriting them with a sentinel. Legacy hosts omit cursorVisible
                    // -> treated as visible.
                    if (json.has("cursorX") && json.has("cursorY")) {
                        val cursorVisible = json.optBoolean("cursorVisible", true)
                        _hostCursorVisible.value = cursorVisible
                        if (cursorVisible) {
                            _hostCursorX.value = json.optDouble("cursorX", 0.0).toFloat()
                            _hostCursorY.value = json.optDouble("cursorY", 0.0).toFloat()
                        }
                    }
                    // Only update the codec when codecInfo is actually present. Live cursor-position
                    // updates arrive as lightweight DesktopMeta messages with no codecInfo, and must
                    // NOT reset the codec to Mjpeg — that would break an active H.264 stream.
                    val codecInfo = json.optJSONObject("codecInfo")
                    if (codecInfo != null) {
                        _activeCodecState.value = codecInfo.optString("codec", "Mjpeg")
                    }
                    // Parse encoded stream pixel dimensions for H.264 decoder initialization.
                    // Like codecInfo above, lightweight cursor-position DesktopMeta updates carry no
                    // resolution — the host serializes PixelWidth/PixelHeight as 0 on those — and must
                    // NOT clobber the active stream resolution. streamPixelWidth/Height key the H264
                    // decoder's AndroidView; a 0x0 value rebuilds it with an invalid surface, which
                    // fails to decode and triggers a teardown/reconnect loop (the landscape<->portrait
                    // flip). Only adopt a positive, real resolution.
                    val pixelWidth = json.optInt("pixelWidth", 0)
                    val pixelHeight = json.optInt("pixelHeight", 0)
                    if (pixelWidth > 0 && pixelHeight > 0) {
                        _streamPixelWidth.value = pixelWidth
                        _streamPixelHeight.value = pixelHeight
                    }
                    Log.i(TAG, "Desktop stream metadata parsed: activeCodec=${_activeCodecState.value}, streamRes=${_streamPixelWidth.value}x${_streamPixelHeight.value}")
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
                                        ?: getApplication<Application>().getString(R.string.remote_desktop_action_failed)
                        return@collect
                    }

                    _windowActionError.value = null
                    val windowsArray = json.optJSONArray("windows")
                    if (windowsArray != null) {
                        val windows = buildList {
                            for (index in 0 until windowsArray.length()) {
                                val item = windowsArray.optJSONObject(index) ?: continue
                                add(
                                        DesktopWindowModel(
                                                id = item.optString("id"),
                                                title =
                                                        item.optString("title").ifBlank {
                                                            getApplication<Application>().getString(R.string.remote_desktop_untitled_window)
                                                        },
                                                className =
                                                        item.optString("className").takeIf {
                                                            it.isNotBlank()
                                                        },
                                                desktopNumber =
                                                        item.optInt("desktopNumber").takeIf {
                                                            item.has("desktopNumber")
                                                        },
                                                isActive = item.optBoolean("isActive", false)
                                        )
                                )
                            }
                        }
                        _windowResults.value = windows
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to parse desktop window result", e)
                    _windowActionError.value = getApplication<Application>().getString(R.string.remote_desktop_metadata_unavailable)
                }
            }
        }

        viewModelScope.launch {
            RemexClientManager.isConnected.collect { connected ->
                if (!connected) {
                    // The socket is already down, so keyUp sends below are best-effort, but the
                    // local latch state MUST reset — otherwise a chip could read LOCKED against a
                    // host that no longer has any idea a key is held (RemEx-yi8o).
                    releaseAllModifiers()
                    _isStreaming.value = false
                    recycleCurrentFrame()
                    // Force a fresh catalog query on the next connection.
                    catalogRequested = false
                    displaysLoaded = false
                    attemptReconnect()
                } else {
                    // Connection restored — reset reconnect counter
                    reconnectAttempts = 0
                    reconnectJob?.cancel()
                    reconnectJob = null
                    // Pre-load the display catalog so the picker is available in settings
                    // before the user starts a stream.
                    ensureDisplayCatalogLoaded()
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
            _desktopError.value = getApplication<Application>().getString(R.string.remote_desktop_connection_lost)
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
        _configState.update { it.copy(targetFps = value.coerceIn(1, 120)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    fun updateScale(value: Float) {
        _configState.update { it.copy(scale = value.coerceIn(0.25f, 1.0f)) }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    /**
     * Applies a named preset in one shot — sets quality, target FPS, capture scale, and the preset
     * id together (persist + push once) instead of three separate slider writes. The capture SCALE
     * is the real FPS lever: the host's rawvideo->ffmpeg encode time scales with pixel count, so a
     * lower scale is what unlocks 120fps on a full-res monitor while the phone downscales for
     * display anyway. (RemEx-4k4 fps, RemEx-vj31 named presets)
     */
    fun applyDesktopPreset(preset: DesktopPreset, quality: Int, fps: Int, scale: Float) {
        _configState.update {
            it.copy(
                quality = quality.coerceIn(1, 100),
                targetFps = fps.coerceIn(1, 120),
                scale = scale.coerceIn(0.25f, 1.0f),
                preset = preset
            )
        }
        persistDesktopDefaults()
        pushConfigIfStreaming()
    }

    /**
     * Switches to Custom without touching the current quality/fps/scale — it just stops treating
     * them as a named bundle so the raw sliders in the settings sheet become editable. (RemEx-vj31)
     */
    fun selectCustomPreset() {
        _configState.update { it.copy(preset = DesktopPreset.CUSTOM) }
        persistDesktopDefaults()
    }

    /** Whether the one-time Unlimited overflow warning has already been shown (RemEx-vj31). */
    val hasShownUnlimitedWarning: StateFlow<Boolean> =
            settingsManager.unlimitedWarningShownFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    false
            )

    fun markUnlimitedWarningShown() {
        viewModelScope.launch { settingsManager.markUnlimitedWarningShown() }
    }

    fun updateDirectTouch(enabled: Boolean) {
        viewModelScope.launch { settingsManager.saveRemoteDesktopDirectTouch(enabled) }
    }

    fun updatePointerSpeed(speed: Float) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopPointerSpeed(speed.coerceIn(0.25f, 3.0f))
        }
    }

    fun updateCursorScale(value: Float) {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopCursorScale(value.coerceIn(0.5f, 2.5f))
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
            _desktopError.value = getApplication<Application>().getString(R.string.status_native_lib_not_loaded)
            return
        }

        if (!_capabilityState.value.supportsRemoteDesktop) {
            _desktopError.value =
                    _capabilityState.value.unavailableReason
                            ?: getApplication<Application>().getString(R.string.remote_desktop_unavailable_fallback)
            return
        }

        // Display selection requires the host's display catalog before we can name a target.
        // Query it first; once it arrives (handleDisplayCatalog) the stream starts with the
        // chosen target. If the catalog never arrives, fall back to a legacy (host-default) start.
        if (!displaysLoaded) {
            _desktopError.value = null
            pendingStreamStart = true
            requestDisplayCatalog()
            scheduleCatalogTimeoutFallback()
            return
        }

        actuallyStartStreaming()
    }

    private fun actuallyStartStreaming() {
        catalogTimeoutJob?.cancel()
        catalogTimeoutJob = null
        pendingStreamStart = false

        val config = buildConfigJson()
        _desktopError.value = null
        // Wait for THIS stream's metadata before the initial fit fires (RemEx-4k4).
        _desktopMetaReady.value = false
        reconnectAttempts = 0
        RemexCoreClient.StartDesktopStream(config.toString()).getOrNull()
        _isStreaming.value = true
        // Arm the stall watchdog so a silently-black start (no frames, no error) self-recovers. (RemEx-5t4)
        armFrameWatchdog()
    }

    /**
     * Eagerly loads the display catalog (e.g. as soon as the device connects) so the display
     * picker is available in settings before the first stream. No-op if already loaded, a
     * request is in flight, or a stream start is already driving its own query.
     */
    fun ensureDisplayCatalogLoaded() {
        if (displaysLoaded || catalogRequested || pendingStreamStart) return
        if (!RemexCoreClient.isLibraryLoaded) return
        if (!_capabilityState.value.supportsRemoteDesktop) return
        requestDisplayCatalog()
    }

    /** Asks the host for its catalog of displays / capture modes over the desktop channel. */
    private fun requestDisplayCatalog() {
        if (!RemexCoreClient.isLibraryLoaded) return
        catalogRequested = true
        Log.i(TAG, "Requesting display catalog from host")
        viewModelScope.launch(Dispatchers.IO) {
            val message = JSONObject().apply { put("type", "desktop_display_query") }
            RemexCoreClient.SendMessage(message.toString()).getOrNull()
        }
    }

    /** If the catalog doesn't arrive promptly, start anyway in legacy (host-default) mode. */
    private fun scheduleCatalogTimeoutFallback() {
        catalogTimeoutJob?.cancel()
        catalogTimeoutJob =
                viewModelScope.launch {
                    delay(4000)
                    if (pendingStreamStart && !displaysLoaded) {
                        Log.w(TAG, "Display catalog timed out — starting in legacy mode")
                        actuallyStartStreaming()
                    }
                }
    }

    private fun handleDisplayCatalog(catalogJson: String) {
        try {
            val json = JSONObject(catalogJson)
            val modes = json.optJSONArray("supportedCaptureModes")
            val supportsVirtual =
                    modes != null &&
                            (0 until modes.length()).any {
                                modes.optString(it) == "VirtualDesktop"
                            }
            val arr = json.optJSONArray("displays")
            val options = mutableListOf<DisplayTargetOption>()
            val displayCount = arr?.length() ?: 0

            if (arr != null) {
                for (i in 0 until displayCount) {
                    val d = arr.optJSONObject(i) ?: continue
                    val displayId = d.optString("displayId")
                    if (displayId.isBlank()) continue
                    val isPrimary = d.optBoolean("isPrimary", false)
                    val label =
                            if (isPrimary)
                                    getApplication<Application>()
                                            .getString(R.string.remote_desktop_display_primary)
                            else
                                    getApplication<Application>()
                                            .getString(R.string.remote_desktop_display_numbered, i + 1)
                    options.add(
                            DisplayTargetOption(
                                    token = "monitor:$displayId",
                                    label = label,
                                    captureMode = "Monitor",
                                    displayId = displayId,
                                    isPrimary = isPrimary
                            )
                    )
                }
            }

            // Offer the combined "both screens" view when the host supports it and there is
            // more than one physical display to combine.
            if (supportsVirtual && displayCount > 1) {
                options.add(
                        DisplayTargetOption(
                                token = "virtual",
                                label =
                                        getApplication<Application>()
                                                .getString(R.string.remote_desktop_display_both),
                                captureMode = "VirtualDesktop",
                                displayId = null,
                                isPrimary = false
                        )
                )
            }

            if (options.isEmpty()) {
                Log.w(TAG, "Display catalog had no usable targets")
                if (pendingStreamStart) actuallyStartStreaming()
                return
            }

            Log.i(
                    TAG,
                    "Display catalog received: $displayCount display(s), ${options.size} option(s), supportsVirtual=$supportsVirtual"
            )
            _displayTargets.value = options

            // Resolve the selection: remembered choice → primary monitor → first option.
            val resolved =
                    options.firstOrNull { it.token == desiredDisplayToken }
                            ?: options.firstOrNull {
                                it.captureMode == "Monitor" && it.isPrimary
                            }
                            ?: options.first()
            _selectedDisplayToken.value = resolved.token
            displaysLoaded = true

            if (pendingStreamStart) {
                actuallyStartStreaming()
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to parse display catalog", e)
            if (pendingStreamStart) actuallyStartStreaming()
        }
    }

    private fun selectedDisplayOption(): DisplayTargetOption? =
            _displayTargets.value.firstOrNull { it.token == _selectedDisplayToken.value }

    /**
     * Selects a remote display/capture target. Persists the choice and, if a stream is active,
     * restarts it on the new target (the host re-captures the chosen surface).
     */
    fun selectDisplayTarget(token: String) {
        val option = _displayTargets.value.firstOrNull { it.token == token } ?: return
        if (option.token == _selectedDisplayToken.value) return

        _selectedDisplayToken.value = option.token
        desiredDisplayToken = option.token
        viewModelScope.launch { settingsManager.saveRemoteDesktopDisplayTarget(option.token) }

        if (_isStreaming.value) {
            restartStreamWithCurrentTarget()
        }
    }

    private fun restartStreamWithCurrentTarget() {
        viewModelScope.launch(Dispatchers.IO) {
            reconnectJob?.cancel()
            reconnectAttempts = 0
            if (RemexCoreClient.isLibraryLoaded) {
                RemexCoreClient.StopDesktopStream().getOrNull()
            }
            // Give the host a moment to tear down the previous capture session.
            delay(200)
            actuallyStartStreaming()
        }
    }

    fun stopStreaming() {
        // Release any latched PC-key modifiers before tearing the session down so a LOCKED
        // Ctrl/Alt/Shift/Win never leaves a key stuck down on the host (RemEx-yi8o).
        releaseAllModifiers()

        reconnectJob?.cancel()
        reconnectJob = null
        reconnectAttempts = maxReconnectAttempts // Prevent auto-reconnect after manual stop
        cancelFrameWatchdog()

        catalogTimeoutJob?.cancel()
        catalogTimeoutJob = null
        pendingStreamStart = false
        // Re-query the catalog on the next start so the target list / primary reflects the
        // host's current monitor configuration. The last-known list stays visible in the UI.
        displaysLoaded = false

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

    /**
     * Sends a pre-serialized `desktop_pointer_batch` JSON envelope to the host. Called by the raw
     * stylus input path in the Screen composable.
     *
     * @param batchJson A fully-formed JSON string from [RemoteDesktopPointerSerializer.toBatchJson]
     * .
     */
    fun sendPointerBatch(batchJson: String) {
        viewModelScope.launch(Dispatchers.IO) {
            if (!RemexCoreClient.isLibraryLoaded) return@launch
            RemexCoreClient.SendDesktopPointerBatch(batchJson).getOrNull()
        }
    }

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
        spendArmedModifiers()
    }

    private fun sendKeyEvent(eventType: String, keyCode: Int) {
        sendInput(
                JSONObject().apply {
                    put("eventType", eventType)
                    put("keyCode", keyCode)
                }
        )
    }

    /**
     * Sends a key press, automatically wrapping it with any ARMED or LOCKED PC-key modifiers
     * (Ctrl/Alt/Shift/Win) currently latched via [cycleModifier] (RemEx-yi8o). A modifier not
     * already held physically down is pressed first; LOCKED modifiers stay down afterwards, ARMED
     * modifiers are released and reset to OFF once this key has been sent. Modifier keycodes
     * themselves are never wrapped — they are only latched through [cycleModifier], not this
     * function.
     */
    fun sendKeyPress(keyCode: Int) {
        if (keyCode in MODIFIER_VIRTUAL_KEY_CODES) {
            sendKeyEvent("keyDown", keyCode)
            sendKeyEvent("keyUp", keyCode)
            return
        }

        val chord =
                computeChordApplication(
                        modifierStates = _modifierStates.value,
                        physicallyDownModifiers = physicallyDownModifiers,
                        keyCode = keyCode,
                        modifierVirtualKeyCodes = MODIFIER_VIRTUAL_KEY_CODES
                )
        chord.toPress.forEach { vk ->
            physicallyDownModifiers.add(vk)
            sendKeyEvent("keyDown", vk)
        }

        sendKeyEvent("keyDown", keyCode)
        sendKeyEvent("keyUp", keyCode)

        if (chord.toRelease.isNotEmpty()) {
            chord.toRelease.forEach { vk ->
                physicallyDownModifiers.remove(vk)
                sendKeyEvent("keyUp", vk)
            }
            _modifierStates.value = chord.nextModifierStates
        }
    }

    /**
     * Cycles a PC-key modifier through OFF → ARMED → LOCKED → OFF (RemEx-yi8o). Transitioning into
     * LOCKED presses the key down on the host immediately and holds it; transitioning out of
     * LOCKED releases it. ARMED presses nothing yet — [sendKeyPress] applies it lazily to the very
     * next non-modifier key.
     */
    fun cycleModifier(vk: Int) {
        val current = _modifierStates.value[vk] ?: ModifierState.OFF
        val next =
                when (current) {
                    ModifierState.OFF -> ModifierState.ARMED
                    ModifierState.ARMED -> ModifierState.LOCKED
                    ModifierState.LOCKED -> ModifierState.OFF
                }

        when (next) {
            ModifierState.LOCKED -> {
                if (physicallyDownModifiers.add(vk)) {
                    sendKeyEvent("keyDown", vk)
                }
            }
            ModifierState.OFF -> {
                if (physicallyDownModifiers.remove(vk)) {
                    sendKeyEvent("keyUp", vk)
                }
            }
            ModifierState.ARMED -> Unit // Not physically pressed yet.
        }

        _modifierStates.update { it + (vk to next) }
    }

    /**
     * Releases every latched PC-key modifier — sending keyUp for anything physically held down on
     * the host and resetting all latch state to OFF. Called whenever the desktop session ends
     * (manual stop, disconnect, or ViewModel clear) so a LOCKED modifier never leaves a key stuck
     * down on the host after the session goes away (RemEx-yi8o).
     */
    fun releaseAllModifiers() {
        physicallyDownModifiers.toList().forEach { vk -> sendKeyEvent("keyUp", vk) }
        physicallyDownModifiers.clear()
        if (_modifierStates.value.isNotEmpty()) {
            _modifierStates.value = emptyMap()
        }
    }

    /**
     * Resets every ARMED (one-shot) PC-key modifier to OFF without touching LOCKED ones and
     * without sending any key events — ARMED modifiers are never physically held down
     * (RemEx-rimh). Text injection bypasses key events entirely, so an ARMED modifier can never
     * apply to it; its one-shot chance is spent, and leaving it latched would silently attach it
     * to a later, unrelated keystroke.
     */
    private fun spendArmedModifiers() {
        if (_modifierStates.value.none { it.value == ModifierState.ARMED }) return
        _modifierStates.update { states ->
            states.mapValues { (_, state) ->
                if (state == ModifierState.ARMED) ModifierState.OFF else state
            }
        }
    }

    fun getHostScreenSize(): Pair<Int, Int> = Pair(hostScreenWidth, hostScreenHeight)
    fun getHostDesktopOffset(): Pair<Int, Int> = Pair(hostDesktopLeft, hostDesktopTop)

    fun queryWindows(searchText: String = "", includeAllDesktops: Boolean = true, limit: Int = 25) {
        if (!_capabilityState.value.supportsAdvancedWindowControl) {
            _windowActionError.value = getApplication<Application>().getString(R.string.remote_desktop_advanced_window_control_unavailable)
            return
        }

        viewModelScope.launch(Dispatchers.IO) {
            val message =
                    JSONObject().apply {
                        put("type", "desktop_window_query")
                        put(
                                "desktopWindowQuery",
                                JSONObject().apply {
                                    put(
                                            "requestId",
                                            java.util.UUID.randomUUID().toString().replace("-", "")
                                    )
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
            sendWindowAction("move_to_desktop", windowId) { put("desktopNumber", desktopNumber) }

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

    /**
     * Decodes a desktop_cursor_shape message (raw BGRA8888 pixels, base64 in JSON) into an
     * ImageBitmap and caches it by shape serial. If the shape is the one the current cursor state is
     * waiting on, it becomes the active overlay bitmap immediately.
     */
    private fun handleCursorShape(shapeJson: String) {
        try {
            val json = JSONObject(shapeJson)
            val serial = json.optLong("shapeSerial", -1L)
            val width = json.optInt("width", 0)
            val height = json.optInt("height", 0)
            if (width <= 0 || height <= 0) return
            val hotspotX = json.optInt("hotspotX", 0)
            val hotspotY = json.optInt("hotspotY", 0)
            val b64 = json.optString("shapeBytes", "")
            if (b64.isEmpty()) return
            val bgra = Base64.decode(b64, Base64.DEFAULT)
            if (bgra.size < width * height * 4) return

            // BGRA8888 -> ARGB int pixels (Android Bitmap.Config.ARGB_8888 expects packed ARGB ints).
            val pixels = IntArray(width * height)
            var src = 0
            for (dst in pixels.indices) {
                val b = bgra[src].toInt() and 0xFF
                val g = bgra[src + 1].toInt() and 0xFF
                val r = bgra[src + 2].toInt() and 0xFF
                val a = bgra[src + 3].toInt() and 0xFF
                pixels[dst] = (a shl 24) or (r shl 16) or (g shl 8) or b
                src += 4
            }
            val bitmap = Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888)
            val entry = CursorShapeEntry(bitmap.asImageBitmap(), hotspotX, hotspotY)
            cursorShapeCache[serial] = entry
            if (serial == activeCursorShapeSerial) {
                _cursorShapeBitmap.value = entry.bitmap
                _cursorHotspotX.value = entry.hotspotX
                _cursorHotspotY.value = entry.hotspotY
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to decode cursor shape", e)
        }
    }

    /**
     * Applies a desktop_cursor_state update: live position + visibility, and selects the active shape
     * by serial. Visibility is carried by [hostCursorVisible]; coordinates stay in raw host
     * (virtual-desktop) space, which is legitimately negative on monitors left of/above the primary.
     */
    private fun handleCursorState(stateJson: String) {
        try {
            val json = JSONObject(stateJson)
            val visible = json.optBoolean("visible", true)
            _hostCursorVisible.value = visible
            if (visible) {
                _hostCursorX.value = json.optInt("x", 0).toFloat()
                _hostCursorY.value = json.optInt("y", 0).toFloat()
            }
            val serial = json.optLong("shapeSerial", -1L)
            activeCursorShapeSerial = serial
            // Swap in the cached shape for this serial if we already have it; otherwise the shape
            // collector will swap it in when the matching desktop_cursor_shape arrives.
            cursorShapeCache[serial]?.let { entry ->
                _cursorShapeBitmap.value = entry.bitmap
                _cursorHotspotX.value = entry.hotspotX
                _cursorHotspotY.value = entry.hotspotY
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to parse cursor state", e)
        }
    }

    /**
     * RD-E: parse the 32-byte binary "RDXC" cursor-position packet (little-endian) with zero
     * JSONObject/String allocation on the 60–90 Hz hot path. Layout: magic[4] version[1] flags[1]
     * reserved[2] X[int32] Y[int32] shapeSerial[int64] streamSerial[int64]. Mirrors handleCursorState.
     */
    private fun handleCursorBinary(packet: ByteArray) {
        if (packet.size < 32) return
        try {
            val buf = java.nio.ByteBuffer.wrap(packet).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            val visible = (buf.get(5).toInt() and 0x1) != 0
            val x = buf.getInt(8)
            val y = buf.getInt(12)
            val shapeSerial = buf.getLong(16)
            _hostCursorVisible.value = visible
            if (visible) {
                _hostCursorX.value = x.toFloat()
                _hostCursorY.value = y.toFloat()
            }
            activeCursorShapeSerial = shapeSerial
            // Swap in the cached shape for this serial if present; the shape collector swaps it in later
            // when the matching desktop_cursor_shape (still JSON) arrives.
            cursorShapeCache[shapeSerial]?.let { entry ->
                _cursorShapeBitmap.value = entry.bitmap
                _cursorHotspotX.value = entry.hotspotX
                _cursorHotspotY.value = entry.hotspotY
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to parse binary cursor packet", e)
        }
    }

    private fun persistDesktopDefaults() {
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopDefaults(
                    quality = _configState.value.quality,
                    targetFps = _configState.value.targetFps,
                    scale = _configState.value.scale,
                    preset = _configState.value.preset.id
            )
        }
    }

    private fun buildConfigJson(): JSONObject {
        return JSONObject().apply {
            put("quality", _configState.value.quality)
            put("scale", _configState.value.scale)
            put("targetFps", _configState.value.targetFps)
            put("codec", _configState.value.codec)
            // The client renders the true native cursor itself from the streamed cursor SHAPE
            // (desktop_cursor_shape, real BGRA pixels) positioned by the live desktop_cursor_state.
            // Host-side compositing is left off so the cursor never freezes on mouse-only frames
            // (the host re-encodes only when the desktop changes). The host gates on the
            // supportsCursorShape capability below and falls back to compositing for legacy clients.
            put("drawCursor", false)

            // Advertise display selection and name an explicit capture target only once we have a
            // resolved option from the catalog. Without a resolved option we stay on the legacy
            // path (no capabilities) so the host falls back to its default (primary monitor).
            // Frame envelope / live target-switch are intentionally NOT advertised: display
            // changes are applied by restarting the stream, keeping the raw frame format intact.
            val option = selectedDisplayOption()
            if (option != null) {
                put("desktopProtocolVersion", 1)
                put(
                        "clientCapabilities",
                        JSONObject().apply {
                            put("supportsDisplaySelection", true)
                            // The client now parses the host's per-frame codec/serial envelope, so the
                            // host tags every frame and the client routes by the frame's own codec —
                            // fixing the mid-codec-switch misroute. Target switch stays off (separate
                            // capability), so the host never resets the stream serial mid-flight. (RemEx-w5v)
                            put("supportsFrameEnvelope", true)
                            put("supportsTargetSwitch", false)
                            put("supportsCursorState", true)
                            put("supportsCursorShape", true)
                            // RD-E: receive cursor position as a binary packet (no JSON/GC at 60–90 Hz).
                            put("supportsBinaryCursor", true)
                        }
                )
                put("captureMode", option.captureMode)
                if (option.displayId != null) {
                    put("displayId", option.displayId)
                }
            }
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

    /**
     * Asks the host to emit an on-demand keyframe (IDR). Called by the H.264 decoder when it drops
     * input or recovers from a transient codec error, so the stream resyncs from the next IDR instead
     * of staying corrupt for up to a full GOP. Additive + backward-compatible: legacy hosts that don't
     * recognize the message type simply ignore it (no protocolVersion bump). (RemEx-bqc)
     */
    fun requestKeyframe() {
        viewModelScope.launch(Dispatchers.IO) {
            if (!RemexCoreClient.isLibraryLoaded || !_isStreaming.value) {
                return@launch
            }
            val message =
                    JSONObject().apply { put("type", "desktop_keyframe_request") }
            RemexCoreClient.SendMessage(message.toString()).getOrNull()
        }
    }

    private fun sendWindowAction(
            action: String,
            windowId: String,
            configure: JSONObject.() -> Unit = {}
    ) {
        if (!_capabilityState.value.supportsAdvancedWindowControl) {
            _windowActionError.value = getApplication<Application>().getString(R.string.remote_desktop_advanced_window_control_unavailable)
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
        activeH264Decoder?.release()
        activeH264Decoder = null
    }
}
