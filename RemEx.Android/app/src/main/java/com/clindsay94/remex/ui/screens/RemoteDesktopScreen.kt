package com.clindsay94.remex.ui.screens

import android.content.pm.ActivityInfo
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import androidx.activity.compose.LocalActivity
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.spring
import android.content.res.Configuration
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.foundation.Canvas
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.*
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.ui.viewinterop.AndroidView
import android.util.Log
import com.clindsay94.remex.R
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sqrt
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private const val TAG = "RemoteDesktopScreen"

// Gesture timing thresholds (ms)
private const val TAP_MAX_DURATION_MS = 250L
private const val LONG_PRESS_THRESHOLD_MS = 500L
private const val DOUBLE_TAP_WINDOW_MS = 300L

// Movement thresholds (dp, converted to px at runtime)
private const val FINGER_SLOP_DP = 8f
private const val STYLUS_SLOP_DP = 4f
private const val DOUBLE_TAP_RADIUS_DP = 24f

// Inertia parameters
private const val INERTIA_MIN_VELOCITY = 2f // dp/frame minimum to start inertia
private const val INERTIA_STOP_VELOCITY = 0.5f // dp/frame to stop
private const val INERTIA_DECAY = 0.93f // velocity multiplier per frame
private const val INERTIA_FRAME_MS = 16L // ~60fps

// Throttle interval for move events
private const val MOVE_THROTTLE_MS = 33L // ~30Hz

/** Stores context about the last completed tap for double-tap detection. */
private data class TapContext(
        val time: Long, // uptimeMillis
        val position: Offset,
        val isStylus: Boolean
)

/** The rectangle (within the view box) that the video content actually occupies: size + top-left
 *  offset. H.264 fills the whole box (stretched TextureView); MJPEG is letterboxed via Fit. */
private data class ContentRect(val w: Float, val h: Float, val x: Float, val y: Float)

data class RemoteDesktopUiState(
        val isStreaming: Boolean = false,
        val capabilityState: RemoteDesktopCapabilityState = RemoteDesktopCapabilityState(),
        val desktopError: String? = null,
        val directTouch: Boolean = false,
        val pointerSpeed: Float = 1.0f,
        val vScrollSensitivity: Float = 1.0f,
        val hScrollSensitivity: Float = 1.0f,
        val cursorScale: Float = 1.0f,
        val hostCursorX: Float = -1f,
        val hostCursorY: Float = -1f,
        val hostCursorVisible: Boolean = false,
        val cursorBitmap: ImageBitmap? = null,
        val cursorHotspotX: Int = 0,
        val cursorHotspotY: Int = 0,
        val isFullscreen: Boolean = false,
        val displayTargets: List<DisplayTargetOption> = emptyList(),
        val selectedDisplayToken: String = "",
        // True from the moment the user taps Start until they stop or the connection fails. Drives an
        // immediate rotation to landscape on tap, before the stream is actually up.
        val streamRequested: Boolean = false
)

// Workaround: calling AnimatedVisibility inside a Box that lives inside a Column
// causes Kotlin overload resolution to bind to ColumnScope.AnimatedVisibility, which
// @LayoutScopeMarker then rejects. A plain composable body resolves to the top-level overload.
@Composable
private fun PlainAnimatedVisibility(
        visible: Boolean,
        modifier: Modifier = Modifier,
        enter: androidx.compose.animation.EnterTransition = fadeIn(),
        exit: androidx.compose.animation.ExitTransition = fadeOut(),
        content: @Composable AnimatedVisibilityScope.() -> Unit
) {
        AnimatedVisibility(
                visible = visible,
                modifier = modifier,
                enter = enter,
                exit = exit,
                content = content
        )
}

/** Small uppercase-ish section heading used to group controls in the settings sheet. */
@Composable
private fun SettingsSectionHeader(text: String) {
        Text(
                text = text,
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.primary
        )
}

/** A compact labeled slider for the settings sheet. The label sits directly above the slider so a
 *  pair of these can sit side-by-side in landscape without wasting vertical space. */
@Composable
private fun SettingSlider(
        label: String,
        value: Float,
        onValueChange: (Float) -> Unit,
        valueRange: ClosedFloatingPointRange<Float>,
        modifier: Modifier = Modifier,
        steps: Int = 0
) {
        Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                        label,
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1
                )
                Slider(
                        value = value,
                        onValueChange = onValueChange,
                        valueRange = valueRange,
                        steps = steps
                )
        }
}

/** Lays two settings controls out side-by-side in landscape (to use the wide aspect ratio) and
 *  stacked in portrait. Each slot is handed the Modifier it should apply (weight vs fillMaxWidth). */
