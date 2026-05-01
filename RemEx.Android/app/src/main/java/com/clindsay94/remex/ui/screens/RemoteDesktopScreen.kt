package com.clindsay94.remex.ui.screens

import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.util.Log
import android.view.HapticFeedbackConstants
import androidx.activity.compose.LocalActivity
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.nestedscroll.nestedScroll
import com.clindsay94.remex.ui.components.RemexScreenHeader
import com.clindsay94.remex.ui.screens.RemoteDesktopCapabilityState

private const val TAG = "RemoteDesktop"

private const val TAP_MAX_DURATION_MS = 200L
private const val LONG_PRESS_THRESHOLD_MS = 500L
private const val DOUBLE_TAP_WINDOW_MS = 300L

private const val FINGER_SLOP_DP = 8f
private const val STYLUS_SLOP_DP = 2f
private const val DOUBLE_TAP_RADIUS_DP = 15f

private const val INERTIA_MIN_VELOCITY = 100f
private const val INERTIA_STOP_VELOCITY = 10f
private const val INERTIA_DECAY = 0.92f
private const val INERTIA_FRAME_MS = 16L

private const val MOVE_THROTTLE_MS = 32L

private data class TapContext(
        val time: Long,
        val position: Offset,
        val isStylus: Boolean
)

data class RemoteDesktopUiState(
    val isStreaming: Boolean = false,
    val capabilityState: RemoteDesktopCapabilityState = RemoteDesktopCapabilityState(false, null),
    val desktopError: String? = null,
    val directTouch: Boolean = false,
    val pointerSpeed: Float = 1.0f,
    val vScrollSensitivity: Float = 1.0f,
    val hScrollSensitivity: Float = 1.0f,
    val hostCursorX: Float = 0f,
    val hostCursorY: Float = 0f,
    val isFullscreen: Boolean = false
)

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

    var isFullscreen by rememberSaveable { mutableStateOf(false) }
    var showFpsOverlay by rememberSaveable { mutableStateOf(false) }

    val scrollBehavior = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()

    val uiState = RemoteDesktopUiState(
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

    Scaffold(
        modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
        topBar = {
            if (!isFullscreen) {
                RemexScreenHeader(
                    title = stringResource(R.string.screen_remote_desktop_title),
                    scrollBehavior = scrollBehavior
                )
            }
        }
    ) { innerPadding ->
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
            onSendMouseUp = { b -> viewModel.sendMouseUp(b) },
            onSendMouseMove = { x, y -> viewModel.sendMouseMove(x, y) },
            onSendMouseAbsolute = { x, y -> viewModel.sendMouseAbsolute(x, y) },
            onSendMouseAbsoluteClick = { b, x, y -> viewModel.sendMouseAbsoluteClick(b, x, y) },
            onSendMouseScroll = { x, y -> viewModel.sendMouseScroll(x, y) },
            onUpdateQuality = { viewModel.updateQuality(it) },
            onUpdateTargetFps = { viewModel.updateTargetFps(it) },
            onUpdateDirectTouch = { viewModel.updateDirectTouch(it) },
            onUpdatePointerSpeed = { viewModel.updatePointerSpeed(it) },
            onUpdateScrollSensitivity = { v, h -> viewModel.updateScrollSensitivity(v, h) },
            getHostScreenSize = { viewModel.getHostScreenSize() },
            currentFrameTimestamp = currentFrame?.timestamp,
            fps = fps,
            showFpsOverlay = showFpsOverlay,
            onToggleFpsOverlay = { showFpsOverlay = !showFpsOverlay },
            modifier = Modifier.padding(if (isFullscreen) PaddingValues(0.dp) else innerPadding)
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
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
    onSendMouseDown: (Int, Int, Int) -> Unit,
    onSendMouseUp: (Int) -> Unit,
    onSendMouseMove: (Float, Float) -> Unit,
    onSendMouseAbsolute: (Int, Int) -> Unit,
    onSendMouseAbsoluteClick: (Int, Int, Int) -> Unit,
    onSendMouseScroll: (Int, Int) -> Unit,
    onUpdateQuality: (Int) -> Unit,
    onUpdateTargetFps: (Int) -> Unit,
    onUpdateDirectTouch: (Boolean) -> Unit,
    onUpdatePointerSpeed: (Float) -> Unit,
    onUpdateScrollSensitivity: (Float, Float) -> Unit,
    getHostScreenSize: () -> Pair<Int, Int>,
    currentFrameTimestamp: Long?,
    fps: Float = 0f,
    showFpsOverlay: Boolean = false,
    onToggleFpsOverlay: () -> Unit = {},
    modifier: Modifier = Modifier
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
                        WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR
            } else {
                controller.show(WindowInsetsCompat.Type.systemBars())
                activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
            }
            onDispose {
                WindowCompat.setDecorFitsSystemWindows(window, true)
                controller.show(WindowInsetsCompat.Type.systemBars())
            }
        }
    }

    fun mapLocalToHost(localOffset: Offset): Offset {
        if (imageSize.width == 0 || imageSize.height == 0) return Offset.Zero

        // Desktop area in the UI (centered)
        val centerX = imageSize.width / 2f
        val centerY = imageSize.height / 2f

        // 1. Adjust for zoom and pan to get position relative to the centered image
        val adjustedX = (localOffset.x - centerX - panOffsetX) / zoomFactor + centerX
        val adjustedY = (localOffset.y - centerY - panOffsetY) / zoomFactor + centerY

        // 2. Map to 0..1 range based on the image size
        val normalizedX = adjustedX / imageSize.width
        val normalizedY = adjustedY / imageSize.height

        // 3. Map to host screen coordinates
        val (hostWidth, hostHeight) = getHostScreenSize()
        return Offset(normalizedX * hostWidth, normalizedY * hostHeight)
    }

    Column(modifier = modifier.fillMaxSize()) {
        Box(modifier = Modifier.weight(1f)) {
            if (!uiState.capabilityState.supportsRemoteDesktop) {
                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                                Icons.Default.DesktopAccessDisabled,
                                contentDescription = null,
                                modifier = Modifier.size(64.dp),
                                tint = MaterialTheme.colorScheme.error
                        )
                        Spacer(Modifier.height(16.dp))
                        Text(
                                uiState.capabilityState.unavailableReason ?: stringResource(R.string.remote_desktop_unavailable),
                                style = MaterialTheme.typography.titleMedium
                        )
                    }
                }
            } else if (!uiState.isStreaming) {
                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                                if (uiState.desktopError != null) uiState.desktopError
                                else stringResource(R.string.remote_desktop_stopped),
                                style = MaterialTheme.typography.bodyLarge,
                                color =
                                        if (uiState.desktopError != null)
                                                MaterialTheme.colorScheme.error
                                        else MaterialTheme.colorScheme.onSurface
                        )
                        Spacer(Modifier.height(24.dp))
                        Button(
                                onClick = onStartStreaming,
                                shape = MaterialTheme.shapes.medium,
                                contentPadding =
                                        PaddingValues(horizontal = 32.dp, vertical = 16.dp)
                        ) {
                            Icon(Icons.Default.PlayArrow, contentDescription = null)
                            Spacer(Modifier.width(8.dp))
                            Text(stringResource(R.string.button_start_streaming))
                        }
                    }
                }
            } else if (currentBitmap == null) {
                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        CircularProgressIndicator()
                        Spacer(Modifier.height(16.dp))
                        Text(
                                stringResource(R.string.remote_desktop_waiting),
                                style = MaterialTheme.typography.bodyMedium
                        )
                    }
                }
            } else {
                // RENDER STREAM
                BoxWithConstraints(
                        modifier =
                                Modifier.fillMaxSize()
                                        .background(Color.Black)
                                        .pointerInput(uiState.directTouch, zoomFactor) {
                                            detectTapGestures(
                                                    onTap = {
                                                        // Direct Touch: Send tap as click
                                                        if (uiState.directTouch) {
                                                            val hostPos = mapLocalToHost(it)
                                                            onSendMouseAbsoluteClick(
                                                                    1,
                                                                    hostPos.x.toInt(),
                                                                    hostPos.y.toInt()
                                                            )
                                                        }
                                                        showControlsWithTimer()
                                                    },
                                                    onDoubleTap = {
                                                        if (uiState.directTouch) {
                                                            val hostPos = mapLocalToHost(it)
                                                            onSendMouseAbsoluteClick(
                                                                    1,
                                                                    hostPos.x.toInt(),
                                                                    hostPos.y.toInt()
                                                            )
                                                            onSendMouseAbsoluteClick(
                                                                    1,
                                                                    hostPos.x.toInt(),
                                                                    hostPos.y.toInt()
                                                            )
                                                        }
                                                    }
                                            )
                                        }
                                        .pointerInput(
                                                uiState.directTouch,
                                                density,
                                                fingerSlopPx,
                                                stylusSlopPx
                                        ) {
                                            // CUSTOM TOUCH STATE
                                            var dragStartX = 0f
                                            var dragStartY = 0f
                                            var isDragging = false
                                            var lastMoveX = 0f
                                            var lastMoveY = 0f
                                            var lastMoveTime = 0L

                                            awaitPointerEventScope {
                                                while (true) {
                                                    val event = awaitPointerEvent()
                                                    val changes = event.changes
                                                    val isStylus =
                                                            changes.any {
                                                                it.type ==
                                                                        androidx.compose.ui.input
                                                                                .pointer
                                                                                .PointerType
                                                                                .Stylus
                                                            }
                                                    isStylusActive = isStylus

                                                    if (changes.size == 1) {
                                                        val change = changes[0]
                                                        if (change.pressed) {
                                                            if (!isDragging) {
                                                                dragStartX = change.position.x
                                                                dragStartY = change.position.y
                                                                isDragging = true
                                                                lastMoveX = dragStartX
                                                                lastMoveY = dragStartY
                                                            } else {
                                                                val dx =
                                                                        change.position.x -
                                                                                lastMoveX
                                                                val dy =
                                                                        change.position.y -
                                                                                lastMoveY
                                                                val now =
                                                                        System.currentTimeMillis()

                                                                if (uiState.directTouch) {
                                                                    // Map to host
                                                                    val hostPos =
                                                                            mapLocalToHost(
                                                                                    change.position
                                                                            )
                                                                    onSendMouseAbsolute(
                                                                            hostPos.x.toInt(),
                                                                            hostPos.y.toInt()
                                                                    )
                                                                } else {
                                                                    // Relative movement
                                                                    if (now - lastMoveTime >
                                                                                    MOVE_THROTTLE_MS
                                                                    ) {
                                                                        onSendMouseMove(
                                                                                dx *
                                                                                        uiState
                                                                                                .pointerSpeed,
                                                                                dy *
                                                                                        uiState
                                                                                                .pointerSpeed
                                                                        )
                                                                        lastMoveTime = now
                                                                    }
                                                                }
                                                                lastMoveX = change.position.x
                                                                lastMoveY = change.position.y
                                                            }
                                                            change.consume()
                                                        } else {
                                                            isDragging = false
                                                        }
                                                    } else if (changes.size == 2) {
                                                        // Two-finger scroll
                                                        val now = System.currentTimeMillis()
                                                        if (now - lastMoveTime > MOVE_THROTTLE_MS) {
                                                            val dy =
                                                                    changes.sumOf {
                                                                            (it.position.y -
                                                                                            it
                                                                                                    .previousPosition
                                                                                                    .y)
                                                                                    .toDouble()
                                                                        }
                                                                        .toFloat() / 2f
                                                            val dx =
                                                                    changes.sumOf {
                                                                            (it.position.x -
                                                                                            it
                                                                                                    .previousPosition
                                                                                                    .x)
                                                                                    .toDouble()
                                                                        }
                                                                        .toFloat() / 2f

                                                            if (dy != 0f || dx != 0f) {
                                                                onSendMouseScroll(
                                                                        (dx *
                                                                                        uiState
                                                                                                .hScrollSensitivity)
                                                                                .toInt(),
                                                                        (dy *
                                                                                        uiState
                                                                                                .vScrollSensitivity)
                                                                                .toInt()
                                                                )
                                                            }
                                                            lastMoveTime = now
                                                        }
                                                        changes.forEach { it.consume() }
                                                    }
                                                }
                                            }
                                        }
                ) {
                    val containerWidth = maxWidth.value * density.density
                    val containerHeight = maxHeight.value * density.density

                    val bitmapWidth = currentBitmap.width.toFloat()
                    val bitmapHeight = currentBitmap.height.toFloat()

                    val scale =
                            (containerWidth / bitmapWidth).coerceAtMost(
                                    containerHeight / bitmapHeight
                            )
                    val displayWidth = bitmapWidth * scale
                    val displayHeight = bitmapHeight * scale

                    imageSize = IntSize(displayWidth.toInt(), displayHeight.toInt())

                    Box(
                            modifier =
                                    Modifier.fillMaxSize()
                                            .graphicsLayer {
                                                scaleX = zoomFactor
                                                scaleY = zoomFactor
                                                translationX = panOffsetX
                                                translationY = panOffsetY
                                            },
                            contentAlignment = Alignment.Center
                    ) {
                        androidx.compose.foundation.Image(
                                bitmap = currentBitmap.asImageBitmap(),
                                contentDescription =
                                        stringResource(R.string.cd_remote_desktop_frame),
                                modifier = Modifier.size(displayWidth.dp, displayHeight.dp)
                        )
                    }

                    // FPS Overlay
                    if (showFpsOverlay) {
                        Surface(
                                color = Color.Black.copy(alpha = 0.6f),
                                shape = MaterialTheme.shapes.small,
                                modifier =
                                        Modifier.align(Alignment.TopStart)
                                                .padding(8.dp)
                                                .statusBarsPadding()
                        ) {
                            Text(
                                    "FPS: ${fps.toInt()}",
                                    color = Color.White,
                                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                                    style = MaterialTheme.typography.labelSmall
                            )
                        }
                    }

                    // On-screen controls overlay
                    androidx.compose.animation.AnimatedVisibility(
                            visible = controlsVisible,
                            enter = fadeIn(),
                            exit = fadeOut(),
                            modifier = Modifier.fillMaxSize()
                    ) {
                        Box(modifier = Modifier.fillMaxSize()) {
                            // Top action bar
                            Surface(
                                    color = Color.Black.copy(alpha = 0.4f),
                                    modifier = Modifier.fillMaxWidth().align(Alignment.TopCenter)
                            ) {
                                Row(
                                        modifier =
                                                Modifier.statusBarsPadding()
                                                        .padding(
                                                                horizontal = 16.dp,
                                                                vertical = 8.dp
                                                        ),
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                        verticalAlignment = Alignment.CenterVertically
                                ) {
                                    IconButton(onClick = onStopStreaming) {
                                        Icon(
                                                Icons.Default.Stop,
                                                contentDescription =
                                                        stringResource(R.string.cd_stop),
                                                tint = Color.White
                                        )
                                    }

                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        IconButton(onClick = { showSettings = true }) {
                                            Icon(
                                                    Icons.Default.Settings,
                                                    contentDescription =
                                                            stringResource(R.string.cd_settings),
                                                    tint = Color.White
                                            )
                                        }
                                        IconButton(
                                                onClick = {
                                                    view.performHapticFeedback(
                                                            HapticFeedbackConstants.KEYBOARD_TAP
                                                    )
                                                    focusRequester.requestFocus()
                                                }
                                        ) {
                                            Icon(
                                                    Icons.Default.Keyboard,
                                                    contentDescription =
                                                            stringResource(
                                                                    R.string.cd_show_keyboard
                                                            ),
                                                    tint = Color.White
                                            )
                                        }
                                        IconButton(
                                                onClick = {
                                                    zoomFactor = 1f
                                                    panOffsetX = 0f
                                                    panOffsetY = 0f
                                                }
                                        ) {
                                            Icon(
                                                    Icons.Default.RestartAlt,
                                                    contentDescription =
                                                            stringResource(R.string.cd_reset_input),
                                                    tint = Color.White
                                            )
                                        }
                                        IconButton(onClick = { onSetFullscreen(!uiState.isFullscreen) }) {
                                            Icon(
                                                    if (uiState.isFullscreen)
                                                            Icons.Default.FullscreenExit
                                                    else Icons.Default.Fullscreen,
                                                    contentDescription =
                                                            stringResource(
                                                                    R.string.cd_toggle_fullscreen
                                                            ),
                                                    tint = Color.White
                                            )
                                        }
                                    }
                                }
                            }

                            // FAB-style secondary controls
                            Column(
                                    modifier =
                                            Modifier.align(Alignment.BottomEnd)
                                                    .padding(16.dp)
                                                    .navigationBarsPadding(),
                                    verticalArrangement = Arrangement.spacedBy(12.dp),
                                    horizontalAlignment = Alignment.End
                            ) {
                                // Mouse controls expander
                                if (mouseControlsExpanded) {
                                    Column(
                                            verticalArrangement = Arrangement.spacedBy(12.dp),
                                            horizontalAlignment = Alignment.CenterHorizontally
                                    ) {
                                        SmallFloatingActionButton(
                                                onClick = { onSendMouseUp(1) },
                                                containerColor =
                                                        MaterialTheme.colorScheme.secondaryContainer
                                        ) {
                                            Icon(
                                                    Icons.Default.Mouse,
                                                    contentDescription =
                                                            stringResource(R.string.cd_mouse_left)
                                            )
                                        }
                                        SmallFloatingActionButton(
                                                onClick = { onSendMouseUp(2) },
                                                containerColor =
                                                        MaterialTheme.colorScheme.secondaryContainer
                                        ) {
                                            Icon(
                                                    Icons.Default.Mouse,
                                                    contentDescription =
                                                            stringResource(R.string.cd_mouse_right)
                                            )
                                        }
                                        SmallFloatingActionButton(
                                                onClick = { onSendMouseUp(3) },
                                                containerColor =
                                                        MaterialTheme.colorScheme.secondaryContainer
                                        ) {
                                            Icon(
                                                    Icons.Default.Mouse,
                                                    contentDescription =
                                                            stringResource(R.string.cd_mouse_middle)
                                            )
                                        }
                                    }
                                }

                                FloatingActionButton(
                                        onClick = {
                                            mouseControlsExpanded = !mouseControlsExpanded
                                        },
                                        containerColor = MaterialTheme.colorScheme.primaryContainer
                                ) {
                                    Icon(
                                            if (mouseControlsExpanded) Icons.Default.ExpandMore
                                            else Icons.Default.ExpandLess,
                                            contentDescription =
                                                    stringResource(R.string.cd_expand_mouse_controls)
                                    )
                                }
                            }

                            // Zoom controls (Left side)
                            Column(
                                    modifier =
                                            Modifier.align(Alignment.BottomStart)
                                                    .padding(16.dp)
                                                    .navigationBarsPadding(),
                                    verticalArrangement = Arrangement.spacedBy(12.dp)
                            ) {
                                SmallFloatingActionButton(onClick = { zoomFactor *= 1.2f }) {
                                    Icon(
                                            Icons.Default.Add,
                                            contentDescription = stringResource(R.string.cd_zoom_in)
                                    )
                                }
                                SmallFloatingActionButton(
                                        onClick = { zoomFactor = (zoomFactor / 1.2f).coerceAtLeast(1f) }
                                ) {
                                    Icon(
                                            Icons.Default.Remove,
                                            contentDescription = stringResource(R.string.cd_zoom_out)
                                    )
                                }
                            }
                        }
                    }

                    // Hidden text field for keyboard
                    Box(modifier = Modifier.size(0.dp).focusRequester(focusRequester)) {
                        androidx.compose.foundation.text.BasicTextField(
                                value = textValue,
                                onValueChange = {
                                    if (it.text.length > textValue.text.length) {
                                        val newChar = it.text.last()
                                        onSendText(newChar.toString())
                                    } else if (it.text.length < textValue.text.length) {
                                        onSendKeyPress(8) // Backspace
                                    }
                                    textValue = it
                                }
                        )
                    }
                }
            }
        }
    }

    if (showSettings) {
        ModalBottomSheet(
                onDismissRequest = { showSettings = false },
                sheetState = sheetState,
                containerColor = MaterialTheme.colorScheme.surfaceContainerHigh
        ) {
            Column(
                    modifier =
                            Modifier.fillMaxWidth()
                                    .padding(horizontal = 24.dp)
                                    .padding(bottom = 48.dp),
                    verticalArrangement = Arrangement.spacedBy(20.dp)
            ) {
                Text(
                        stringResource(R.string.remote_desktop_config_title),
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.Bold
                )

                // Quality
                Column {
                    Text(
                            stringResource(R.string.remote_desktop_quality_label, config.quality),
                            style = MaterialTheme.typography.labelMedium
                    )
                    Slider(
                            value = config.quality.toFloat(),
                            onValueChange = { onUpdateQuality(it.toInt()) },
                            valueRange = 10f..100f,
                            steps = 9
                    )
                }

                // FPS
                Column {
                    Text(
                            stringResource(R.string.remote_desktop_fps_label, config.targetFps),
                            style = MaterialTheme.typography.labelMedium
                    )
                    Slider(
                            value = config.targetFps.toFloat(),
                            onValueChange = { onUpdateTargetFps(it.toInt()) },
                            valueRange = 5f..120f
                    )
                }

                // Direct Touch Toggle
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                                stringResource(R.string.remote_desktop_direct_touch_label),
                                style = MaterialTheme.typography.bodyLarge,
                                fontWeight = FontWeight.Bold
                        )
                        Text(
                                stringResource(R.string.remote_desktop_direct_touch_desc),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    Switch(checked = uiState.directTouch, onCheckedChange = onUpdateDirectTouch)
                }

                // Controls Hint
                Card(
                        colors =
                                CardDefaults.cardColors(
                                        containerColor =
                                                MaterialTheme.colorScheme.secondaryContainer
                                )
                ) {
                    Row(
                            modifier = Modifier.padding(12.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(Icons.Default.Info, contentDescription = null, modifier = Modifier.size(20.dp))
                        Text(
                                text = stringResource(R.string.remote_desktop_controls_hint_v2),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }
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
