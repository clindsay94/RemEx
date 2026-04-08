package com.clindsay94.remex.ui.screens

import android.util.Log
import androidx.activity.compose.LocalActivity
import androidx.compose.animation.AnimatedVisibility
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.*
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteDesktopScreen(
    viewModel: RemoteDesktopViewModel = viewModel()
) {
    val currentFrame by viewModel.currentFrame.collectAsState()
    val isStreaming by viewModel.isStreaming.collectAsState()
    val capabilityState by viewModel.capabilityState.collectAsState()
    val desktopError by viewModel.desktopError.collectAsState()
    val config by viewModel.configState.collectAsState()

    val activity = LocalActivity.current
    val keyboardController = LocalSoftwareKeyboardController.current
    val scope = rememberCoroutineScope()
    
    var isFullscreen by remember { mutableStateOf(false) }
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

    // Throttle movement events to 30Hz (33ms) to prevent socket overflow
    var lastMoveEventTime by remember { mutableLongStateOf(0L) }

    DisposableEffect(activity, isFullscreen) {
        if (activity == null) {
            onDispose { }
        } else {
            val window = activity.window
            val controller = WindowInsetsControllerCompat(window, window.decorView)
            WindowCompat.setDecorFitsSystemWindows(window, !isFullscreen)
            if (isFullscreen) {
                controller.hide(WindowInsetsCompat.Type.systemBars())
                controller.systemBarsBehavior =
                    WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            } else {
                controller.show(WindowInsetsCompat.Type.systemBars())
            }
            onDispose {
                WindowCompat.setDecorFitsSystemWindows(window, true)
                controller.show(WindowInsetsCompat.Type.systemBars())
            }
        }
    }

    fun mapLocalToHost(localOffset: Offset): Offset {
        if (imageSize.width == 0 || imageSize.height == 0) return Offset.Zero
        val (hostW, hostH) = viewModel.getHostScreenSize()

        val centerX = imageSize.width / 2f
        val centerY = imageSize.height / 2f
        val adjustedX = (localOffset.x - centerX - panOffsetX) / zoomFactor + centerX
        val adjustedY = (localOffset.y - centerY - panOffsetY) / zoomFactor + centerY

        val bmpWidth = currentFrame?.width?.toFloat() ?: 1920f
        val bmpHeight = currentFrame?.height?.toFloat() ?: 1080f
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

        return Offset(relativeX * hostW, relativeY * hostH)
    }

    Scaffold(
        topBar = {
            if (!isFullscreen) {
                TopAppBar(
                    title = { Text(stringResource(R.string.screen_remote_desktop_title), fontWeight = FontWeight.Bold) },
                    actions = {
                        IconButton(onClick = { keyboardController?.show() }) {
                            Icon(Icons.Default.Keyboard, contentDescription = stringResource(R.string.cd_show_keyboard))
                        }
                        IconButton(onClick = { inputResetTrigger++ }) {
                            Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.cd_reset_input))
                        }
                        IconButton(onClick = { showSettings = true }) { Icon(Icons.Default.Tune, contentDescription = stringResource(R.string.cd_settings)) }
                        IconButton(onClick = { isFullscreen = !isFullscreen }) { Icon(Icons.Default.Fullscreen, contentDescription = stringResource(R.string.cd_toggle_fullscreen)) }
                        if (isStreaming) {
                            IconButton(onClick = { viewModel.stopStreaming() }) { Icon(Icons.Default.Stop, contentDescription = stringResource(R.string.cd_stop), tint = MaterialTheme.colorScheme.error) }
                        } else {
                            IconButton(onClick = { viewModel.startStreaming() }, enabled = capabilityState.supportsRemoteDesktop) {
                                Icon(Icons.Default.PlayArrow, contentDescription = null, tint = if (capabilityState.supportsRemoteDesktop) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                )
            }
        }
    ) { padding ->
        Column(modifier = Modifier.fillMaxSize().padding(if (isFullscreen) PaddingValues(0.dp) else padding).background(Color.Black)) {
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .onGloballyPositioned { imageSize = it.size }
                    .pointerInput(isStreaming, inputResetTrigger) {
                        if (!isStreaming) return@pointerInput
                        
                        awaitPointerEventScope {
                            var isMouseDown = false
                            var hasMovedSignificantly = false
                            var pressedButton = 0
                            var startTime = 0L
                            var lastDownPos = Offset.Zero

                            try {
                            while (true) {
                                val event = awaitPointerEvent()
                                val change = event.changes.first()
                                val isStylus = change.type == PointerType.Stylus
                                isStylusActive = isStylus

                                when (event.type) {
                                    PointerEventType.Move -> {
                                        val now = System.currentTimeMillis()
                                        val hostPos = mapLocalToHost(change.position)

                                        // Movement Throttle (30Hz)
                                        if (now - lastMoveEventTime >= 33) {
                                            if (isStylus) {
                                                viewModel.sendMouseAbsolute(hostPos.x.toInt(), hostPos.y.toInt())
                                            } else if (change.pressed) {
                                                val diff = change.position - change.previousPosition
                                                viewModel.sendMouseMove(diff.x, diff.y)
                                            }
                                            lastMoveEventTime = now
                                        }

                                        if (change.pressed && (change.position - lastDownPos).getDistance() > 5f) {
                                            hasMovedSignificantly = true
                                        }

                                        cursorVisible = true
                                        cursorX = change.position.x
                                        cursorY = change.position.y
                                    }

                                    PointerEventType.Press -> {
                                        startTime = System.currentTimeMillis()
                                        lastDownPos = change.position
                                        hasMovedSignificantly = false
                                        isMouseDown = true
                                        cursorVisible = true
                                        cursorX = change.position.x
                                        cursorY = change.position.y

                                        if (isStylus) {
                                            val hostPos = mapLocalToHost(change.position)
                                            // S-Pen barrel button (side button) = right mouse button
                                            // The property `buttons` is not available in earlier versions of Compose, so we check using `pressed`
                                            pressedButton = 0 // Stylus right click unsupported without buttons field
                                            viewModel.sendMouseDown(pressedButton, hostPos.x.toInt(), hostPos.y.toInt())
                                        } else {
                                            pressedButton = 0
                                            viewModel.sendMouseDown(0)
                                        }
                                    }

                                    PointerEventType.Release -> {
                                        if (isMouseDown) {
                                            val duration = System.currentTimeMillis() - startTime
                                            // Long press with no movement and left button pressed = right-click gesture
                                            // (Barrel button already sent right-click on press; skip long-press check for it)
                                            if (!hasMovedSignificantly && duration > 600 && pressedButton == 0) {
                                                viewModel.sendMouseUp(0)
                                                if (isStylus) {
                                                    val hostPos = mapLocalToHost(change.position)
                                                    viewModel.sendMouseDown(2, hostPos.x.toInt(), hostPos.y.toInt())
                                                } else {
                                                    viewModel.sendMouseDown(2)
                                                }
                                                viewModel.sendMouseUp(2)
                                            } else {
                                                viewModel.sendMouseUp(pressedButton)
                                            }
                                        }
                                        isMouseDown = false
                                        pressedButton = 0
                                    }

                                    PointerEventType.Exit -> {
                                        if (isMouseDown) {
                                            viewModel.sendMouseUp(pressedButton)
                                            viewModel.sendMouseUp(2)
                                        }
                                        isMouseDown = false
                                        pressedButton = 0
                                    }
                                }
                            }
                            } finally {
                                // Scope cancelled (streaming stopped or input reset) — release any held buttons
                                if (isMouseDown) {
                                    viewModel.sendMouseUp(pressedButton)
                                    viewModel.sendMouseUp(2)
                                }
                            }
                        }
                    }
                    .pointerInput(isStreaming, inputResetTrigger) {
                        if (!isStreaming) return@pointerInput
                        awaitPointerEventScope {
                            while (true) {
                                val event = awaitPointerEvent()
                                val pressed = event.changes.filter { it.pressed }
                                
                                if (pressed.size == 2) {
                                    event.changes.forEach { it.consume() }
                                    
                                    val p1 = pressed[0].position
                                    val p2 = pressed[1].position
                                    val currentDist = kotlin.math.sqrt((p1.x - p2.x) * (p1.x - p2.x) + (p1.y - p2.y) * (p1.y - p2.y))
                                    val currentCenter = Offset((p1.x + p2.x) / 2f, (p1.y + p2.y) / 2f)

                                    val prevP1 = pressed[0].previousPosition
                                    val prevP2 = pressed[1].previousPosition
                                    val prevDist = kotlin.math.sqrt((prevP1.x - prevP2.x) * (prevP1.x - prevP2.x) + (prevP1.y - prevP2.y) * (prevP1.y - prevP2.y))
                                    val prevCenter = Offset((prevP1.x + prevP2.x) / 2f, (prevP1.y + prevP2.y) / 2f)

                                    val distDelta = kotlin.math.abs(currentDist - prevDist)
                                    val moveDelta = (currentCenter - prevCenter)

                                    if (distDelta > 5f) {
                                        val zoomDelta = currentDist / prevDist
                                        zoomFactor = (zoomFactor * zoomDelta).coerceIn(1f, 4f)
                                    } else if (moveDelta.getDistance() > 2f) {
                                        if (zoomFactor > 1.05f) {
                                            panOffsetX += moveDelta.x
                                            panOffsetY += moveDelta.y
                                        } else {
                                            val scrollAmount = (moveDelta.y * 2).toInt()
                                            if (kotlin.math.abs(scrollAmount) >= 1) {
                                                viewModel.sendMouseScroll(0, -scrollAmount)
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    },
                contentAlignment = Alignment.Center
            ) {
                val safeFrame = currentFrame
                if (safeFrame != null && !safeFrame.isRecycled) {
                    Image(
                        bitmap = safeFrame.asImageBitmap(),
                        contentDescription = "Remote Desktop Frame",
                        modifier = Modifier
                            .fillMaxSize()
                            .graphicsLayer {
                                scaleX = zoomFactor
                                scaleY = zoomFactor
                                translationX = panOffsetX
                                translationY = panOffsetY
                            },
                        contentScale = ContentScale.Fit
                    )

                    if (isStreaming && cursorVisible) {
                        val cursorSizeDp = if (isStylusActive) 6.dp else 12.dp
                        Box(
                            modifier = Modifier
                                .offset {
                                    val halfPx = (cursorSizeDp / 2).roundToPx()
                                    IntOffset(cursorX.roundToInt() - halfPx, cursorY.roundToInt() - halfPx)
                                }
                                .size(cursorSizeDp)
                                .clip(CircleShape)
                                .background(if (isStylusActive) MaterialTheme.colorScheme.tertiary else Color.White.copy(alpha = 0.5f))
                                .border(1.dp, Color.Black.copy(alpha = 0.3f), CircleShape)
                        )
                    }
                } else {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(imageVector = Icons.Default.Monitor, contentDescription = null, modifier = Modifier.size(64.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f))
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(text = when {
                            desktopError != null -> desktopError!!
                            !capabilityState.supportsRemoteDesktop -> capabilityState.unavailableReason ?: stringResource(R.string.remote_desktop_unavailable)
                            isStreaming -> stringResource(R.string.remote_desktop_waiting)
                            else -> stringResource(R.string.remote_desktop_stopped)
                        }, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f), style = MaterialTheme.typography.bodyLarge)
                        if (!isStreaming && capabilityState.supportsRemoteDesktop) {
                            Spacer(modifier = Modifier.height(16.dp))
                            FilledTonalButton(onClick = { viewModel.startStreaming() }) {
                                Icon(Icons.Default.PlayArrow, contentDescription = null, modifier = Modifier.size(18.dp))
                                Spacer(modifier = Modifier.width(8.dp))
                                Text(stringResource(R.string.button_start_streaming))
                            }
                        }
                    }
                }

                if (isFullscreen) {
                    FilledTonalIconButton(
                        onClick = { isFullscreen = false },
                        modifier = Modifier.align(Alignment.TopEnd).padding(16.dp),
                        colors = IconButtonDefaults.filledTonalIconButtonColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHighest.copy(alpha = 0.7f))
                    ) {
                        Icon(Icons.Default.FullscreenExit, contentDescription = stringResource(R.string.cd_exit_fullscreen))
                    }
                }
            }

            if (isStreaming) {
                Surface(
                    tonalElevation = 3.dp,
                    color = MaterialTheme.colorScheme.surfaceContainerHigh,
                    modifier = Modifier.fillMaxWidth().let { if (isFullscreen) it.windowInsetsPadding(WindowInsets.navigationBars) else it }
                ) {
                    Row(
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
                        horizontalArrangement = Arrangement.SpaceEvenly,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        RepeatingIconButton(
                            onClick = { viewModel.sendMouseScroll(0, 120) },
                            icon = Icons.Default.KeyboardDoubleArrowUp,
                            description = stringResource(R.string.cd_scroll_up)
                        )
                        RepeatingIconButton(
                            onClick = { viewModel.sendMouseScroll(0, -120) },
                            icon = Icons.Default.KeyboardDoubleArrowDown,
                            description = stringResource(R.string.cd_scroll_down)
                        )
                        
                        if (zoomFactor > 1.05f) {
                            FilledTonalIconButton(onClick = { zoomFactor = 1f; panOffsetX = 0f; panOffsetY = 0f }) {
                                Text("1\u00d7", fontWeight = FontWeight.Bold)
                            }
                        }
                        IconButton(onClick = { zoomFactor = (zoomFactor + 0.5f).coerceIn(1f, 4f) }) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.cd_zoom_in)) }
                        IconButton(onClick = { zoomFactor = (zoomFactor - 0.5f).coerceIn(1f, 4f) }) { Icon(Icons.Default.Remove, contentDescription = stringResource(R.string.cd_zoom_out)) }
                    }
                }
            }

            if (showSettings) {
                ModalBottomSheet(onDismissRequest = { showSettings = false }, sheetState = sheetState) {
                    Column(modifier = Modifier.fillMaxWidth().padding(24.dp).padding(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        Text(stringResource(R.string.remote_desktop_config_title), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(stringResource(R.string.remote_desktop_quality_label, config.quality), fontWeight = FontWeight.SemiBold)
                            Slider(value = config.quality.toFloat(), onValueChange = { viewModel.updateQuality(it.toInt()) }, valueRange = 1f..100f)
                        }
                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(stringResource(R.string.remote_desktop_fps_label, config.targetFps), fontWeight = FontWeight.SemiBold)
                            Slider(value = config.targetFps.toFloat(), onValueChange = { viewModel.updateTargetFps(it.toInt()) }, valueRange = 1f..120f)
                        }
                        Text(text = stringResource(R.string.remote_desktop_controls_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
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

private fun lerp(start: Float, stop: Float, fraction: Float): Float = start + fraction * (stop - start)
