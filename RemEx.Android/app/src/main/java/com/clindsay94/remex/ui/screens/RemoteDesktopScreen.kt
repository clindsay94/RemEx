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
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.*
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
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

data class RemoteDesktopUiState(
        val isStreaming: Boolean = false,
        val capabilityState: RemoteDesktopCapabilityState = RemoteDesktopCapabilityState(),
        val desktopError: String? = null,
        val directTouch: Boolean = false,
        val pointerSpeed: Float = 1.0f,
        val vScrollSensitivity: Float = 1.0f,
        val hScrollSensitivity: Float = 1.0f,
        val hostCursorX: Float = -1f,
        val hostCursorY: Float = -1f,
        val isFullscreen: Boolean = false
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

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteDesktopScreen(viewModel: RemoteDesktopViewModel = viewModel()) {
        val currentFrame by viewModel.currentFrame.collectAsState()
        val currentBitmap = currentFrame?.bitmap
        val isStreaming by viewModel.isStreaming.collectAsState()
        val capabilityState by viewModel.capabilityState.collectAsState()
        val desktopError by viewModel.desktopError.collectAsState()
        val config by viewModel.configState.collectAsState()
        val directTouch by viewModel.directTouch.collectAsState()
        val pointerSpeed by viewModel.pointerSpeed.collectAsState()
        val vScrollSensitivity by viewModel.verticalScrollSensitivity.collectAsState()
        val hScrollSensitivity by viewModel.horizontalScrollSensitivity.collectAsState()
        val hostCursorX by viewModel.hostCursorX.collectAsState()
        val hostCursorY by viewModel.hostCursorY.collectAsState()
        val fps by viewModel.fps.collectAsState()
        val windowResults by viewModel.windowResults.collectAsState()
        val windowActionError by viewModel.windowActionError.collectAsState()
        val activeCodec by viewModel.activeCodecState.collectAsState()
        val streamPixelWidth by viewModel.streamPixelWidth.collectAsState()
        val streamPixelHeight by viewModel.streamPixelHeight.collectAsState()

        var isFullscreen by rememberSaveable { mutableStateOf(false) }
        var showFpsOverlay by rememberSaveable { mutableStateOf(false) }

        val uiState =
                RemoteDesktopUiState(
                        isStreaming = isStreaming,
                        capabilityState = capabilityState,
                        desktopError = desktopError,
                        directTouch = directTouch,
                        pointerSpeed = pointerSpeed,
                        vScrollSensitivity = vScrollSensitivity,
                        hScrollSensitivity = hScrollSensitivity,
                        hostCursorX = hostCursorX,
                        hostCursorY = hostCursorY,
                        isFullscreen = isFullscreen
                )

        RemoteDesktopScreenContent(
                uiState = uiState,
                currentBitmap = currentBitmap,
                config = config,
                onSetFullscreen = { isFullscreen = it },
                onStartStreaming = { viewModel.startStreaming() },
                onStopStreaming = { viewModel.stopStreaming() },
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
                onActiveH264DecoderChange = { decoder -> viewModel.activeH264Decoder = decoder }
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
        onActiveH264DecoderChange: (H264StreamDecoder?) -> Unit = {}
) {
        val activity = LocalActivity.current
        val scope = rememberCoroutineScope()
        val density = LocalDensity.current
        val view = LocalView.current

        var showSettings by remember { mutableStateOf(false) }
        val sheetState = rememberModalBottomSheetState()

        var zoomFactor by remember { mutableFloatStateOf(1f) }
        var panOffsetX by remember { mutableFloatStateOf(0f) }
        var panOffsetY by remember { mutableFloatStateOf(0f) }
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

        DisposableEffect(activity, uiState.isFullscreen) {
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
                                activity.requestedOrientation =
                                        ActivityInfo.SCREEN_ORIENTATION_SENSOR
                        } else {
                                controller.show(WindowInsetsCompat.Type.systemBars())
                                activity.requestedOrientation =
                                        ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
                        }
                        onDispose {
                                WindowCompat.setDecorFitsSystemWindows(window, true)
                                controller.show(WindowInsetsCompat.Type.systemBars())
                        }
                }
        }

        fun mapLocalToHost(localOffset: Offset): Offset {
                if (imageSize.width == 0 || imageSize.height == 0) return Offset.Zero
                // Guard against Offset.Unspecified (NaN) and any other non-finite values
                // that would produce NaN in the output and crash JSON serialization.
                if (!localOffset.x.isFinite() || !localOffset.y.isFinite()) return Offset.Zero
                val (hostW, hostH) = getHostScreenSize()
                val (hostLeft, hostTop) = getHostDesktopOffset()

                val centerX = imageSize.width / 2f
                val centerY = imageSize.height / 2f
                val adjustedX = (localOffset.x - centerX - panOffsetX) / zoomFactor + centerX
                val adjustedY = (localOffset.y - centerY - panOffsetY) / zoomFactor + centerY

                val bmpWidth = currentBitmap?.width?.toFloat() ?: 1920f
                val bmpHeight = currentBitmap?.height?.toFloat() ?: 1080f
                val bmpAspect = bmpWidth / bmpHeight
                val boxAspect = imageSize.width.toFloat() / imageSize.height.toFloat()

                val effectiveW: Float
                val effectiveH: Float
                val letterboxX: Float
                val letterboxY: Float

                if (bmpAspect > boxAspect) {
                        effectiveW = imageSize.width.toFloat()
                        effectiveH = effectiveW / bmpAspect
                        letterboxX = 0f
                        letterboxY = (imageSize.height - effectiveH) / 2f
                } else {
                        effectiveH = imageSize.height.toFloat()
                        effectiveW = effectiveH * bmpAspect
                        letterboxX = (imageSize.width - effectiveW) / 2f
                        letterboxY = 0f
                }

                val relativeX = ((adjustedX - letterboxX) / effectiveW).coerceIn(0f, 1f)
                val relativeY = ((adjustedY - letterboxY) / effectiveH).coerceIn(0f, 1f)

                return Offset(relativeX * hostW + hostLeft, relativeY * hostH + hostTop)
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
                                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                                keyboardActions = KeyboardActions(onSend = { onSendKeyPress(13) }),
                                modifier =
                                        Modifier.size(1.dp)
                                                .graphicsLayer { alpha = 0f }
                                                .focusRequester(focusRequester)
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
                                                                                if (host ==
                                                                                                Offset.Zero &&
                                                                                                (vx !=
                                                                                                        0f ||
                                                                                                        vy !=
                                                                                                                0f)
                                                                                )
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
                                                                                                                        } else {
                                                                                                                                // Mouse wheel scroll
                                                                                                                                // with accumulator
                                                                                                                                scrollAccumX +=
                                                                                                                                        moveDelta
                                                                                                                                                .x *
                                                                                                                                                0.5f *
                                                                                                                                                hScrollSensState
                                                                                                                                                        .value
                                                                                                                                scrollAccumY +=
                                                                                                                                        moveDelta
                                                                                                                                                .y *
                                                                                                                                                0.5f *
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
                                                                                                                        onSendMouseDown(
                                                                                                                                0,
                                                                                                                                hostPress
                                                                                                                                        .x
                                                                                                                                        .toInt(),
                                                                                                                                hostPress
                                                                                                                                        .y
                                                                                                                                        .toInt()
                                                                                                                        )
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
                                                                                                                onSendMouseDown(
                                                                                                                        0,
                                                                                                                        hostPress
                                                                                                                                .x
                                                                                                                                .toInt(),
                                                                                                                        hostPress
                                                                                                                                .y
                                                                                                                                .toInt()
                                                                                                                )
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
                                                                                                                onSendMouseAbsolute(
                                                                                                                        hostPos.x
                                                                                                                                .toInt(),
                                                                                                                        hostPos.y
                                                                                                                                .toInt()
                                                                                                                )
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
                                                                                                                        onSendMouseAbsoluteClick(
                                                                                                                                0,
                                                                                                                                hostPos.x
                                                                                                                                        .toInt(),
                                                                                                                                hostPos.y
                                                                                                                                        .toInt()
                                                                                                                        )
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
                                                                                                                        onSendMouseAbsoluteClick(
                                                                                                                                2,
                                                                                                                                hostPos.x
                                                                                                                                        .toInt(),
                                                                                                                                hostPos.y
                                                                                                                                        .toInt()
                                                                                                                        )
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
                                                AndroidView(
                                                        factory = { context ->
                                                                android.view.TextureView(context).apply {
                                                                        surfaceTextureListener = object : android.view.TextureView.SurfaceTextureListener {
                                                                                override fun onSurfaceTextureAvailable(surfaceTexture: android.graphics.SurfaceTexture, width: Int, height: Int) {
                                                                                        val surface = android.view.Surface(surfaceTexture)
                                                                                        // Use the encoded stream dimensions from DesktopMeta, not the surface view size
                                                                                        val decoder = H264StreamDecoder(streamPixelWidth, streamPixelHeight, surface)
                                                                                        onActiveH264DecoderChange(decoder)
                                                                                        Log.i(TAG, "H.264 stream surface created: encoded=${streamPixelWidth}x${streamPixelHeight}, surface=${width}x${height}")
                                                                                }
                                                                                override fun onSurfaceTextureSizeChanged(surface: android.graphics.SurfaceTexture, width: Int, height: Int) {}
                                                                                override fun onSurfaceTextureDestroyed(surface: android.graphics.SurfaceTexture): Boolean {
                                                                                        onActiveH264DecoderChange(null)
                                                                                        Log.i(TAG, "H.264 stream surface destroyed.")
                                                                                        return true
                                                                                }
                                                                                override fun onSurfaceTextureUpdated(surface: android.graphics.SurfaceTexture) {}
                                                                        }
                                                                }
                                                        },
                                                        modifier = Modifier.fillMaxSize()
                                                                .graphicsLayer {
                                                                        scaleX = zoomFactor
                                                                        scaleY = zoomFactor
                                                                        translationX = panOffsetX
                                                                        translationY = panOffsetY
                                                                }
                                                )
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

                                        // Cursor overlay: visible in absolute modes (direct
                                        // touch/stylus) OR when host
                                        // provides cursor position (trackpad mode)
                                        // Sentinel -1f from ViewModel means "no cursor reported
                                        // yet".
                                        // (0,0) is a valid on-screen position (top-left corner).
                                        val hasHostCursor =
                                                uiState.hostCursorX >= 0f &&
                                                        uiState.hostCursorY >= 0f
                                        val showCursor =
                                                uiState.isStreaming &&
                                                        (cursorVisible &&
                                                                (uiState.directTouch ||
                                                                        isStylusActive) ||
                                                                (!uiState.directTouch &&
                                                                        hasHostCursor))

                                        if (showCursor) {
                                                val cursorSizeDp =
                                                        if (isStylusActive) 6.dp else 12.dp

                                                // Use local cursor position if in touch mode,
                                                // otherwise map host cursor to
                                                // screen
                                                val (hostWidth, hostHeight) = getHostScreenSize()
                                                val (hostLeft, hostTop) = getHostDesktopOffset()
                                                val displayCursorX =
                                                        if (uiState.directTouch || isStylusActive) {
                                                                cursorX
                                                        } else {
                                                                // Map host cursor coordinates to
                                                                // screen coordinates using
                                                                // actual host dimensions
                                                                imageSize.width *
                                                                        ((uiState.hostCursorX -
                                                                                hostLeft) /
                                                                                hostWidth.toFloat())
                                                        }
                                                val displayCursorY =
                                                        if (uiState.directTouch || isStylusActive) {
                                                                cursorY
                                                        } else {
                                                                imageSize.height *
                                                                        ((uiState.hostCursorY -
                                                                                hostTop) /
                                                                                hostHeight
                                                                                        .toFloat())
                                                        }

                                                Box(
                                                        modifier =
                                                                Modifier.offset {
                                                                                val halfPx =
                                                                                        (cursorSizeDp /
                                                                                                        2)
                                                                                                .roundToPx()
                                                                                // Apply the same
                                                                                // zoom/pan
                                                                                // transform the
                                                                                // underlying Image
                                                                                // uses so the
                                                                                // cursor tracks
                                                                                // the frame as the
                                                                                // user zooms or
                                                                                // pans.
                                                                                val transformedX =
                                                                                        displayCursorX *
                                                                                                zoomFactor +
                                                                                                panOffsetX
                                                                                val transformedY =
                                                                                        displayCursorY *
                                                                                                zoomFactor +
                                                                                                panOffsetY
                                                                                IntOffset(
                                                                                        transformedX
                                                                                                .roundToInt() -
                                                                                                halfPx,
                                                                                        transformedY
                                                                                                .roundToInt() -
                                                                                                halfPx
                                                                                )
                                                                        }
                                                                        .size(cursorSizeDp)
                                                                        .clip(CircleShape)
                                                                        .background(
                                                                                if (isStylusActive)
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .tertiary
                                                                                else
                                                                                        Color.White
                                                                                                .copy(
                                                                                                        alpha =
                                                                                                                0.7f
                                                                                                )
                                                                        )
                                                                        .border(
                                                                                1.5.dp,
                                                                                Color.Black.copy(
                                                                                        alpha = 0.5f
                                                                                ),
                                                                                CircleShape
                                                                        )
                                                )
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
                                                                text = "${fps.toInt()} FPS",
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
                                        FilledTonalIconButton(
                                                onClick = { onSetFullscreen(false) },
                                                modifier =
                                                        Modifier.align(Alignment.TopEnd)
                                                                .padding(16.dp),
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
                                                                                                        "FPS",
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
                                                                                        "L",
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
                                                                                        "Left",
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
                                                                                        "M",
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
                                                                                        "Middle",
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
                                                                                        "R",
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
                                                                                        "Right",
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
                                        sheetState = sheetState
                                ) {
                                        Column(
                                                modifier =
                                                        Modifier.fillMaxWidth()
                                                                .padding(24.dp)
                                                                .padding(bottom = 32.dp),
                                                verticalArrangement = Arrangement.spacedBy(16.dp)
                                        ) {
                                                Text(
                                                        stringResource(
                                                                R.string.remote_desktop_config_title
                                                        ),
                                                        style = MaterialTheme.typography.titleLarge,
                                                        fontWeight = FontWeight.Bold
                                                )

                                                // Quality slider
                                                Column(
                                                        verticalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_quality_label,
                                                                        config.quality
                                                                ),
                                                                fontWeight = FontWeight.SemiBold
                                                        )
                                                        Slider(
                                                                value = config.quality.toFloat(),
                                                                onValueChange = {
                                                                        onUpdateQuality(it.toInt())
                                                                },
                                                                valueRange = 1f..100f
                                                        )
                                                }

                                                // FPS slider
                                                Column(
                                                        verticalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_fps_label,
                                                                        config.targetFps
                                                                ),
                                                                fontWeight = FontWeight.SemiBold
                                                        )
                                                        Slider(
                                                                value = config.targetFps.toFloat(),
                                                                onValueChange = {
                                                                        onUpdateTargetFps(
                                                                                it.toInt()
                                                                        )
                                                                },
                                                                valueRange = 1f..360f
                                                        )
                                                }

                                                HorizontalDivider()

                                                // Direct Touch toggle
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
                                                        Switch(
                                                                checked = uiState.directTouch,
                                                                onCheckedChange = {
                                                                        onUpdateDirectTouch(it)
                                                                }
                                                        )
                                                }

                                                // Pointer Speed slider
                                                Column(
                                                        verticalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        Text(
                                                                String.format(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_pointer_speed_label
                                                                        ),
                                                                        uiState.pointerSpeed
                                                                ),
                                                                fontWeight = FontWeight.SemiBold
                                                        )
                                                        Slider(
                                                                value = uiState.pointerSpeed,
                                                                onValueChange = {
                                                                        onUpdatePointerSpeed(it)
                                                                },
                                                                valueRange = 0.25f..3.0f,
                                                                steps = 10
                                                        )
                                                }

                                                // Vertical Scroll Sensitivity slider
                                                Column(
                                                        verticalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        Text(
                                                                String.format(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_v_scroll_sensitivity_label
                                                                        ),
                                                                        uiState.vScrollSensitivity
                                                                ),
                                                                fontWeight = FontWeight.SemiBold
                                                        )
                                                        Slider(
                                                                value = uiState.vScrollSensitivity,
                                                                onValueChange = {
                                                                        onUpdateScrollSensitivity(
                                                                                it,
                                                                                uiState.hScrollSensitivity
                                                                        )
                                                                },
                                                                valueRange = 0.1f..5.0f,
                                                                steps = 20
                                                        )
                                                }

                                                // Horizontal Scroll Sensitivity slider
                                                Column(
                                                        verticalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        Text(
                                                                String.format(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_h_scroll_sensitivity_label
                                                                        ),
                                                                        uiState.hScrollSensitivity
                                                                ),
                                                                fontWeight = FontWeight.SemiBold
                                                        )
                                                        Slider(
                                                                value = uiState.hScrollSensitivity,
                                                                onValueChange = {
                                                                        onUpdateScrollSensitivity(
                                                                                uiState.vScrollSensitivity,
                                                                                it
                                                                        )
                                                                },
                                                                valueRange = 0.1f..5.0f,
                                                                steps = 20
                                                        )
                                                }

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
                                                                text = "Window controls",
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleMedium,
                                                                fontWeight = FontWeight.Bold
                                                        )
                                                        Text(
                                                                text =
                                                                        "Backend: ${uiState.capabilityState.windowBackend ?: "host"}",
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
                                                                label = { Text("Search windows") },
                                                                modifier = Modifier.fillMaxWidth(),
                                                                singleLine = true
                                                        )

                                                        FilledTonalButton(
                                                                onClick = {
                                                                        onQueryWindows(windowSearch)
                                                                }
                                                        ) { Text("Refresh windows") }

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
                                                                                "No window results yet. Refresh to load the current desktop windows.",
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
                                                                                                                                                ?: "Unknown class"
                                                                                                                                )
                                                                                                                                window.desktopNumber
                                                                                                                                        ?.let {
                                                                                                                                                append(
                                                                                                                                                        " • desktop "
                                                                                                                                                )
                                                                                                                                                append(
                                                                                                                                                        it
                                                                                                                                                )
                                                                                                                                        }
                                                                                                                                if (window.isActive
                                                                                                                                ) {
                                                                                                                                        append(
                                                                                                                                                " • active"
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
                                                                                                                                "Activate"
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
                                                                                                                                "Raise"
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
                                                                                                                                "Minimize"
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
                                                                                                                                "Close"
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
                                                                                "Selected: ${selectedWindow.title}",
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
                                                                                                "Width"
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
                                                                                                "Height"
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
                                                                ) { Text("Resize selected") }

                                                                OutlinedTextField(
                                                                        value = targetDesktopText,
                                                                        onValueChange = {
                                                                                targetDesktopText =
                                                                                        it
                                                                        },
                                                                        label = {
                                                                                Text("Desktop #")
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
                                                                ) { Text("Move selected window") }
                                                        }
                                                }
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