@Composable
private fun SettingsPair(
        isLandscape: Boolean,
        first: @Composable (Modifier) -> Unit,
        second: @Composable (Modifier) -> Unit
) {
        if (isLandscape) {
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(24.dp)
                ) {
                        first(Modifier.weight(1f))
                        second(Modifier.weight(1f))
                }
        } else {
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        first(Modifier.fillMaxWidth())
                        second(Modifier.fillMaxWidth())
                }
        }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteDesktopScreen(viewModel: RemoteDesktopViewModel = viewModel()) {
        val currentFrame by viewModel.currentFrame.collectAsStateWithLifecycle()
        val currentBitmap = currentFrame?.bitmap
        val isStreaming by viewModel.isStreaming.collectAsStateWithLifecycle()
        val capabilityState by viewModel.capabilityState.collectAsStateWithLifecycle()
        val desktopError by viewModel.desktopError.collectAsStateWithLifecycle()
        val config by viewModel.configState.collectAsStateWithLifecycle()
        val directTouch by viewModel.directTouch.collectAsStateWithLifecycle()
        val pointerSpeed by viewModel.pointerSpeed.collectAsStateWithLifecycle()
        val vScrollSensitivity by viewModel.verticalScrollSensitivity.collectAsStateWithLifecycle()
        val hScrollSensitivity by viewModel.horizontalScrollSensitivity.collectAsStateWithLifecycle()
        val cursorScale by viewModel.cursorScale.collectAsStateWithLifecycle()
        val hostCursorX by viewModel.hostCursorX.collectAsStateWithLifecycle()
        val hostCursorY by viewModel.hostCursorY.collectAsStateWithLifecycle()
        val hostCursorVisible by viewModel.hostCursorVisible.collectAsStateWithLifecycle()
        val cursorBitmap by viewModel.cursorShapeBitmap.collectAsStateWithLifecycle()
        val cursorHotspotX by viewModel.cursorHotspotX.collectAsStateWithLifecycle()
        val cursorHotspotY by viewModel.cursorHotspotY.collectAsStateWithLifecycle()
        val fps by viewModel.fps.collectAsStateWithLifecycle()
        val windowResults by viewModel.windowResults.collectAsStateWithLifecycle()
        val windowActionError by viewModel.windowActionError.collectAsStateWithLifecycle()
        val activeCodec by viewModel.activeCodecState.collectAsStateWithLifecycle()
        val streamPixelWidth by viewModel.streamPixelWidth.collectAsStateWithLifecycle()
        val streamPixelHeight by viewModel.streamPixelHeight.collectAsStateWithLifecycle()
        val displayTargets by viewModel.displayTargets.collectAsStateWithLifecycle()
        val selectedDisplayToken by viewModel.selectedDisplayToken.collectAsStateWithLifecycle()

        var isFullscreen by rememberSaveable { mutableStateOf(false) }
        var showFpsOverlay by rememberSaveable { mutableStateOf(false) }
        var streamRequested by rememberSaveable { mutableStateOf(false) }

        // Drop the landscape request if the stream never came up (connection/host error).
        LaunchedEffect(desktopError) {
                if (desktopError != null) streamRequested = false
        }

        val uiState =
                RemoteDesktopUiState(
                        isStreaming = isStreaming,
                        capabilityState = capabilityState,
                        desktopError = desktopError,
                        directTouch = directTouch,
                        pointerSpeed = pointerSpeed,
                        vScrollSensitivity = vScrollSensitivity,
                        hScrollSensitivity = hScrollSensitivity,
                        cursorScale = cursorScale,
                        hostCursorX = hostCursorX,
                        hostCursorY = hostCursorY,
                        hostCursorVisible = hostCursorVisible,
                        cursorBitmap = cursorBitmap,
                        cursorHotspotX = cursorHotspotX,
                        cursorHotspotY = cursorHotspotY,
                        isFullscreen = isFullscreen,
                        displayTargets = displayTargets,
                        selectedDisplayToken = selectedDisplayToken,
                        streamRequested = streamRequested
                )

        RemoteDesktopScreenContent(
                uiState = uiState,
                currentBitmap = currentBitmap,
                config = config,
                onSetFullscreen = { isFullscreen = it },
                onStartStreaming = {
                        streamRequested = true
                        viewModel.startStreaming()
                },
                onStopStreaming = {
                        streamRequested = false
                        viewModel.stopStreaming()
                },
                onSendText = { viewModel.sendText(it) },
                onSendKeyPress = { viewModel.sendKeyPress(it) },
                onSendMouseDown = { b, x, y -> viewModel.sendMouseDown(b, x, y) },
                onSendMouseClick = { b -> viewModel.sendMouseClick(b) },
                onSendMouseUp = { b -> viewModel.sendMouseUp(b) },
                onSendMouseMove = { x, y -> viewModel.sendMouseMove(x, y) },
                onSendMouseAbsolute = { x, y -> viewModel.sendMouseAbsolute(x, y) },
                onSendMouseAbsoluteClick = { b, x, y -> viewModel.sendMouseAbsoluteClick(b, x, y) },
                onSendMouseScroll = { x, y -> viewModel.sendMouseScroll(x, y) },
                onSendPointerBatch = { viewModel.sendPointerBatch(it) },
                onUpdateQuality = { viewModel.updateQuality(it) },
                onUpdateTargetFps = { viewModel.updateTargetFps(it) },
                onUpdateDirectTouch = { viewModel.updateDirectTouch(it) },
                onUpdatePointerSpeed = { viewModel.updatePointerSpeed(it) },
                onUpdateScrollSensitivity = { v, h -> viewModel.updateScrollSensitivity(v, h) },
                onUpdateCursorScale = { viewModel.updateCursorScale(it) },
                onSelectDisplayTarget = { viewModel.selectDisplayTarget(it) },
                windowResults = windowResults,
                windowActionError = windowActionError,
                onQueryWindows = { viewModel.queryWindows(it) },
                onActivateWindow = { viewModel.activateWindow(it) },
                onRaiseWindow = { viewModel.raiseWindow(it) },
                onMinimizeWindow = { viewModel.minimizeWindow(it) },
                onCloseWindow = { viewModel.closeWindow(it) },
                onResizeWindow = { id, width, height -> viewModel.resizeWindow(id, width, height) },
                onMoveWindowToDesktop = { id, desktop ->
                        viewModel.moveWindowToDesktop(id, desktop)
                },
                getHostScreenSize = { viewModel.getHostScreenSize() },
                getHostDesktopOffset = { viewModel.getHostDesktopOffset() },
                currentFrameTimestamp = currentFrame?.timestamp,
                fps = fps,
                showFpsOverlay = showFpsOverlay,
                onToggleFpsOverlay = { showFpsOverlay = !showFpsOverlay },
                activeCodec = activeCodec,
                streamPixelWidth = streamPixelWidth,
                streamPixelHeight = streamPixelHeight,
                onActiveH264DecoderChange = { decoder -> viewModel.activeH264Decoder = decoder },
                onH264DecoderReleased = { decoder -> viewModel.onH264DecoderReleased(decoder) },
                onH264DecoderInitFailed = { viewModel.onH264DecoderInitFailed() }
        )
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalComposeUiApi::class)
@Composable
fun RemoteDesktopScreenContent(
        uiState: RemoteDesktopUiState,
        currentBitmap: android.graphics.Bitmap?,
        config: RemoteDesktopConfigState,
        onSetFullscreen: (Boolean) -> Unit,
        onStartStreaming: () -> Unit,
        onStopStreaming: () -> Unit,
        onSendText: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit,
        onSendMouseDown: (Int, Int?, Int?) -> Unit,
        onSendMouseClick: (Int) -> Unit,
        onSendMouseUp: (Int) -> Unit,
        onSendMouseMove: (Float, Float) -> Unit,
        onSendMouseAbsolute: (Int, Int) -> Unit,
        onSendMouseAbsoluteClick: (Int, Int, Int) -> Unit,
        onSendMouseScroll: (Int, Int) -> Unit,
        onSendPointerBatch: (String) -> Unit = {},
        onUpdateQuality: (Int) -> Unit,
        onUpdateTargetFps: (Int) -> Unit,
        onUpdateDirectTouch: (Boolean) -> Unit,
        onUpdatePointerSpeed: (Float) -> Unit,
        onUpdateScrollSensitivity: (Float, Float) -> Unit,
        onUpdateCursorScale: (Float) -> Unit = {},
        onSelectDisplayTarget: (String) -> Unit = {},
        windowResults: List<DesktopWindowModel>,
        windowActionError: String?,
        onQueryWindows: (String) -> Unit,
        onActivateWindow: (String) -> Unit,
        onRaiseWindow: (String) -> Unit,
        onMinimizeWindow: (String) -> Unit,
        onCloseWindow: (String) -> Unit,
        onResizeWindow: (String, Int, Int) -> Unit,
        onMoveWindowToDesktop: (String, Int) -> Unit,
        getHostScreenSize: () -> Pair<Int, Int>,
        getHostDesktopOffset: () -> Pair<Int, Int> = { Pair(0, 0) },
        currentFrameTimestamp: Long?,
        fps: Float = 0f,
        showFpsOverlay: Boolean = false,
        onToggleFpsOverlay: () -> Unit = {},
        activeCodec: String = "Mjpeg",
        streamPixelWidth: Int = 1920,
        streamPixelHeight: Int = 1080,
        onActiveH264DecoderChange: (H264StreamDecoder?) -> Unit = {},
        onH264DecoderReleased: (H264StreamDecoder) -> Unit = {},
        onH264DecoderInitFailed: () -> Unit = {}
) {
        val activity = LocalActivity.current
        val scope = rememberCoroutineScope()
        val density = LocalDensity.current
        val view = LocalView.current

        var showSettings by remember { mutableStateOf(false) }
        // Skip the half-expanded state: in landscape the partial sheet is too short to reveal the
        // input sliders, and the content is scrollable anyway. Always open fully expanded.
        val sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))

        var zoomFactor by remember { mutableFloatStateOf(1f) }
        var panOffsetX by remember { mutableFloatStateOf(0f) }
        var panOffsetY by remember { mutableFloatStateOf(0f) }
        // While the user is manually panning/pinching, suppress cursor pan-follow so the two do
        // not fight. Set to now+cooldown on every manual pan write; pan-follow skips until then.
        var suppressPanFollowUntilMs by remember { mutableLongStateOf(0L) }
        var cursorX by remember { mutableFloatStateOf(0f) }
        var cursorY by remember { mutableFloatStateOf(0f) }
        var imageSize by remember { mutableStateOf(IntSize.Zero) }

        var isStylusActive by remember { mutableStateOf(false) }
        var inputResetTrigger by remember { mutableIntStateOf(0) }
        var cursorVisible by remember { mutableStateOf(false) }
        var mouseControlsExpanded by rememberSaveable { mutableStateOf(false) }
        var controlsVisible by remember { mutableStateOf(false) }
        var controlsHideJob by remember { mutableStateOf<Job?>(null) }
        var windowSearch by rememberSaveable { mutableStateOf("") }
        var selectedWindowId by rememberSaveable { mutableStateOf<String?>(null) }
        var resizeWidthText by rememberSaveable { mutableStateOf("1280") }
        var resizeHeightText by rememberSaveable { mutableStateOf("720") }
        var targetDesktopText by rememberSaveable { mutableStateOf("1") }
        val selectedWindow = windowResults.firstOrNull { it.id == selectedWindowId }

        // Show controls and start auto-hide timer
        fun showControlsWithTimer() {
                controlsVisible = true
                controlsHideJob?.cancel()
                controlsHideJob =
                        scope.launch {
                                delay(4000L) // 4 seconds
                                controlsVisible = false
                        }
        }

        // Tap anywhere on desktop to toggle controls
        var lastTapTime by remember { mutableLongStateOf(0L) }

        // Keyboard support
        val focusRequester = remember { FocusRequester() }
        var textValue by remember { mutableStateOf(TextFieldValue("")) }
        var isRemoteKeyboardOpen by remember { mutableStateOf(false) }

        // Inertia job reference (cancelled on new touch)
        var inertiaJob by remember { mutableStateOf<Job?>(null) }

        // Precompute slop thresholds in pixels
        val fingerSlopPx = with(density) { FINGER_SLOP_DP.dp.toPx() }
        val stylusSlopPx = with(density) { STYLUS_SLOP_DP.dp.toPx() }
        val doubleTapRadiusPx = with(density) { DOUBLE_TAP_RADIUS_DP.dp.toPx() }
        val inertiaMinVelPx = with(density) { INERTIA_MIN_VELOCITY.dp.toPx() }
        val inertiaStopVelPx = with(density) { INERTIA_STOP_VELOCITY.dp.toPx() }

        // These are read inside pointerInput coroutines which don't see recomposition.
        // rememberUpdatedState ensures the coroutine always reads the latest value.
        val pointerSpeedState = rememberUpdatedState(uiState.pointerSpeed)
        val hScrollSensState = rememberUpdatedState(uiState.hScrollSensitivity)
        val vScrollSensState = rememberUpdatedState(uiState.vScrollSensitivity)

        DisposableEffect(activity, uiState.isFullscreen, uiState.isStreaming, uiState.streamRequested) {
                if (activity == null) {
                        onDispose {}
                } else {
                        val window = activity.window
                        val controller = WindowInsetsControllerCompat(window, window.decorView)
                        WindowCompat.setDecorFitsSystemWindows(window, !uiState.isFullscreen)
                        if (uiState.isFullscreen) {
                                controller.hide(WindowInsetsCompat.Type.systemBars())
                                controller.systemBarsBehavior =
                                        WindowInsetsControllerCompat
                                                .BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                        } else {
                                controller.show(WindowInsetsCompat.Type.systemBars())
                        }
                        // Honor the device's orientation like every other screen. Forced landscape
                        // was a crutch for the pre-pan-follow era (a zoomed portrait view stranded
                        // the cursor off-screen); pan-follow now keeps the cursor on screen, so RD
                        // no longer locks orientation.
                        activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                        onDispose {
                                WindowCompat.setDecorFitsSystemWindows(window, true)
                                controller.show(WindowInsetsCompat.Type.systemBars())
                                activity.requestedOrientation =
                                        ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                        }
                }
        }

        // The rectangle the video content occupies within the view box. BOTH codecs letterbox to
        // preserve the source aspect ratio — H.264 via Modifier.aspectRatio on the TextureView,
        // MJPEG via ContentScale.Fit — so this is the same letterboxed region for both. Input
        // mapping, cursor overlay, and the video must all agree on this rect or coordinates drift.
        fun contentRect(): ContentRect {
                if (imageSize.width == 0 || imageSize.height == 0) {
                        return ContentRect(0f, 0f, 0f, 0f)
                }
                val bmpWidth =
                        (if (streamPixelWidth > 0) streamPixelWidth
                        else currentBitmap?.width ?: 1920).toFloat()
                val bmpHeight =
                        (if (streamPixelHeight > 0) streamPixelHeight
                        else currentBitmap?.height ?: 1080).toFloat()
                val bmpAspect = bmpWidth / bmpHeight
                val boxAspect = imageSize.width.toFloat() / imageSize.height.toFloat()
                return if (bmpAspect > boxAspect) {
                        val ew = imageSize.width.toFloat()
                        val eh = ew / bmpAspect
                        ContentRect(ew, eh, 0f, (imageSize.height - eh) / 2f)
                } else {
                        val eh = imageSize.height.toFloat()
                        val ew = eh * bmpAspect
                        ContentRect(ew, eh, (imageSize.width - ew) / 2f, 0f)
                }
        }

        // Returns null (not Offset.Zero) on the three not-ready / degenerate error
        // conditions below, because (0,0) is also a LEGITIMATE host pixel (primary
        // monitor top-left). Callers must skip their action when this returns null.
        fun mapLocalToHost(localOffset: Offset): Offset? {
                if (imageSize.width == 0 || imageSize.height == 0) return null
                // Guard against Offset.Unspecified (NaN) and any other non-finite values
                // that would produce NaN in the output and crash JSON serialization.
                if (!localOffset.x.isFinite() || !localOffset.y.isFinite()) return null
                val (hostW, hostH) = getHostScreenSize()
                val (hostLeft, hostTop) = getHostDesktopOffset()

                val centerX = imageSize.width / 2f
                val centerY = imageSize.height / 2f
                val adjustedX = (localOffset.x - centerX - panOffsetX) / zoomFactor + centerX
                val adjustedY = (localOffset.y - centerY - panOffsetY) / zoomFactor + centerY

                val rect = contentRect()
                if (rect.w <= 0f || rect.h <= 0f) return null
                val relativeX = ((adjustedX - rect.x) / rect.w).coerceIn(0f, 1f)
                val relativeY = ((adjustedY - rect.y) / rect.h).coerceIn(0f, 1f)

                return Offset(relativeX * hostW + hostLeft, relativeY * hostH + hostTop)
        }

        // Inverse of mapLocalToHost: maps a host cursor position to a local (screen) position so we
        // can draw the cursor overlay where the actual Windows cursor is.
        fun mapHostToLocal(hostX: Float, hostY: Float, coerce: Boolean = true): Offset? {
                if (imageSize.width == 0 || imageSize.height == 0) return null
                // Negative host coordinates are VALID: monitors positioned left of/above the
                // primary have negative virtual-desktop coordinates. Cursor visibility is gated by
                // the caller (uiState.hostCursorVisible), never by the sign of the coordinate.
                val (hostW, hostH) = getHostScreenSize()
                val (hostLeft, hostTop) = getHostDesktopOffset()
                if (hostW <= 0 || hostH <= 0) return null
                val rect = contentRect()
                if (rect.w <= 0f || rect.h <= 0f) return null
                val relX = ((hostX - hostLeft) / hostW.toFloat()).let { if (coerce) it.coerceIn(0f, 1f) else it }
                val relY = ((hostY - hostTop) / hostH.toFloat()).let { if (coerce) it.coerceIn(0f, 1f) else it }
                val adjX = relX * rect.w + rect.x
                val adjY = relY * rect.h + rect.y
                val centerX = imageSize.width / 2f
                val centerY = imageSize.height / 2f
                return Offset(
                        (adjX - centerX) * zoomFactor + panOffsetX + centerX,
                        (adjY - centerY) * zoomFactor + panOffsetY + centerY
                )
        }

        // Smooth the remote cursor: the host streams its position at a limited rate, so animate the
        // overlay toward each new position (critically-damped spring) instead of stepping to it.
        // Snap on (re)appearance / display switch so the cursor doesn't slide across the whole screen.
        val animatedCursorX = remember { Animatable(0f) }
        val animatedCursorY = remember { Animatable(0f) }
        var cursorWasVisible by remember { mutableStateOf(false) }
        LaunchedEffect(uiState.hostCursorX, uiState.hostCursorY, uiState.hostCursorVisible) {
            if (!uiState.hostCursorVisible) {
                cursorWasVisible = false
                return@LaunchedEffect
            }
            if (!cursorWasVisible) {
                cursorWasVisible = true
                animatedCursorX.snapTo(uiState.hostCursorX)
                animatedCursorY.snapTo(uiState.hostCursorY)
                return@LaunchedEffect
            }
            val spec = spring<Float>(dampingRatio = 1f, stiffness = Spring.StiffnessMedium)
            launch { animatedCursorX.animateTo(uiState.hostCursorX, spec) }
            launch { animatedCursorY.animateTo(uiState.hostCursorY, spec) }
        }

        // Pan-follow: when zoomed, keep the streamed host cursor on screen by panning the view
        // toward it (edge-triggered via a deadzone), animated so it glides. Re-runs whenever the
        // host cursor moves; each run cancels the previous animation and re-targets.
        LaunchedEffect(uiState.hostCursorX, uiState.hostCursorY, uiState.hostCursorVisible, zoomFactor) {
            if (zoomFactor <= 1f) return@LaunchedEffect
            // Don't chase a cursor that isn't on the streamed display — its coords are last-known/stale.
            if (!uiState.hostCursorVisible) return@LaunchedEffect
            if (System.currentTimeMillis() < suppressPanFollowUntilMs) return@LaunchedEffect
            if (imageSize.width == 0 || imageSize.height == 0) return@LaunchedEffect
            val local = mapHostToLocal(uiState.hostCursorX, uiState.hostCursorY, coerce = false)
                ?: return@LaunchedEffect
            val (targetX, targetY) = PanFollowCalculator.compute(
                cursorLocalX = local.x,
                cursorLocalY = local.y,
                panX = panOffsetX,
                panY = panOffsetY,
                zoom = zoomFactor,
                imageWidth = imageSize.width.toFloat(),
                imageHeight = imageSize.height.toFloat(),
            )
            // This epsilon is load-bearing: when the host cursor sits past the max-pan clamp,
            // compute() returns the same clamped target every tick; this skip prevents the
            // animation from restarting forever. Do not remove.
            if (abs(targetX - panOffsetX) < 0.5f && abs(targetY - panOffsetY) < 0.5f) {
                return@LaunchedEffect
            }
            val startX = panOffsetX
            val startY = panOffsetY
            animate(0f, 1f, animationSpec = spring(stiffness = Spring.StiffnessMediumLow)) { t, _ ->
                panOffsetX = startX + (targetX - startX) * t
                panOffsetY = startY + (targetY - startY) * t
            }
        }

        Scaffold(
                topBar = {
                        if (!uiState.isFullscreen) {
                                TopAppBar(
                                        title = {
                                                // M3: TopAppBar already applies titleLarge weight;
                                                // no override needed
                                                Text(
                                                        stringResource(
                                                                R.string.screen_remote_desktop_title
                                                        )
                                                )
                                        },
                                        actions = {
                                                IconButton(
                                                        onClick = {
                                                                try {
                                                                        focusRequester
                                                                                .requestFocus()
                                                                } catch (_: Exception) {}
                                                        }
                                                ) {
                                                        Icon(
                                                                Icons.Default.Keyboard,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string
                                                                                        .cd_show_keyboard
                                                                        )
                                                        )
                                                }
                                                IconButton(
                                                        onClick = {
                                                                inputResetTrigger++
                                                                zoomFactor = 1f
                                                                panOffsetX = 0f
                                                                panOffsetY = 0f
                                                        }
                                                ) {
                                                        Icon(
                                                                Icons.Default.Refresh,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string
                                                                                        .cd_reset_input
                                                                        )
                                                        )
                                                }
                                                IconButton(onClick = { showSettings = true }) {
                                                        Icon(
                                                                Icons.Default.Tune,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string.cd_settings
                                                                        )
                                                        )
                                                }
                                                IconButton(
                                                        onClick = {
                                                                onSetFullscreen(
                                                                        !uiState.isFullscreen
                                                                )
                                                        }
                                                ) {
                                                        Icon(
                                                                Icons.Default.Fullscreen,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string
                                                                                        .cd_toggle_fullscreen
                                                                        )
                                                        )
                                                }
                                                if (uiState.isStreaming) {
                                                        IconButton(
                                                                onClick = { onStopStreaming() }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Stop,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_stop
                                                                                ),
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .error
                                                                )
                                                        }
                                                } else {
                                                        IconButton(
                                                                onClick = { onStartStreaming() },
                                                                enabled =
                                                                        uiState.capabilityState
                                                                                .supportsRemoteDesktop
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.PlayArrow,
                                                                        contentDescription = null,
                                                                        tint =
                                                                                if (uiState.capabilityState
                                                                                                .supportsRemoteDesktop
                                                                                )
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .primary
                                                                                else
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .onSurfaceVariant
                                                                )
                                                        }
                                                }
                                        }
                                )
                        }
                }
        ) { padding ->
                Column(
                        modifier =
                                Modifier.fillMaxSize()
                                        .padding(
                                                if (uiState.isFullscreen) PaddingValues(0.dp)
                                                else padding
                                        )
                                        .background(Color.Black)
                ) {

                        // Hidden TextField for keyboard input (invisible but focusable)
                        BasicTextField(
                                value = textValue,
                                onValueChange = { newValue ->
                                        textValue =
                                                applyRemoteKeyboardEdit(
                                                        currentValue = textValue,
                                                        newValue = newValue,
                                                        onSendText = onSendText,
                                                        onSendKeyPress = onSendKeyPress
                                                )
                                },
                                keyboardOptions = KeyboardOptions(
                                        keyboardType = KeyboardType.Text,
                                        autoCorrectEnabled = false,
                                        // Default (not Send): on a multiline field Gboard renders a real ↵ Enter key,
                                        // whose '\n' insertion is converted to keycode 13 by applyRemoteKeyboardEdit.
                                        imeAction = ImeAction.Default
                                ),
                                keyboardActions = KeyboardActions(
                                        // Fallbacks: any IME that still renders an action key routes here.
                                        onDone = { onSendKeyPress(13) },
                                        onGo = { onSendKeyPress(13) },
                                        onNext = { onSendKeyPress(13) },
                                        onSearch = { onSendKeyPress(13) },
                                        onSend = { onSendKeyPress(13) }
                                ),
                                modifier =
                                        Modifier.size(1.dp)
                                                .graphicsLayer { alpha = 0f }
                                                .focusRequester(focusRequester)
                                                .onFocusChanged {
                                                        isRemoteKeyboardOpen = it.isFocused
                                                }
                        )

                        Box(
                                modifier =
                                        Modifier.weight(1f)
                                                .fillMaxWidth()
                                                .onGloballyPositioned { imageSize = it.size }
                                                // ═══ RAW STYLUS CONTACT CAPTURE ═══
                                                // intercepts before Compose gesture system;
                                                // returns true (consumed) for stylus/eraser only
                                                .pointerInteropFilter { event ->
                                                        if (!uiState.isStreaming)
                                                                return@pointerInteropFilter false
                                                        val toolType = event.getToolType(0)
                                                        if (toolType !=
                                                                        MotionEvent
                                                                                .TOOL_TYPE_STYLUS &&
                                                                        toolType !=
                                                                                MotionEvent
                                                                                        .TOOL_TYPE_ERASER
                                                        ) {
                                                                return@pointerInteropFilter false
                                                        }
                                                        // Hover events must NOT be consumed here.
                                                        // Returning true for ACTION_HOVER_*
                                                        // corrupts
                                                        // Compose's pointer-tracking state and
                                                        // crashes
                                                        // the app. Let them fall through to the
                                                        // pointerInput hover block below.
                                                        val actionMasked = event.actionMasked
                                                        if (actionMasked ==
                                                                        MotionEvent
                                                                                .ACTION_HOVER_ENTER ||
                                                                        actionMasked ==
                                                                                MotionEvent
                                                                                        .ACTION_HOVER_MOVE ||
                                                                        actionMasked ==
                                                                                MotionEvent
                                                                                        .ACTION_HOVER_EXIT
                                                        ) {
                                                                return@pointerInteropFilter false
                                                        }
                                                        val samples =
                                                                RemoteDesktopMotionMapper
                                                                        .mapContactEvent(event) {
                                                                                vx,
                                                                                vy ->
                                                                                val host =
                                                                                        mapLocalToHost(
                                                                                                Offset(
                                                                                                        vx,
                                                                                                        vy
                                                                                                )
                                                                                        )
                                                                                if (host == null)
                                                                                        null
                                                                                else
                                                                                        Pair(
                                                                                                host.x,
                                                                                                host.y
                                                                                        )
                                                                        }
                                                        if (samples.isNotEmpty()) {
                                                                val json =
                                                                        RemoteDesktopPointerSerializer
                                                                                .toBatchJson(
                                                                                        samples
                                                                                )
                                                                onSendPointerBatch(json)
                                                        }
                                                        true
                                                }
                                                // ═══ STYLUS HOVER HANDLING ═══
                                                .pointerInput(uiState.isStreaming) {
                                                        if (!uiState.isStreaming)
                                                                return@pointerInput
                                                        awaitPointerEventScope {
                                                                var lastHoverTime = 0L
                                                                while (true) {
                                                                        val event =
                                                                                awaitPointerEvent(
                                                                                        PointerEventPass
                                                                                                .Main
                                                                                )
                                                                        if (event.type ==
                                                                                        PointerEventType
                                                                                                .Move
                                                                        ) {
                                                                                val stylusChange =
                                                                                        event.changes
                                                                                                .find {
                                                                                                        it.type ==
                                                                                                                PointerType
                                                                                                                        .Stylus &&
                                                                                                                !it.pressed
                                                                                                }
                                                                                if (stylusChange !=
                                                                                                null
                                                                                ) {
                                                                                        val now =
                                                                                                stylusChange
                                                                                                        .uptimeMillis
                                                                                        if (now -
                                                                                                        lastHoverTime >=
                                                                                                        MOVE_THROTTLE_MS
                                                                                        ) {
                                                                                                lastHoverTime =
                                                                                                        now
                                                                                                val hostPos =
                                                                                                        mapLocalToHost(
                                                                                                                stylusChange
                                                                                                                        .position
                                                                                                        )
                                                                                                                ?: continue
                                                                                                // Send as typed hover sample (tool kind Pen, phase HoverMove)
                                                                                                val sample =
                                                                                                        PointerSampleData(
                                                                                                                timestamp =
                                                                                                                        now,
                                                                                                                pointerId =
                                                                                                                        stylusChange
                                                                                                                                .id
                                                                                                                                .value
                                                                                                                                .toInt(),
                                                                                                                phase =
                                                                                                                        PointerPhase
                                                                                                                                .HoverMove,
                                                                                                                toolKind =
                                                                                                                        PointerToolKind
                                                                                                                                .Pen,
                                                                                                                deviceKind =
                                                                                                                        PointerDeviceKind
                                                                                                                                .Stylus,
                                                                                                                logicalX =
                                                                                                                        hostPos.x,
                                                                                                                logicalY =
                                                                                                                        hostPos.y,
                                                                                                        )
                                                                                                onSendPointerBatch(
                                                                                                        RemoteDesktopPointerSerializer
                                                                                                                .toBatchJson(
                                                                                                                        listOf(
                                                                                                                                sample
                                                                                                                        )
                                                                                                                )
                                                                                                )
                                                                                                cursorX =
                                                                                                        stylusChange
                                                                                                                .position
                                                                                                                .x
                                                                                                cursorY =
                                                                                                        stylusChange
                                                                                                                .position
                                                                                                                .y
                                                                                                cursorVisible =
                                                                                                        true
                                                                                                isStylusActive =
                                                                                                        true
                                                                                        }
                                                                                }
                                                                        }
                                                                }
                                                        }
                                                }
                                                // ═══ UNIFIED GESTURE STATE MACHINE ═══
                                                // Key on directTouch so the handler restarts when
                                                // mode changes
                                                .pointerInput(
                                                        uiState.isStreaming,
                                                        inputResetTrigger,
                                                        uiState.directTouch
                                                ) {
                                                        if (!uiState.isStreaming)
                                                                return@pointerInput

                                                        // Detect taps to show/hide controls
                                                        awaitEachGesture {
                                                                val down = awaitFirstDown()
                                                                val now = System.currentTimeMillis()
                                                                if (now - lastTapTime < 300) {
                                                                        // Double-tap detected -
                                                                        // toggle controls
                                                                        showControlsWithTimer()
                                                                        lastTapTime = 0L
                                                                } else {
                                                                        lastTapTime = now
                                                                }
                                                        }
                                                }
                                                .pointerInput(
                                                        uiState.isStreaming,
                                                        inputResetTrigger,
                                                        uiState.directTouch
                                                ) {
                                                        if (!uiState.isStreaming)
                                                                return@pointerInput

                                                        awaitPointerEventScope {
                                                                // ── Persistent gesture state ──
                                                                var primaryPointerId: PointerId? =
                                                                        null
                                                                var pressTime =
                                                                        0L // uptimeMillis of
                                                                // primary pointer press
                                                                var pressPos =
                                                                        Offset.Zero // position at
                                                                // press
                                                                var pressIsStylus = false
                                                                var isDragging =
                                                                        false // true once mouseDown
                                                                // has been sent
                                                                var dragButton =
                                                                        0 // which mouse button is
                                                                // held
                                                                var hasMovedBeyondSlop = false
                                                                var multiTouchActive =
                                                                        false // true when 2+
                                                                // pointers are down
                                                                var lastTap: TapContext? =
                                                                        null // for double-tap
                                                                // detection
                                                                var isDoubleTapHoldArmed =
                                                                        false // second press in
                                                                // double-tap window
                                                                var longPressArmed =
                                                                        false // 500ms still-hold
                                                                // completed
                                                                // (trackpad)
                                                                var longPressJob: Job? = null
                                                                var lastMoveTime =
                                                                        0L // for move throttling
                                                                var scrollAccumX = 0f
                                                                var scrollAccumY = 0f

                                                                // Two-finger gesture state
                                                                var twoFingerIntent: String? =
                                                                        null // "scroll" | "pinch" |
                                                                // null
                                                                var prevTwoFingerDist = 0f
                                                                var prevTwoFingerCenter =
                                                                        Offset.Zero

                                                                // Velocity tracking for inertia
                                                                // (trackpad mode)
                                                                val recentDeltas =
                                                                        ArrayDeque<Offset>(4)
                                                                var lastDeltaTime = 0L

                                                                fun slopPx() =
                                                                        if (pressIsStylus)
                                                                                stylusSlopPx
                                                                        else fingerSlopPx

                                                                fun resetSinglePointerState() {
                                                                        primaryPointerId = null
                                                                        pressTime = 0L
                                                                        pressPos = Offset.Zero
                                                                        hasMovedBeyondSlop = false
                                                                        isDoubleTapHoldArmed = false
                                                                        longPressArmed = false
                                                                        longPressJob?.cancel()
                                                                        longPressJob = null
                                                                        lastMoveTime = 0L
                                                                        recentDeltas.clear()
                                                                }

                                                                fun resetTwoFingerState() {
                                                                        twoFingerIntent = null
                                                                        prevTwoFingerDist = 0f
                                                                        prevTwoFingerCenter =
                                                                                Offset.Zero
                                                                        scrollAccumX = 0f
                                                                        scrollAccumY = 0f
                                                                }

                                                                fun cancelDrag() {
                                                                        if (isDragging) {
                                                                                onSendMouseUp(
                                                                                        dragButton
                                                                                )
                                                                                isDragging = false
                                                                                dragButton = 0
                                                                        }
                                                                }

                                                                try {
                                                                        while (true) {
                                                                                val event =
                                                                                        awaitPointerEvent()
                                                                                val activePointers =
                                                                                        event.changes
                                                                                                .filter {
                                                                                                        it.pressed
                                                                                                }
                                                                                val pointerCount =
                                                                                        activePointers
                                                                                                .size

                                                                                // Cancel inertia on
                                                                                // any new touch
                                                                                if (event.type ==
                                                                                                PointerEventType
                                                                                                        .Press
                                                                                ) {
                                                                                        inertiaJob
                                                                                                ?.cancel()
                                                                                        inertiaJob =
                                                                                                null
                                                                                }

                                                                                // ── MULTI-TOUCH
                                                                                // HANDLING (2
                                                                                // fingers) ──
                                                                                if (pointerCount >=
                                                                                                2
                                                                                ) {
                                                                                        if (!multiTouchActive
                                                                                        ) {
                                                                                                // Transition 1→2+: cancel any
                                                                                                // single-pointer gesture
                                                                                                cancelDrag()
                                                                                                resetSinglePointerState()
                                                                                                multiTouchActive =
                                                                                                        true
                                                                                                resetTwoFingerState()
                                                                                        }

                                                                                        if (pointerCount ==
                                                                                                        2
                                                                                        ) {
                                                                                                val p1 =
                                                                                                        activePointers[
                                                                                                                        0]
                                                                                                                .position
                                                                                                val p2 =
                                                                                                        activePointers[
                                                                                                                        1]
                                                                                                                .position
                                                                                                val dist =
                                                                                                        sqrt(
                                                                                                                (p1.x -
                                                                                                                        p2.x) *
                                                                                                                        (p1.x -
                                                                                                                                p2.x) +
                                                                                                                        (p1.y -
                                                                                                                                p2.y) *
                                                                                                                                (p1.y -
                                                                                                                                        p2.y)
                                                                                                        )
                                                                                                val center =
                                                                                                        Offset(
                                                                                                                (p1.x +
                                                                                                                        p2.x) /
                                                                                                                        2f,
                                                                                                                (p1.y +
                                                                                                                        p2.y) /
                                                                                                                        2f
                                                                                                        )

                                                                                                if (prevTwoFingerDist >
                                                                                                                0f
                                                                                                ) {
                                                                                                        val distDelta =
                                                                                                                abs(
                                                                                                                        dist -
                                                                                                                                prevTwoFingerDist
                                                                                                                )
                                                                                                        val moveDelta =
                                                                                                                center -
                                                                                                                        prevTwoFingerCenter

                                                                                                        // Lock intent after first
                                                                                                        // significant gesture
                                                                                                        if (twoFingerIntent ==
                                                                                                                        null
                                                                                                        ) {
                                                                                                                if (distDelta >
                                                                                                                                5f
                                                                                                                )
                                                                                                                        twoFingerIntent =
                                                                                                                                "pinch"
                                                                                                                else if (moveDelta
                                                                                                                                .getDistance() >
                                                                                                                                3f
                                                                                                                )
                                                                                                                        twoFingerIntent =
                                                                                                                                "scroll"
                                                                                                        }

                                                                                                        when (twoFingerIntent
                                                                                                        ) {
                                                                                                                "pinch" -> {
                                                                                                                        if (distDelta >
                                                                                                                                        2f
                                                                                                                        ) {
                                                                                                                                val zoomDelta =
                                                                                                                                        dist /
                                                                                                                                                prevTwoFingerDist
                                                                                                                                val oldZoom =
                                                                                                                                        zoomFactor
                                                                                                                                zoomFactor =
                                                                                                                                        (zoomFactor *
                                                                                                                                                        zoomDelta)
                                                                                                                                                .coerceIn(
                                                                                                                                                        1f,
                                                                                                                                                        4f
                                                                                                                                                )
                                                                                                                                val actualDelta =
                                                                                                                                        zoomFactor /
                                                                                                                                                oldZoom
                                                                                                                                panOffsetX =
                                                                                                                                        (center.x -
                                                                                                                                                imageSize
                                                                                                                                                        .width /
                                                                                                                                                        2f) *
                                                                                                                                                (1f -
                                                                                                                                                        actualDelta) +
                                                                                                                                                panOffsetX *
                                                                                                                                                        actualDelta
                                                                                                                                panOffsetY =
                                                                                                                                        (center.y -
                                                                                                                                                imageSize
                                                                                                                                                        .height /
                                                                                                                                                        2f) *
                                                                                                                                                (1f -
                                                                                                                                                        actualDelta) +
                                                                                                                                                panOffsetY *
                                                                                                                                                        actualDelta
                                                                                                                                val maxPanX =
                                                                                                                                        imageSize.width *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                val maxPanY =
                                                                                                                                        imageSize.height *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                panOffsetX =
                                                                                                                                        panOffsetX
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanX,
                                                                                                                                                        maxPanX
                                                                                                                                                )
                                                                                                                                panOffsetY =
                                                                                                                                        panOffsetY
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanY,
                                                                                                                                                        maxPanY
                                                                                                                                                )
                                                                                                                                suppressPanFollowUntilMs = System.currentTimeMillis() + 350
                                                                                                                        }
                                                                                                                }
                                                                                                                "scroll" -> {
                                                                                                                        if (zoomFactor >
                                                                                                                                        1.05f
                                                                                                                        ) {
                                                                                                                                // Panning zoomed view
                                                                                                                                panOffsetX +=
                                                                                                                                        moveDelta
                                                                                                                                                .x
                                                                                                                                panOffsetY +=
                                                                                                                                        moveDelta
                                                                                                                                                .y
                                                                                                                                val maxPanX =
                                                                                                                                        imageSize.width *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                val maxPanY =
                                                                                                                                        imageSize.height *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                panOffsetX =
                                                                                                                                        panOffsetX
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanX,
                                                                                                                                                        maxPanX
                                                                                                                                                )
                                                                                                                                panOffsetY =
                                                                                                                                        panOffsetY
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanY,
                                                                                                                                                        maxPanY
                                                                                                                                                )
                                                                                                                                suppressPanFollowUntilMs = System.currentTimeMillis() + 350
                                                                                                                        } else {
                                                                                                                                // Mouse wheel scroll
                                                                                                                                // with accumulator
                                                                                                                                scrollAccumX +=
                                                                                                                                        moveDelta
                                                                                                                                                .x *
                                                                                                                                                1.5f *
                                                                                                                                                hScrollSensState
                                                                                                                                                        .value
                                                                                                                                scrollAccumY +=
                                                                                                                                        moveDelta
                                                                                                                                                .y *
                                                                                                                                                1.5f *
                                                                                                                                                vScrollSensState
                                                                                                                                                        .value
                                                                                                                                val sx =
                                                                                                                                        scrollAccumX
                                                                                                                                                .toInt()
                                                                                                                                val sy =
                                                                                                                                        scrollAccumY
                                                                                                                                                .toInt()
                                                                                                                                if (sx !=
                                                                                                                                                0 ||
                                                                                                                                                sy !=
                                                                                                                                                        0
                                                                                                                                ) {
                                                                                                                                        onSendMouseScroll(
                                                                                                                                                -sx,
                                                                                                                                                -sy
                                                                                                                                        )
                                                                                                                                        scrollAccumX -=
                                                                                                                                                sx
                                                                                                                                        scrollAccumY -=
                                                                                                                                                sy
                                                                                                                                }
                                                                                                                        }
                                                                                                                }
                                                                                                        }
                                                                                                }

                                                                                                prevTwoFingerDist =
                                                                                                        dist
                                                                                                prevTwoFingerCenter =
                                                                                                        center
                                                                                                event.changes
                                                                                                        .forEach {
                                                                                                                it.consume()
                                                                                                        }
                                                                                        }
                                                                                        // 3+
                                                                                        // fingers:
                                                                                        // ignore
                                                                                        // entirely
                                                                                        continue
                                                                                }

                                                                                // ── SINGLE-POINTER
                                                                                // HANDLING ──
                                                                                if (pointerCount ==
                                                                                                0 &&
                                                                                                multiTouchActive
                                                                                ) {
                                                                                        // All
                                                                                        // fingers
                                                                                        // lifted
                                                                                        // after
                                                                                        // multi-touch —
                                                                                        // reset
                                                                                        multiTouchActive =
                                                                                                false
                                                                                        resetTwoFingerState()
                                                                                        resetSinglePointerState()
                                                                                        continue
                                                                                }

                                                                                if (multiTouchActive
                                                                                )
                                                                                        continue // Still in multi-touch, ignore
                                                                                // single pointer
                                                                                // events

                                                                                // Find the primary
                                                                                // pointer (track by
                                                                                // ID)
                                                                                val change =
                                                                                        if (primaryPointerId !=
                                                                                                        null
                                                                                        ) {
                                                                                                event.changes
                                                                                                        .find {
                                                                                                                it.id ==
                                                                                                                        primaryPointerId
                                                                                                        }
                                                                                                        ?: event.changes
                                                                                                                .firstOrNull()
                                                                                        } else {
                                                                                                event.changes
                                                                                                        .firstOrNull()
                                                                                        }
                                                                                if (change == null)
                                                                                        continue

                                                                                val isStylus =
                                                                                        change.type ==
                                                                                                PointerType
                                                                                                        .Stylus
                                                                                val now =
                                                                                        change.uptimeMillis
                                                                                val useAbsolute =
                                                                                        isStylus ||
                                                                                                uiState.directTouch

                                                                                when (event.type) {
                                                                                        PointerEventType
                                                                                                .Press -> {
                                                                                                primaryPointerId =
                                                                                                        change.id
                                                                                                pressTime =
                                                                                                        now
                                                                                                pressPos =
                                                                                                        change.position
                                                                                                pressIsStylus =
                                                                                                        isStylus
                                                                                                hasMovedBeyondSlop =
                                                                                                        false
                                                                                                isStylusActive =
                                                                                                        isStylus

                                                                                                cursorVisible =
                                                                                                        useAbsolute
                                                                                                cursorX =
                                                                                                        change.position
                                                                                                                .x
                                                                                                cursorY =
                                                                                                        change.position
                                                                                                                .y
                                                                                                recentDeltas
                                                                                                        .clear()
                                                                                                lastDeltaTime =
                                                                                                        now

                                                                                                // Check for double-tap-hold
                                                                                                val lt =
                                                                                                        lastTap
                                                                                                if (lt !=
                                                                                                                null &&
                                                                                                                (now -
                                                                                                                        lt.time) <
                                                                                                                        DOUBLE_TAP_WINDOW_MS &&
                                                                                                                (change.position -
                                                                                                                                lt.position)
                                                                                                                        .getDistance() <
                                                                                                                        doubleTapRadiusPx &&
                                                                                                                lt.isStylus ==
                                                                                                                        isStylus
                                                                                                ) {
                                                                                                        isDoubleTapHoldArmed =
                                                                                                                true
                                                                                                } else {
                                                                                                        isDoubleTapHoldArmed =
                                                                                                                false
                                                                                                }

                                                                                                // Arm long-press-then-drag timer for
                                                                                                // trackpad mode (fires after 500ms
                                                                                                // still-hold). Not used in absolute
                                                                                                // mode or when double-tap is armed.
                                                                                                longPressJob
                                                                                                        ?.cancel()
                                                                                                longPressArmed =
                                                                                                        false
                                                                                                if (!useAbsolute &&
                                                                                                                !isDoubleTapHoldArmed
                                                                                                ) {
                                                                                                        longPressJob =
                                                                                                                scope
                                                                                                                        .launch {
                                                                                                                                delay(
                                                                                                                                        LONG_PRESS_THRESHOLD_MS
                                                                                                                                )
                                                                                                                                if (!isDragging &&
                                                                                                                                                !hasMovedBeyondSlop
                                                                                                                                ) {
                                                                                                                                        longPressArmed =
                                                                                                                                                true
                                                                                                                                        view.performHapticFeedback(
                                                                                                                                                HapticFeedbackConstants
                                                                                                                                                        .LONG_PRESS
                                                                                                                                        )
                                                                                                                                }
                                                                                                                        }
                                                                                                }

                                                                                                // DO NOT send mouseDown here — deferred
                                                                                                // until movement or release
                                                                                        }
                                                                                        PointerEventType
                                                                                                .Move -> {
                                                                                                if (primaryPointerId ==
                                                                                                                null
                                                                                                )
                                                                                                        continue

                                                                                                val distance =
                                                                                                        (change.position -
                                                                                                                        pressPos)
                                                                                                                .getDistance()
                                                                                                val slop =
                                                                                                        slopPx()

                                                                                                if (!hasMovedBeyondSlop &&
                                                                                                                distance >
                                                                                                                        slop
                                                                                                ) {
                                                                                                        hasMovedBeyondSlop =
                                                                                                                true
                                                                                                        // Moving cancels the still-hold
                                                                                                        // long-press timer if it hasn't
                                                                                                        // fired yet
                                                                                                        if (!longPressArmed
                                                                                                        ) {
                                                                                                                longPressJob
                                                                                                                        ?.cancel()
                                                                                                                longPressJob =
                                                                                                                        null
                                                                                                        }
                                                                                                }

                                                                                                // Update cursor position for visual
                                                                                                // feedback
                                                                                                if (useAbsolute
                                                                                                ) {
                                                                                                        cursorX =
                                                                                                                change.position
                                                                                                                        .x
                                                                                                        cursorY =
                                                                                                                change.position
                                                                                                                        .y
                                                                                                }

                                                                                                // Throttle movement events to ~30Hz
                                                                                                if (now -
                                                                                                                lastMoveTime <
                                                                                                                MOVE_THROTTLE_MS
                                                                                                )
                                                                                                        continue
                                                                                                lastMoveTime =
                                                                                                        now

                                                                                                if (hasMovedBeyondSlop
                                                                                                ) {
                                                                                                        if (isDoubleTapHoldArmed &&
                                                                                                                        !isDragging
                                                                                                        ) {
                                                                                                                // Double-tap-then-drag: second
                                                                                                                // press has started moving —
                                                                                                                // immediately enter left drag
                                                                                                                if (useAbsolute
                                                                                                                ) {
                                                                                                                        val hostPress =
                                                                                                                                mapLocalToHost(
                                                                                                                                        pressPos
                                                                                                                                )
                                                                                                                        hostPress?.let {
                                                                                                                                onSendMouseDown(
                                                                                                                                        0,
                                                                                                                                        it.x.toInt(),
                                                                                                                                        it.y.toInt()
                                                                                                                                )
                                                                                                                        }
                                                                                                                } else {
                                                                                                                        onSendMouseDown(
                                                                                                                                0,
                                                                                                                                null,
                                                                                                                                null
                                                                                                                        )
                                                                                                                }
                                                                                                                isDragging =
                                                                                                                        true
                                                                                                                dragButton =
                                                                                                                        0
                                                                                                                isDoubleTapHoldArmed =
                                                                                                                        false
                                                                                                                lastTap =
                                                                                                                        null
                                                                                                        } else if (longPressArmed &&
                                                                                                                        !isDragging &&
                                                                                                                        !useAbsolute
                                                                                                        ) {
                                                                                                                // Long-press-then-drag
                                                                                                                // (trackpad): enter left drag
                                                                                                                // now that user has started
                                                                                                                // moving after 500ms hold
                                                                                                                onSendMouseDown(
                                                                                                                        0,
                                                                                                                        null,
                                                                                                                        null
                                                                                                                )
                                                                                                                isDragging =
                                                                                                                        true
                                                                                                                dragButton =
                                                                                                                        0
                                                                                                                longPressArmed =
                                                                                                                        false
                                                                                                        } else if (!isDragging &&
                                                                                                                        !isDoubleTapHoldArmed &&
                                                                                                                        useAbsolute
                                                                                                        ) {
                                                                                                                // Absolute/stylus mode: normal
                                                                                                                // drag requires mouseDown
                                                                                                                val hostPress =
                                                                                                                        mapLocalToHost(
                                                                                                                                pressPos
                                                                                                                        )
                                                                                                                hostPress?.let {
                                                                                                                        onSendMouseDown(
                                                                                                                                0,
                                                                                                                                it.x.toInt(),
                                                                                                                                it.y.toInt()
                                                                                                                        )
                                                                                                                }
                                                                                                                isDragging =
                                                                                                                        true
                                                                                                                dragButton =
                                                                                                                        0
                                                                                                        }
                                                                                                        // Trackpad (relative, no
                                                                                                        // long-press, no double-tap): just
                                                                                                        // move cursor, no button held

                                                                                                        if (useAbsolute
                                                                                                        ) {
                                                                                                                // Absolute positioning
                                                                                                                val hostPos =
                                                                                                                        mapLocalToHost(
                                                                                                                                change.position
                                                                                                                        )
                                                                                                                hostPos?.let {
                                                                                                                        onSendMouseAbsolute(
                                                                                                                                it.x.toInt(),
                                                                                                                                it.y.toInt()
                                                                                                                        )
                                                                                                                }
                                                                                                        } else {
                                                                                                                // Relative (trackpad)
                                                                                                                // positioning with pointer
                                                                                                                // speed
                                                                                                                val diff =
                                                                                                                        change.position -
                                                                                                                                change.previousPosition
                                                                                                                val scaledX =
                                                                                                                        diff.x *
                                                                                                                                pointerSpeedState
                                                                                                                                        .value
                                                                                                                val scaledY =
                                                                                                                        diff.y *
                                                                                                                                pointerSpeedState
                                                                                                                                        .value

                                                                                                                if (isDragging
                                                                                                                ) {
                                                                                                                        // Dragging with button held
                                                                                                                        onSendMouseMove(
                                                                                                                                scaledX,
                                                                                                                                scaledY
                                                                                                                        )
                                                                                                                } else {
                                                                                                                        // Normal trackpad cursor
                                                                                                                        // movement — NO mouseDown
                                                                                                                        onSendMouseMove(
                                                                                                                                scaledX,
                                                                                                                                scaledY
                                                                                                                        )
                                                                                                                }

                                                                                                                // Track velocity for inertia
                                                                                                                if (!isDragging
                                                                                                                ) {
                                                                                                                        recentDeltas
                                                                                                                                .addLast(
                                                                                                                                        Offset(
                                                                                                                                                scaledX,
                                                                                                                                                scaledY
                                                                                                                                        )
                                                                                                                                )
                                                                                                                        if (recentDeltas
                                                                                                                                        .size >
                                                                                                                                        3
                                                                                                                        )
                                                                                                                                recentDeltas
                                                                                                                                        .removeFirst()
                                                                                                                }
                                                                                                        }
                                                                                                }
                                                                                        }
                                                                                        PointerEventType
                                                                                                .Release -> {
                                                                                                if (primaryPointerId ==
                                                                                                                null
                                                                                                )
                                                                                                        continue
                                                                                                val duration =
                                                                                                        now -
                                                                                                                pressTime

                                                                                                if (isDragging
                                                                                                ) {
                                                                                                        // End drag
                                                                                                        onSendMouseUp(
                                                                                                                dragButton
                                                                                                        )
                                                                                                        isDragging =
                                                                                                                false
                                                                                                        dragButton =
                                                                                                                0
                                                                                                } else if (!hasMovedBeyondSlop
                                                                                                ) {
                                                                                                        // Tap gesture (no significant
                                                                                                        // movement)
                                                                                                        if (duration <
                                                                                                                        TAP_MAX_DURATION_MS
                                                                                                        ) {
                                                                                                                // Quick tap = left-click
                                                                                                                if (useAbsolute
                                                                                                                ) {
                                                                                                                        val hostPos =
                                                                                                                                mapLocalToHost(
                                                                                                                                        pressPos
                                                                                                                                )
                                                                                                                        hostPos?.let {
                                                                                                                                onSendMouseAbsoluteClick(
                                                                                                                                        0,
                                                                                                                                        it.x.toInt(),
                                                                                                                                        it.y.toInt()
                                                                                                                                )
                                                                                                                        }
                                                                                                                } else {
                                                                                                                        onSendMouseClick(
                                                                                                                                0
                                                                                                                        )
                                                                                                                }
                                                                                                                lastTap =
                                                                                                                        TapContext(
                                                                                                                                now,
                                                                                                                                pressPos,
                                                                                                                                isStylus
                                                                                                                        )
                                                                                                        } else if (duration >=
                                                                                                                        LONG_PRESS_THRESHOLD_MS
                                                                                                        ) {
                                                                                                                // Long press = right-click
                                                                                                                if (useAbsolute
                                                                                                                ) {
                                                                                                                        val hostPos =
                                                                                                                                mapLocalToHost(
                                                                                                                                        pressPos
                                                                                                                                )
                                                                                                                        hostPos?.let {
                                                                                                                                onSendMouseAbsoluteClick(
                                                                                                                                        2,
                                                                                                                                        it.x.toInt(),
                                                                                                                                        it.y.toInt()
                                                                                                                                )
                                                                                                                        }
                                                                                                                } else {
                                                                                                                        onSendMouseClick(
                                                                                                                                2
                                                                                                                        )
                                                                                                                }
                                                                                                                lastTap =
                                                                                                                        null // Long press is
                                                                                                                // not a tap for
                                                                                                                // double-tap
                                                                                                                // purposes
                                                                                                        }
                                                                                                } else if (!useAbsolute &&
                                                                                                                !isDragging
                                                                                                ) {
                                                                                                        // Trackpad: finger lifted after
                                                                                                        // moving — apply inertia
                                                                                                        if (recentDeltas
                                                                                                                        .isNotEmpty()
                                                                                                        ) {
                                                                                                                var avgX =
                                                                                                                        0f
                                                                                                                var avgY =
                                                                                                                        0f
                                                                                                                recentDeltas
                                                                                                                        .forEach {
                                                                                                                                avgX +=
                                                                                                                                        it.x
                                                                                                                                avgY +=
                                                                                                                                        it.y
                                                                                                                        }
                                                                                                                avgX /=
                                                                                                                        recentDeltas
                                                                                                                                .size
                                                                                                                avgY /=
                                                                                                                        recentDeltas
                                                                                                                                .size
                                                                                                                val velocity =
                                                                                                                        sqrt(
                                                                                                                                avgX *
                                                                                                                                        avgX +
                                                                                                                                        avgY *
                                                                                                                                                avgY
                                                                                                                        )

                                                                                                                if (velocity >
                                                                                                                                inertiaMinVelPx
                                                                                                                ) {
                                                                                                                        inertiaJob
                                                                                                                                ?.cancel()
                                                                                                                        inertiaJob =
                                                                                                                                scope
                                                                                                                                        .launch {
                                                                                                                                                var vx =
                                                                                                                                                        avgX
                                                                                                                                                var vy =
                                                                                                                                                        avgY
                                                                                                                                                while (sqrt(
                                                                                                                                                        vx *
                                                                                                                                                                vx +
                                                                                                                                                                vy *
                                                                                                                                                                        vy
                                                                                                                                                ) >
                                                                                                                                                        inertiaStopVelPx) {
                                                                                                                                                        onSendMouseMove(
                                                                                                                                                                vx,
                                                                                                                                                                vy
                                                                                                                                                        )
                                                                                                                                                        vx *=
                                                                                                                                                                INERTIA_DECAY
                                                                                                                                                        vy *=
                                                                                                                                                                INERTIA_DECAY
                                                                                                                                                        delay(
                                                                                                                                                                INERTIA_FRAME_MS
                                                                                                                                                        )
                                                                                                                                                }
                                                                                                                                        }
                                                                                                                }
                                                                                                        }
                                                                                                }

                                                                                                resetSinglePointerState()
                                                                                        }
                                                                                        PointerEventType
                                                                                                .Exit -> {
                                                                                                cancelDrag()
                                                                                                resetSinglePointerState()
                                                                                        }
                                                                                }
                                                                        }
                                                                } finally {
                                                                        // Scope cancelled — release
                                                                        // any held buttons
                                                                        if (isDragging) {
                                                                                onSendMouseUp(
                                                                                        dragButton
                                                                                )
                                                                        }
                                                                        inertiaJob?.cancel()
                                                                        longPressJob?.cancel()
                                                                }
                                                        }
                                                },
                                contentAlignment = Alignment.Center
                        ) {
                                val safeFrame = currentBitmap

                                if (activeCodec == "H264" || (safeFrame != null && !safeFrame.isRecycled)) {
                                        if (activeCodec == "H264") {
                                                key(streamPixelWidth, streamPixelHeight) {
                                                AndroidView(
                                                        factory = { context ->
                                                                var localDecoder: H264StreamDecoder? = null
                                                                android.view.TextureView(context).apply {
                                                                        surfaceTextureListener = object : android.view.TextureView.SurfaceTextureListener {
                                                                                override fun onSurfaceTextureAvailable(surfaceTexture: android.graphics.SurfaceTexture, width: Int, height: Int) {
                                                                                        val surface = android.view.Surface(surfaceTexture)
                                                                                        // Use the encoded stream dimensions from DesktopMeta, not the surface view size
                                                                                        val decoder = H264StreamDecoder(
                                                                                                streamPixelWidth,
                                                                                                streamPixelHeight,
                                                                                                surface,
                                                                                                onInitFailure = onH264DecoderInitFailed
                                                                                        )
                                                                                        localDecoder = decoder
                                                                                        onActiveH264DecoderChange(decoder)
                                                                                        Log.i(TAG, "H.264 stream surface created: encoded=${streamPixelWidth}x${streamPixelHeight}, surface=${width}x${height}")
                                                                                }
                                                                                override fun onSurfaceTextureSizeChanged(surface: android.graphics.SurfaceTexture, width: Int, height: Int) {}
                                                                                override fun onSurfaceTextureDestroyed(surface: android.graphics.SurfaceTexture): Boolean {
                                                                                        // Capture and clear the local reference first. When key(streamPixelWidth,
                                                                                        // streamPixelHeight) rebuilds this AndroidView on a resolution change, the
                                                                                        // NEW view's onSurfaceTextureAvailable may fire BEFORE this (old) view's
                                                                                        // destroy callback. Releasing via the guarded callback ensures we only
                                                                                        // clear the ViewModel's active decoder if it still points at THIS decoder,
                                                                                        // never the freshly-created one — otherwise frames would be starved.
                                                                                        val released = localDecoder
                                                                                        localDecoder = null
                                                                                        if (released != null) {
                                                                                                released.release()
                                                                                                onH264DecoderReleased(released)
                                                                                        }
                                                                                        Log.i(TAG, "H.264 stream surface destroyed.")
                                                                                        return true
                                                                                }
                                                                                override fun onSurfaceTextureUpdated(surface: android.graphics.SurfaceTexture) {}
                                                                        }
                                                                }
                                                        },
                                                        // Letterbox to the source aspect ratio instead of stretching to fill
                                                        // (the parent Box centers us). A TextureView always scales its content
                                                        // to its bounds, so we must size the view itself to the stream aspect —
                                                        // this is the H.264 equivalent of MJPEG's ContentScale.Fit, and it
                                                        // matches contentRect() so input/cursor/pan stay aligned. Fixes the
                                                        // squished image in portrait orientation.
                                                        modifier = Modifier.aspectRatio(
                                                                if (streamPixelWidth > 0 && streamPixelHeight > 0)
                                                                        streamPixelWidth.toFloat() / streamPixelHeight.toFloat()
                                                                else 16f / 9f
                                                        )
                                                                .graphicsLayer {
                                                                        scaleX = zoomFactor
                                                                        scaleY = zoomFactor
                                                                        translationX = panOffsetX
                                                                        translationY = panOffsetY
                                                                }
                                                )
                                                }
                                        } else {
                                                if (safeFrame != null && !safeFrame.isRecycled) {
                                                        // Force Image to redraw when frame changes by using
                                                        // key(timestamp)
                                                        key(currentFrameTimestamp) {
                                                                Image(
                                                                        bitmap = safeFrame.asImageBitmap(),
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_remote_desktop_frame
                                                                                ),
                                                                        modifier =
                                                                                Modifier.fillMaxSize()
                                                                                        .graphicsLayer {
                                                                                                scaleX = zoomFactor
                                                                                                scaleY = zoomFactor
                                                                                                translationX =
                                                                                                        panOffsetX
                                                                                                translationY =
                                                                                                        panOffsetY
                                                                                        },
                                                                        contentScale = ContentScale.Fit
                                                                )
                                                        }
                                                }
                                        }

                                        // Cursor overlay: draw the host's actual cursor as an arrow at
                                        // its mapped on-screen position, so the user can see exactly
                                        // where they're pointing. Uses the same content rect as input
                                        // mapping, so the arrow, the video, and where clicks land all
                                        // agree. hostCursorX/Y are host-desktop coords; -1 = none yet.
                                        val cursorLocal =
                                                if (uiState.isStreaming && uiState.hostCursorVisible) {
                                                        // Use the smoothed (animated) host position so
                                                        // the overlay glides instead of stepping.
                                                        mapHostToLocal(
                                                                animatedCursorX.value,
                                                                animatedCursorY.value
                                                        )
                                                } else {
                                                        null
                                                }

                                        if (cursorLocal != null) {
                                                val cursorBmp = uiState.cursorBitmap
                                                Canvas(modifier = Modifier.fillMaxSize()) {
                                                        if (cursorBmp != null) {
                                                                // Draw the true native cursor bitmap (BGRA from the host) with
                                                                // its hotspot at the mapped position. Scale it the same way the
                                                                // video is scaled — content-rect width vs host width, times the
                                                                // pan/zoom factor — so it matches desktop content, then by the
                                                                // user's cursorScale preference.
                                                                val rect = contentRect()
                                                                val (hostW, _) = getHostScreenSize()
                                                                val pxPerHost =
                                                                        if (hostW > 0 && rect.w > 0f)
                                                                                (rect.w * zoomFactor) / hostW
                                                                        else 1f
                                                                val drawScale = pxPerHost * uiState.cursorScale
                                                                val w =
                                                                        (cursorBmp.width * drawScale)
                                                                                .roundToInt()
                                                                                .coerceAtLeast(1)
                                                                val h =
                                                                        (cursorBmp.height * drawScale)
                                                                                .roundToInt()
                                                                                .coerceAtLeast(1)
                                                                drawImage(
                                                                        image = cursorBmp,
                                                                        dstOffset =
                                                                                IntOffset(
                                                                                        (cursorLocal.x -
                                                                                                        uiState.cursorHotspotX *
                                                                                                                drawScale)
                                                                                                .roundToInt(),
                                                                                        (cursorLocal.y -
                                                                                                        uiState.cursorHotspotY *
                                                                                                                drawScale)
                                                                                                .roundToInt()
                                                                                ),
                                                                        dstSize = IntSize(w, h)
                                                                )
                                                        } else {
                                                                // Fallback generic arrow until the first cursor shape arrives,
                                                                // or for legacy hosts that don't stream cursor shapes.
                                                                // 1 arrow unit ≈ 1.4.dp → ~26dp tall pointer at scale 1.0;
                                                                // cursorScale (0.5–2.5) resizes it.
                                                                val s = 1.4.dp.toPx() * uiState.cursorScale
                                                                val ox = cursorLocal.x
                                                                val oy = cursorLocal.y
                                                                val arrow =
                                                                        Path().apply {
                                                                                moveTo(ox, oy)
                                                                                lineTo(ox, oy + 16f * s)
                                                                                lineTo(ox + 4f * s, oy + 12.5f * s)
                                                                                lineTo(ox + 6.5f * s, oy + 18.5f * s)
                                                                                lineTo(ox + 8.5f * s, oy + 17.5f * s)
                                                                                lineTo(ox + 6f * s, oy + 11.5f * s)
                                                                                lineTo(ox + 10.5f * s, oy + 11.5f * s)
                                                                                close()
                                                                        }
                                                                drawPath(arrow, Color.White)
                                                                drawPath(
                                                                        arrow,
                                                                        Color.Black,
                                                                        style = Stroke(width = 1.5.dp.toPx())
                                                                )
                                                        }
                                                }
                                        }
                                } else {
                                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                                Icon(
                                                        imageVector = Icons.Default.Monitor,
                                                        contentDescription =
                                                                stringResource(
                                                                        R.string.cd_monitor_icon
                                                                ),
                                                        modifier = Modifier.size(64.dp),
                                                        tint =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant
                                                )
                                                Spacer(modifier = Modifier.height(16.dp))
                                                Text(
                                                        text =
                                                                when {
                                                                        uiState.desktopError !=
                                                                                null ->
                                                                                uiState.desktopError
                                                                        !uiState.capabilityState
                                                                                .supportsRemoteDesktop ->
                                                                                uiState.capabilityState
                                                                                        .unavailableReason
                                                                                        ?: stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_unavailable
                                                                                        )
                                                                        uiState.isStreaming ->
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_waiting
                                                                                )
                                                                        else ->
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_stopped
                                                                                )
                                                                },
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant,
                                                        style = MaterialTheme.typography.bodyLarge
                                                )
                                                if (!uiState.isStreaming &&
                                                                uiState.capabilityState
                                                                        .supportsRemoteDesktop
                                                ) {
                                                        Spacer(modifier = Modifier.height(16.dp))
                                                        FilledTonalButton(
                                                                onClick = { onStartStreaming() }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.PlayArrow,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_play_icon
                                                                                ),
                                                                        modifier =
                                                                                Modifier.size(18.dp)
                                                                )
                                                                Spacer(
                                                                        modifier =
                                                                                Modifier.width(8.dp)
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .button_start_streaming
                                                                        )
                                                                )
                                                        }
                                                }
                                        }
                                }

                                // M3: AssistChip with AnimatedVisibility replaces raw Surface+Text
                                // for FPS overlay
                                // PlainAnimatedVisibility resolves ColumnScope.AnimatedVisibility
                                // implicit receiver conflict
                                PlainAnimatedVisibility(
                                        visible = uiState.isStreaming && showFpsOverlay,
                                        modifier =
                                                Modifier.align(Alignment.TopStart).padding(12.dp),
                                        enter =
                                                fadeIn(
                                                        animationSpec =
                                                                androidx.compose.animation.core
                                                                        .tween(durationMillis = 200)
                                                ),
                                        exit =
                                                fadeOut(
                                                        animationSpec =
                                                                androidx.compose.animation.core
                                                                        .tween(durationMillis = 200)
                                                )
                                ) {
                                        AssistChip(
                                                onClick = {},
                                                label = {
                                                        Text(
                                                                text = stringResource(R.string.remote_desktop_fps_overlay, fps.toInt()),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .labelSmall
                                                        )
                                                },
                                                colors =
                                                        AssistChipDefaults.assistChipColors(
                                                                containerColor =
                                                                        MaterialTheme.colorScheme
                                                                                .scrim.copy(
                                                                                alpha = 0.55f
                                                                        ),
                                                                labelColor = Color.White
                                                        ),
                                                border = null
                                        )
                                }

                                if (uiState.isFullscreen) {
                                        Row(
                                                modifier =
                                                        Modifier.align(Alignment.TopEnd)
                                                                .padding(16.dp),
                                                horizontalArrangement = Arrangement.spacedBy(8.dp)
                                        ) {
                                                // Settings button — open the (translucent) settings
                                                // overlay without having to leave fullscreen first.
                                                FilledTonalIconButton(
                                                        onClick = { showSettings = true },
                                                        colors =
                                                                IconButtonDefaults
                                                                        .filledTonalIconButtonColors(
                                                                                containerColor =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .surfaceContainerHighest
                                                                        )
                                                ) {
                                                        Icon(
                                                                Icons.Default.Tune,
                                                                contentDescription = "Settings"
                                                        )
                                                }
                                                FilledTonalIconButton(
                                                        onClick = { onSetFullscreen(false) },
                                                        colors =
                                                                IconButtonDefaults
                                                                        .filledTonalIconButtonColors(
                                                                                containerColor =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .surfaceContainerHighest
                                                                        )
                                                ) {
                                                        Icon(
                                                                Icons.Default.FullscreenExit,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string.cd_exit_fullscreen
                                                                        )
                                                        )
                                                }
                                        }
                                }

                                // ═══ UNIFIED AUTO-HIDING CONTROL BAR ═══
                                if (uiState.isStreaming) {
                                        androidx.compose.animation.AnimatedVisibility(
                                                visible = controlsVisible || !uiState.isFullscreen,
                                                enter =
                                                        slideInVertically(
                                                                androidx.compose.animation.core
                                                                        .spring(
                                                                                stiffness =
                                                                                        androidx.compose
                                                                                                .animation
                                                                                                .core
                                                                                                .Spring
                                                                                                .StiffnessMediumLow
                                                                        )
                                                        ) { it } +
                                                                fadeIn(
                                                                        androidx.compose.animation
                                                                                .core.tween(
                                                                                durationMillis = 200
                                                                        )
                                                                ),
                                                exit =
                                                        slideOutVertically(
                                                                androidx.compose.animation.core
                                                                        .spring(
                                                                                stiffness =
                                                                                        androidx.compose
                                                                                                .animation
                                                                                                .core
                                                                                                .Spring
                                                                                                .StiffnessMediumLow
                                                                        )
                                                        ) { it } +
                                                                fadeOut(
                                                                        androidx.compose.animation
                                                                                .core.tween(
                                                                                durationMillis = 200
                                                                        )
                                                                ),
                                                modifier = Modifier.align(Alignment.BottomCenter)
                                        ) {
                                                Surface(
                                                        tonalElevation = 8.dp,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .surfaceContainerHighest,
                                                        modifier =
                                                                Modifier.fillMaxWidth().let {
                                                                        if (uiState.isFullscreen)
                                                                                it.windowInsetsPadding(
                                                                                        WindowInsets
                                                                                                .navigationBars
                                                                                )
                                                                        else it
                                                                },
                                                        shape =
                                                                if (uiState.isFullscreen)
                                                                        MaterialTheme.shapes.medium
                                                                else RoundedCornerShape(0.dp)
                                                ) {
                                                        Column(
                                                                modifier = Modifier.fillMaxWidth(),
                                                                verticalArrangement =
                                                                        Arrangement.spacedBy(4.dp)
                                                        ) {
                                                                // Top row: Scroll & Zoom controls
                                                                Row(
                                                                        modifier =
                                                                                Modifier.fillMaxWidth()
                                                                                        .padding(
                                                                                                horizontal =
                                                                                                        12.dp,
                                                                                                vertical =
                                                                                                        8.dp
                                                                                        ),
                                                                        horizontalArrangement =
                                                                                Arrangement
                                                                                        .SpaceEvenly,
                                                                        verticalAlignment =
                                                                                Alignment
                                                                                        .CenterVertically
                                                                ) {
                                                                        // Scroll controls
                                                                        RepeatingIconButton(
                                                                                onClick = {
                                                                                        onSendMouseScroll(
                                                                                                0,
                                                                                                120
                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                },
                                                                                icon =
                                                                                        Icons.Default
                                                                                                .KeyboardDoubleArrowUp,
                                                                                description =
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .cd_scroll_up
                                                                                        )
                                                                        )
                                                                        RepeatingIconButton(
                                                                                onClick = {
                                                                                        onSendMouseScroll(
                                                                                                0,
                                                                                                -120
                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                },
                                                                                icon =
                                                                                        Icons.Default
                                                                                                .KeyboardDoubleArrowDown,
                                                                                description =
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .cd_scroll_down
                                                                                        )
                                                                        )

                                                                        HorizontalDivider(
                                                                                modifier =
                                                                                        Modifier.width(
                                                                                                        1.dp
                                                                                                )
                                                                                                .height(
                                                                                                        32.dp
                                                                                                ),
                                                                                color =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .outlineVariant
                                                                        )

                                                                        // Zoom controls
                                                                        if (zoomFactor > 1.05f) {
                                                                                FilledTonalIconButton(
                                                                                        onClick = {
                                                                                                zoomFactor =
                                                                                                        1f
                                                                                                panOffsetX =
                                                                                                        0f
                                                                                                panOffsetY =
                                                                                                        0f
                                                                                                showControlsWithTimer()
                                                                                        }
                                                                                ) {
                                                                                        Text(
                                                                                                "1×",
                                                                                                fontWeight =
                                                                                                        FontWeight
                                                                                                                .Bold,
                                                                                                style =
                                                                                                        MaterialTheme
                                                                                                                .typography
                                                                                                                .labelSmall
                                                                                        )
                                                                                }
                                                                        }
                                                                        IconButton(
                                                                                onClick = {
                                                                                        zoomFactor =
                                                                                                (zoomFactor -
                                                                                                                0.5f)
                                                                                                        .coerceIn(
                                                                                                                1f,
                                                                                                                4f
                                                                                                        )
                                                                                        val mpX =
                                                                                                imageSize.width *
                                                                                                        (zoomFactor -
                                                                                                                1f) /
                                                                                                        2f
                                                                                        val mpY =
                                                                                                imageSize.height *
                                                                                                        (zoomFactor -
                                                                                                                1f) /
                                                                                                        2f
                                                                                        panOffsetX =
                                                                                                panOffsetX
                                                                                                        .coerceIn(
                                                                                                                -mpX,
                                                                                                                mpX
                                                                                                        )
                                                                                        panOffsetY =
                                                                                                panOffsetY
                                                                                                        .coerceIn(
                                                                                                                -mpY,
                                                                                                                mpY
                                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                }
                                                                        ) {
                                                                                Icon(
                                                                                        Icons.Default
                                                                                                .Remove,
                                                                                        contentDescription =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                        .cd_zoom_out
                                                                                                )
                                                                                )
                                                                        }
                                                                        IconButton(
                                                                                onClick = {
                                                                                        zoomFactor =
                                                                                                (zoomFactor +
                                                                                                                0.5f)
                                                                                                        .coerceIn(
                                                                                                                1f,
                                                                                                                4f
                                                                                                        )
                                                                                        val mpX =
                                                                                                imageSize.width *
                                                                                                        (zoomFactor -
                                                                                                                1f) /
                                                                                                        2f
                                                                                        val mpY =
                                                                                                imageSize.height *
                                                                                                        (zoomFactor -
                                                                                                                1f) /
                                                                                                        2f
                                                                                        panOffsetX =
                                                                                                panOffsetX
                                                                                                        .coerceIn(
                                                                                                                -mpX,
                                                                                                                mpX
                                                                                                        )
                                                                                        panOffsetY =
                                                                                                panOffsetY
                                                                                                        .coerceIn(
                                                                                                                -mpY,
                                                                                                                mpY
                                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                }
                                                                        ) {
                                                                                Icon(
                                                                                        Icons.Default
                                                                                                .Add,
                                                                                        contentDescription =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                        .cd_zoom_in
                                                                                                )
                                                                                )
                                                                        }

                                                                        HorizontalDivider(
                                                                                modifier =
                                                                                        Modifier.width(
                                                                                                        1.dp
                                                                                                )
                                                                                                .height(
                                                                                                        32.dp
                                                                                                ),
                                                                                color =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .outlineVariant
                                                                        )

                                                                        // M3: TextButton is correct
                                                                        // for text-only actions,
                                                                        // not IconButton
                                                                        TextButton(
                                                                                onClick = {
                                                                                        onToggleFpsOverlay()
                                                                                        showControlsWithTimer()
                                                                                }
                                                                        ) {
                                                                                Text(
                                                                                        text =
                                                                                                if (showFpsOverlay
                                                                                                )
                                                                                                        "${fps.toInt()}"
                                                                                                else
                                                                                                        stringResource(R.string.remote_desktop_fps_overlay_btn),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .labelSmall,
                                                                                        fontWeight =
                                                                                                FontWeight
                                                                                                        .Bold,
                                                                                        color =
                                                                                                if (showFpsOverlay
                                                                                                )
                                                                                                        MaterialTheme
                                                                                                                .colorScheme
                                                                                                                .primary
                                                                                                else
                                                                                                        MaterialTheme
                                                                                                                .colorScheme
                                                                                                                .onSurface
                                                                                )
                                                                        }
                                                                }

                                                                HorizontalDivider(
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .outlineVariant
                                                                )

                                                                // Bottom row: Mouse click buttons
                                                                Row(
                                                                        modifier =
                                                                                Modifier.fillMaxWidth()
                                                                                        .padding(
                                                                                                horizontal =
                                                                                                        12.dp,
                                                                                                vertical =
                                                                                                        8.dp
                                                                                        ),
                                                                        horizontalArrangement =
                                                                                Arrangement
                                                                                        .SpaceEvenly,
                                                                        verticalAlignment =
                                                                                Alignment
                                                                                        .CenterVertically
                                                                ) {
                                                                        // Left Click
                                                                        FilledTonalButton(
                                                                                onClick = {
                                                                                        onSendMouseAbsoluteClick(
                                                                                                0,
                                                                                                -1,
                                                                                                -1
                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        )
                                                                        ) {
                                                                                Text(
                                                                                        stringResource(R.string.remote_desktop_left_initial),
                                                                                        fontWeight =
                                                                                                FontWeight
                                                                                                        .Bold
                                                                                )
                                                                                Spacer(
                                                                                        Modifier.width(
                                                                                                4.dp
                                                                                        )
                                                                                )
                                                                                Text(
                                                                                        stringResource(R.string.remote_desktop_left_btn),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .labelSmall
                                                                                )
                                                                        }

                                                                        Spacer(Modifier.width(8.dp))

                                                                        // Middle Click
                                                                        FilledTonalButton(
                                                                                onClick = {
                                                                                        onSendMouseAbsoluteClick(
                                                                                                1,
                                                                                                -1,
                                                                                                -1
                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        )
                                                                        ) {
                                                                                Text(
                                                                                        stringResource(R.string.remote_desktop_middle_initial),
                                                                                        fontWeight =
                                                                                                FontWeight
                                                                                                        .Bold
                                                                                )
                                                                                Spacer(
                                                                                        Modifier.width(
                                                                                                4.dp
                                                                                        )
                                                                                )
                                                                                Text(
                                                                                        stringResource(R.string.remote_desktop_middle_btn),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .labelSmall
                                                                                )
                                                                        }

                                                                        Spacer(Modifier.width(8.dp))

                                                                        // Right Click
                                                                        FilledTonalButton(
                                                                                onClick = {
                                                                                        onSendMouseAbsoluteClick(
                                                                                                2,
                                                                                                -1,
                                                                                                -1
                                                                                        )
                                                                                        showControlsWithTimer()
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        )
                                                                        ) {
                                                                                Text(
                                                                                        stringResource(R.string.remote_desktop_right_initial),
                                                                                        fontWeight =
                                                                                                FontWeight
                                                                                                        .Bold
                                                                                )
                                                                                Spacer(
                                                                                        Modifier.width(
                                                                                                4.dp
                                                                                        )
                                                                                )
                                                                                Text(
                                                                                        stringResource(R.string.remote_desktop_right_btn),
                                                                                        style =
                                                                                                MaterialTheme
                                                                                                        .typography
                                                                                                        .labelSmall
                                                                                )
                                                                        }
                                                                }
                                                        }
                                                }
                                        }
                                }
                        }

                        if (showSettings) {
                                ModalBottomSheet(
                                        onDismissRequest = { showSettings = false },
                                        sheetState = sheetState,
                                        // Translucent so the desktop stays partly visible behind the
                                        // settings, and no dim scrim over the stream.
                                        containerColor =
                                                MaterialTheme.colorScheme.surface.copy(alpha = 0.88f),
                                        scrimColor = Color.Transparent
                                ) {
                                        val isLandscape =
                                                LocalConfiguration.current.orientation ==
                                                        Configuration.ORIENTATION_LANDSCAPE
                                        Column(
                                                modifier =
                                                        Modifier.fillMaxWidth()
                                                                // Scrollable so every control is
                                                                // reachable even when the sheet is
                                                                // short (landscape).
                                                                .verticalScroll(
                                                                        rememberScrollState()
                                                                )
                                                                .padding(
                                                                        horizontal = 24.dp,
                                                                        vertical = 16.dp
                                                                )
                                                                .navigationBarsPadding()
                                                                .imePadding(),
                                                verticalArrangement = Arrangement.spacedBy(16.dp)
                                        ) {
                                                // Header: title + close button
                                                Row(
                                                        modifier = Modifier.fillMaxWidth(),
                                                        horizontalArrangement =
                                                                Arrangement.SpaceBetween,
                                                        verticalAlignment =
                                                                Alignment.CenterVertically
                                                ) {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_config_title
                                                                ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleLarge,
                                                                fontWeight = FontWeight.Bold
                                                        )
                                                        IconButton(
                                                                onClick = { showSettings = false }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Close,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_close_settings
                                                                                )
                                                                )
                                                        }
                                                }

                                                // ── Display section ── (only when there is an
                                                // actual choice: 2+ monitors, or a combined option)
                                                if (uiState.displayTargets.size >= 2) {
                                                        SettingsSectionHeader(
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_display_label
                                                                )
                                                        )
                                                        Row(
                                                                modifier =
                                                                        Modifier.fillMaxWidth()
                                                                                .horizontalScroll(
                                                                                        rememberScrollState()
                                                                                ),
                                                                horizontalArrangement =
                                                                        Arrangement.spacedBy(8.dp)
                                                        ) {
                                                                uiState.displayTargets.forEach {
                                                                        target ->
                                                                        FilterChip(
                                                                                selected =
                                                                                        target.token ==
                                                                                                uiState.selectedDisplayToken,
                                                                                onClick = {
                                                                                        onSelectDisplayTarget(
                                                                                                target.token
                                                                                        )
                                                                                },
                                                                                label = {
                                                                                        Text(
                                                                                                target.label
                                                                                        )
                                                                                }
                                                                        )
                                                                }
                                                        }
                                                }

                                                // ── Stream section ──
                                                SettingsSectionHeader(
                                                        stringResource(
                                                                R.string
                                                                        .remote_desktop_section_stream
                                                        )
                                                )
                                                SettingsPair(
                                                        isLandscape = isLandscape,
                                                        first = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_quality_label,
                                                                                        config.quality
                                                                                ),
                                                                        value =
                                                                                config.quality
                                                                                        .toFloat(),
                                                                        onValueChange = {
                                                                                onUpdateQuality(
                                                                                        it.toInt()
                                                                                )
                                                                        },
                                                                        valueRange = 1f..100f,
                                                                        modifier = m
                                                                )
                                                        },
                                                        second = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_fps_label,
                                                                                        config.targetFps
                                                                                ),
                                                                        value =
                                                                                config.targetFps
                                                                                        .toFloat(),
                                                                        onValueChange = {
                                                                                onUpdateTargetFps(
                                                                                        it.toInt()
                                                                                )
                                                                        },
                                                                        valueRange = 1f..120f,
                                                                        modifier = m
                                                                )
                                                        }
                                                )

                                                HorizontalDivider()

                                                // ── Input & Controls section ──
                                                SettingsSectionHeader(
                                                        stringResource(
                                                                R.string
                                                                        .remote_desktop_section_input
                                                        )
                                                )

                                                // Direct Touch toggle (full width — own row so it
                                                // never collides with a slider beside it)
                                                Row(
                                                        modifier = Modifier.fillMaxWidth(),
                                                        horizontalArrangement =
                                                                Arrangement.SpaceBetween,
                                                        verticalAlignment =
                                                                Alignment.CenterVertically
                                                ) {
                                                        Column(modifier = Modifier.weight(1f)) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_direct_touch_label
                                                                        ),
                                                                        fontWeight =
                                                                                FontWeight.SemiBold
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_direct_touch_desc
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        }
                                                        Spacer(Modifier.width(16.dp))
                                                        Switch(
                                                                checked = uiState.directTouch,
                                                                onCheckedChange = {
                                                                        onUpdateDirectTouch(it)
                                                                }
                                                        )
                                                }

                                                SettingsPair(
                                                        isLandscape = isLandscape,
                                                        first = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_pointer_speed_label
                                                                                        ),
                                                                                        uiState.pointerSpeed
                                                                                ),
                                                                        value =
                                                                                uiState.pointerSpeed,
                                                                        onValueChange = {
                                                                                onUpdatePointerSpeed(
                                                                                        it
                                                                                )
                                                                        },
                                                                        valueRange = 0.25f..3.0f,
                                                                        steps = 10,
                                                                        modifier = m
                                                                )
                                                        },
                                                        second = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_cursor_size_label
                                                                                        ),
                                                                                        uiState.cursorScale
                                                                                ),
                                                                        value =
                                                                                uiState.cursorScale,
                                                                        onValueChange = {
                                                                                onUpdateCursorScale(
                                                                                        it
                                                                                )
                                                                        },
                                                                        valueRange = 0.5f..2.5f,
                                                                        steps = 7,
                                                                        modifier = m
                                                                )
                                                        }
                                                )

                                                SettingsPair(
                                                        isLandscape = isLandscape,
                                                        first = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_v_scroll_short
                                                                                        ),
                                                                                        uiState.vScrollSensitivity
                                                                                ),
                                                                        value =
                                                                                uiState.vScrollSensitivity,
                                                                        onValueChange = {
                                                                                onUpdateScrollSensitivity(
                                                                                        it,
                                                                                        uiState.hScrollSensitivity
                                                                                )
                                                                        },
                                                                        valueRange = 0.1f..5.0f,
                                                                        steps = 20,
                                                                        modifier = m
                                                                )
                                                        },
                                                        second = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_h_scroll_short
                                                                                        ),
                                                                                        uiState.hScrollSensitivity
                                                                                ),
                                                                        value =
                                                                                uiState.hScrollSensitivity,
                                                                        onValueChange = {
                                                                                onUpdateScrollSensitivity(
                                                                                        uiState.vScrollSensitivity,
                                                                                        it
                                                                                )
                                                                        },
                                                                        valueRange = 0.1f..5.0f,
                                                                        steps = 20,
                                                                        modifier = m
                                                                )
                                                        }
                                                )

                                                Text(
                                                        text =
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_controls_hint_v2
                                                                ),
                                                        style = MaterialTheme.typography.bodySmall,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant
                                                )

                                                if (uiState.capabilityState
                                                                .supportsAdvancedWindowControl
                                                ) {
                                                        HorizontalDivider()
                                                        Text(
                                                                text = stringResource(R.string.remote_desktop_window_controls_header),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleMedium,
                                                                fontWeight = FontWeight.Bold
                                                        )
                                                        Text(
                                                                text =
                                                                        stringResource(R.string.remote_desktop_backend, uiState.capabilityState.windowBackend ?: "host"),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodySmall,
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant
                                                        )

                                                        OutlinedTextField(
                                                                value = windowSearch,
                                                                onValueChange = {
                                                                        windowSearch = it
                                                                },
                                                                label = { Text(stringResource(R.string.remote_desktop_search_windows_label)) },
                                                                modifier = Modifier.fillMaxWidth(),
                                                                singleLine = true
                                                        )

                                                        FilledTonalButton(
                                                                onClick = {
                                                                        onQueryWindows(windowSearch)
                                                                }
                                                        ) { Text(stringResource(R.string.remote_desktop_refresh_windows)) }

                                                        if (windowActionError != null) {
                                                                Text(
                                                                        text = windowActionError,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .error,
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall
                                                                )
                                                        }

                                                        if (windowResults.isEmpty()) {
                                                                Text(
                                                                        text =
                                                                                stringResource(R.string.remote_desktop_no_results),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        } else {
                                                                Column(
                                                                        verticalArrangement =
                                                                                Arrangement
                                                                                        .spacedBy(
                                                                                                8.dp
                                                                                        )
                                                                ) {
                                                                        windowResults.take(6)
                                                                                .forEach { window ->
                                                                                        ElevatedCard(
                                                                                                onClick = {
                                                                                                        selectedWindowId =
                                                                                                                window.id
                                                                                                }
                                                                                        ) {
                                                                                                Column(
                                                                                                        modifier =
                                                                                                                Modifier.fillMaxWidth()
                                                                                                                        .padding(
                                                                                                                                12.dp
                                                                                                                        ),
                                                                                                        verticalArrangement =
                                                                                                                Arrangement
                                                                                                                        .spacedBy(
                                                                                                                                6.dp
                                                                                                                        )
                                                                                                ) {
                                                                                                        Text(
                                                                                                                text =
                                                                                                                        window.title,
                                                                                                                style =
                                                                                                                        MaterialTheme
                                                                                                                                .typography
                                                                                                                                .bodyLarge,
                                                                                                                fontWeight =
                                                                                                                        FontWeight
                                                                                                                                .SemiBold
                                                                                                        )
                                                                                                        Text(
                                                                                                                text =
                                                                                                                        buildString {
                                                                                                                                append(
                                                                                                                                        window.className
                                                                                                                                                ?: stringResource(R.string.remote_desktop_unknown_class)
                                                                                                                                )
                                                                                                                                window.desktopNumber
                                                                                                                                        ?.let {
                                                                                                                                                append(
                                                                                                                                                        stringResource(
                                                                                                                                                                R.string.remote_desktop_desktop_number,
                                                                                                                                                                it
                                                                                                                                                        )
                                                                                                                                                )
                                                                                                                                        }
                                                                                                                                if (window.isActive
                                                                                                                                ) {
                                                                                                                                        append(
                                                                                                                                                stringResource(
                                                                                                                                                        R.string.remote_desktop_active_status
                                                                                                                                                )
                                                                                                                                        )
                                                                                                                                }
                                                                                                                        },
                                                                                                                style =
                                                                                                                        MaterialTheme
                                                                                                                                .typography
                                                                                                                                .bodySmall,
                                                                                                                color =
                                                                                                                        MaterialTheme
                                                                                                                                .colorScheme
                                                                                                                                .onSurfaceVariant
                                                                                                        )
                                                                                                        Row(
                                                                                                                horizontalArrangement =
                                                                                                                        Arrangement
                                                                                                                                .spacedBy(
                                                                                                                                        8.dp
                                                                                                                                )
                                                                                                        ) {
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onActivateWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_activate)
                                                                                                                        )
                                                                                                                }
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onRaiseWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_raise)
                                                                                                                        )
                                                                                                                }
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onMinimizeWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_minimize)
                                                                                                                        )
                                                                                                                }
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onCloseWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_close)
                                                                                                                        )
                                                                                                                }
                                                                                                        }
                                                                                                }
                                                                                        }
                                                                                }
                                                                }
                                                        }

                                                        if (selectedWindow != null) {
                                                                Text(
                                                                        text =
                                                                                stringResource(R.string.remote_desktop_selected, selectedWindow.title),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodyMedium,
                                                                        fontWeight =
                                                                                FontWeight.SemiBold
                                                                )
                                                                Row(
                                                                        horizontalArrangement =
                                                                                Arrangement
                                                                                        .spacedBy(
                                                                                                8.dp
                                                                                        )
                                                                ) {
                                                                        OutlinedTextField(
                                                                                value =
                                                                                        resizeWidthText,
                                                                                onValueChange = {
                                                                                        resizeWidthText =
                                                                                                it
                                                                                },
                                                                                label = {
                                                                                        Text(
                                                                                                stringResource(R.string.remote_desktop_width)
                                                                                        )
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        ),
                                                                                singleLine = true
                                                                        )
                                                                        OutlinedTextField(
                                                                                value =
                                                                                        resizeHeightText,
                                                                                onValueChange = {
                                                                                        resizeHeightText =
                                                                                                it
                                                                                },
                                                                                label = {
                                                                                        Text(
                                                                                                stringResource(R.string.remote_desktop_height)
                                                                                        )
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        ),
                                                                                singleLine = true
                                                                        )
                                                                }
                                                                FilledTonalButton(
                                                                        onClick = {
                                                                                val width =
                                                                                        resizeWidthText
                                                                                                .toIntOrNull()
                                                                                val height =
                                                                                        resizeHeightText
                                                                                                .toIntOrNull()
                                                                                if (width != null &&
                                                                                                height !=
                                                                                                        null
                                                                                ) {
                                                                                        onResizeWindow(
                                                                                                selectedWindow
                                                                                                        .id,
                                                                                                width,
                                                                                                height
                                                                                        )
                                                                                }
                                                                        }
                                                                ) { Text(stringResource(R.string.remote_desktop_resize_selected)) }

                                                                OutlinedTextField(
                                                                        value = targetDesktopText,
                                                                        onValueChange = {
                                                                                targetDesktopText =
                                                                                        it
                                                                        },
                                                                        label = {
                                                                                Text(stringResource(R.string.remote_desktop_desktop_label))
                                                                        },
                                                                        modifier =
                                                                                Modifier.fillMaxWidth(),
                                                                        singleLine = true
                                                                )
                                                                FilledTonalButton(
                                                                        onClick = {
                                                                                val desktop =
                                                                                        targetDesktopText
                                                                                                .toIntOrNull()
                                                                                if (desktop != null
                                                                                ) {
                                                                                        onMoveWindowToDesktop(
                                                                                                selectedWindow
                                                                                                        .id,
                                                                                                desktop
                                                                                        )
                                                                                }
                                                                        }
                                                                ) { Text(stringResource(R.string.remote_desktop_move_selected)) }
                                                        }
                                                }
                                        }
                                }
                        }

                        // Utility key row — shown while the remote keyboard is open
                        AnimatedVisibility(
                                visible = isRemoteKeyboardOpen,
                                enter =
                                        slideInVertically(initialOffsetY = { it }) +
                                                fadeIn(),
                                exit =
                                        slideOutVertically(targetOffsetY = { it }) +
                                                fadeOut()
                        ) {
                                val utilKeys =
                                        listOf(
                                                Triple(
                                                        "Esc",
                                                        stringResource(R.string.cd_key_escape),
                                                        27
                                                ),
                                                Triple(
                                                        "Tab",
                                                        stringResource(R.string.cd_key_tab),
                                                        9
                                                ),
                                                Triple(
                                                        "⌫",
                                                        stringResource(R.string.cd_key_backspace),
                                                        8
                                                ),
                                                Triple(
                                                        "↵",
                                                        stringResource(R.string.cd_key_enter),
                                                        13
                                                ),
                                                Triple(
                                                        "←",
                                                        stringResource(
                                                                R.string.cd_key_arrow_left
                                                        ),
                                                        37
                                                ),
                                                Triple(
                                                        "↑",
                                                        stringResource(R.string.cd_key_arrow_up),
                                                        38
                                                ),
                                                Triple(
                                                        "↓",
                                                        stringResource(
                                                                R.string.cd_key_arrow_down
                                                        ),
                                                        40
                                                ),
                                                Triple(
                                                        "→",
                                                        stringResource(
                                                                R.string.cd_key_arrow_right
                                                        ),
                                                        39
                                                ),
                                                Triple(
                                                        "Del",
                                                        stringResource(R.string.cd_key_delete),
                                                        46
                                                ),
                                                Triple(
                                                        "⊞",
                                                        stringResource(R.string.cd_key_windows),
                                                        91
                                                ),
                                        )
                                Row(
                                        modifier =
                                                Modifier.fillMaxWidth()
                                                        .background(
                                                                MaterialTheme.colorScheme
                                                                        .surfaceVariant
                                                                        .copy(alpha = 0.95f)
                                                        )
                                                        .horizontalScroll(rememberScrollState())
                                                        .padding(
                                                                vertical = 6.dp,
                                                                horizontal = 12.dp
                                                        ),
                                        horizontalArrangement =
                                                Arrangement.spacedBy(8.dp)
                                ) {
                                        utilKeys.forEach { (label, cd, vk) ->
                                                AssistChip(
                                                        onClick = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .VIRTUAL_KEY
                                                                )
                                                                onSendKeyPress(vk)
                                                        },
                                                        label = { Text(label) },
                                                        modifier =
                                                                Modifier.semantics {
                                                                        contentDescription = cd
                                                                },
                                                        colors =
                                                                AssistChipDefaults.assistChipColors(
                                                                        containerColor =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .secondaryContainer,
                                                                        labelColor =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSecondaryContainer
                                                                )
                                                )
                                        }
                                }
                        }
                }
        }
}

@Preview(showBackground = true)
@Composable
private fun RemoteDesktopScreenPreview() {
    RemExTheme {
        RemoteDesktopScreenContent(
            uiState = RemoteDesktopUiState(
                isStreaming = true,
                capabilityState = RemoteDesktopCapabilityState(supportsRemoteDesktop = true),
                isFullscreen = false
            ),
            currentBitmap = null,
            config = RemoteDesktopConfigState(quality = 70, targetFps = 60),
            onSetFullscreen = {},
            onStartStreaming = {},
            onStopStreaming = {},
            onSendText = {},
            onSendKeyPress = {},
            onSendMouseDown = { _, _, _ -> },
            onSendMouseClick = {},
            onSendMouseUp = {},
            onSendMouseMove = { _, _ -> },
            onSendMouseAbsolute = { _, _ -> },
            onSendMouseAbsoluteClick = { _, _, _ -> },
            onSendMouseScroll = { _, _ -> },
            onUpdateQuality = {},
            onUpdateTargetFps = {},
            onUpdateDirectTouch = {},
            onUpdatePointerSpeed = {},
            onUpdateScrollSensitivity = { _, _ -> },
            windowResults = emptyList(),
            windowActionError = null,
            onQueryWindows = {},
            onActivateWindow = {},
            onRaiseWindow = {},
            onMinimizeWindow = {},
            onCloseWindow = {},
            onResizeWindow = { _, _, _ -> },
            onMoveWindowToDesktop = { _, _ -> },
            getHostScreenSize = { Pair(1920, 1080) },
            currentFrameTimestamp = 0L,
            fps = 60f,
            showFpsOverlay = true,
            onToggleFpsOverlay = {}
        )
    }
}

@Composable
private fun RepeatingIconButton(
        onClick: () -> Unit,
        icon: androidx.compose.ui.graphics.vector.ImageVector,
        description: String
) {
        val interactionSource = remember { MutableInteractionSource() }
        val isPressed by interactionSource.collectIsPressedAsState()

        LaunchedEffect(isPressed) {
                if (isPressed) {
                        var delayTime = 400L
                        while (true) {
                                onClick()
                                delay(delayTime)
                                delayTime = (delayTime * 0.8f).toLong().coerceAtLeast(50L)
                        }
                }
        }

        IconButton(onClick = {}, interactionSource = interactionSource) {
                Icon(icon, contentDescription = description)
        }
}
