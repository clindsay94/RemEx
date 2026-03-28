package com.clindsay94.remex.ui.screens

import androidx.activity.compose.LocalActivity
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.KeyboardDoubleArrowDown
import androidx.compose.material.icons.filled.KeyboardDoubleArrowUp
import androidx.compose.material.icons.filled.Monitor
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerType
import androidx.compose.ui.input.pointer.isSecondaryPressed
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.viewmodel.compose.viewModel
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
    val desktopPrefs by viewModel.savedDesktopDefaults.collectAsState()

    val activity = LocalActivity.current
    var isFullscreen by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    var showMouseControls by remember { mutableStateOf(true) }
    val sheetState = rememberModalBottomSheetState()

    var zoomFactor by remember { mutableFloatStateOf(1f) }
    var panOffsetX by remember { mutableFloatStateOf(0f) }
    var panOffsetY by remember { mutableFloatStateOf(0f) }
    var cursorX by remember { mutableFloatStateOf(0f) }
    var cursorY by remember { mutableFloatStateOf(0f) }
    var imageSize by remember { mutableStateOf(IntSize.Zero) }
    var isStylusActive by remember { mutableStateOf(false) }
    var stylusButtonPressed by remember { mutableStateOf(false) }
    var lastTapTimeMs by remember { mutableStateOf(0L) }
    val tapDebounceMs = 150L

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
        val frame = currentFrame ?: return Offset.Zero
        val (hostW, hostH) = viewModel.getHostScreenSize()

        // 1. Reverse the graphicsLayer zoom/pan transform.
        //    graphicsLayer scales around the composable center and then translates.
        val centerX = imageSize.width / 2f
        val centerY = imageSize.height / 2f
        val adjustedX = (localOffset.x - centerX - panOffsetX) / zoomFactor + centerX
        val adjustedY = (localOffset.y - centerY - panOffsetY) / zoomFactor + centerY

        // 2. Compute the letterbox offsets using the *actual bitmap* dimensions.
        //    This matches exactly what ContentScale.Fit does — not the host metadata.
        val bmpAspect = frame.width.toFloat() / frame.height.toFloat()
        val boxAspect = imageSize.width.toFloat() / imageSize.height.toFloat()

        val effectiveW: Float
        val effectiveH: Float
        val letterboxX: Float
        val letterboxY: Float

        if (bmpAspect > boxAspect) {
            // Image is wider than box → full width, black bars top/bottom
            effectiveW = imageSize.width.toFloat()
            effectiveH = effectiveW / bmpAspect
            letterboxX = 0f
            letterboxY = (imageSize.height - effectiveH) / 2f
        } else {
            // Image is taller than box → full height, black bars left/right
            effectiveH = imageSize.height.toFloat()
            effectiveW = effectiveH * bmpAspect
            letterboxX = (imageSize.width - effectiveW) / 2f
            letterboxY = 0f
        }

        // 3. Map to 0..1 relative position within the visible image content
        val relativeX = ((adjustedX - letterboxX) / effectiveW).coerceIn(0f, 1f)
        val relativeY = ((adjustedY - letterboxY) / effectiveH).coerceIn(0f, 1f)

        // 4. Scale to host screen coordinates
        return Offset(relativeX * hostW, relativeY * hostH)
    }

    Scaffold(
        topBar = {
            if (!isFullscreen) {
                TopAppBar(
                    title = { Text("Remote Desktop", fontWeight = FontWeight.Bold) },
                    colors = TopAppBarDefaults.topAppBarColors(
                        containerColor = MaterialTheme.colorScheme.surface,
                        titleContentColor = MaterialTheme.colorScheme.onSurface
                    ),
                    actions = {
                        // Toggle integrated mouse controls
                        IconButton(onClick = { showMouseControls = !showMouseControls }) {
                            Icon(
                                if (showMouseControls) Icons.Default.Mouse else Icons.Default.TouchApp,
                                contentDescription = if (showMouseControls) "Hide mouse controls" else "Show mouse controls",
                                tint = if (showMouseControls) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        IconButton(onClick = { showSettings = true }) {
                            Icon(Icons.Default.Tune, contentDescription = "Settings")
                        }
                        IconButton(onClick = { isFullscreen = !isFullscreen }) {
                            Icon(
                                imageVector = Icons.Default.Fullscreen,
                                contentDescription = "Toggle fullscreen"
                            )
                        }
                        if (isStreaming) {
                            IconButton(onClick = { viewModel.stopStreaming() }) {
                                Icon(
                                    Icons.Default.Stop,
                                    contentDescription = "Stop",
                                    tint = MaterialTheme.colorScheme.error
                                )
                            }
                        } else {
                            IconButton(
                                onClick = { viewModel.startStreaming() },
                                enabled = capabilityState.supportsRemoteDesktop
                            ) {
                                Icon(
                                    Icons.Default.PlayArrow,
                                    contentDescription = "Start",
                                    tint = if (capabilityState.supportsRemoteDesktop) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                    }
                )
            }
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(if (isFullscreen) PaddingValues(0.dp) else padding)
                .background(Color.Black)
        ) {
            // --- Stream viewport ---
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .onGloballyPositioned { imageSize = it.size }
                    // Passive observer: detect stylus vs finger without consuming events
                    .pointerInput(Unit) {
                        awaitPointerEventScope {
                            while (true) {
                                val event = awaitPointerEvent(PointerEventPass.Initial)
                                val stylusChange =
                                    event.changes.firstOrNull { it.type == PointerType.Stylus }
                                if (stylusChange != null) {
                                    isStylusActive = true
                                    if (stylusChange.pressed) {
                                        stylusButtonPressed = event.buttons.isSecondaryPressed
                                    }
                                } else if (event.changes.any { it.type == PointerType.Touch }) {
                                    isStylusActive = false
                                    stylusButtonPressed = false
                                }
                            }
                        }
                    }
                    .pointerInput(isStreaming, desktopPrefs.directTouch) {
                        detectTapGestures(
                            onTap = { offset ->
                                val now = System.currentTimeMillis()
                                if (isStreaming && now - lastTapTimeMs > tapDebounceMs) {
                                    lastTapTimeMs = now
                                    if (desktopPrefs.directTouch || isStylusActive) {
                                        // S-Pen button held = right-click; otherwise left-click
                                        val button = if (stylusButtonPressed) 2 else 1
                                        val hostOffset = mapLocalToHost(offset)
                                        viewModel.sendMouseAbsoluteClick(
                                            button,
                                            hostOffset.x.toInt(),
                                            hostOffset.y.toInt()
                                        )
                                        cursorX = offset.x
                                        cursorY = offset.y
                                    } else {
                                        viewModel.sendMouseClick(1)
                                    }
                                }
                            },
                            onLongPress = { offset ->
                                val now = System.currentTimeMillis()
                                if (isStreaming && now - lastTapTimeMs > tapDebounceMs) {
                                    lastTapTimeMs = now
                                    if (desktopPrefs.directTouch || isStylusActive) {
                                        val hostOffset = mapLocalToHost(offset)
                                        viewModel.sendMouseAbsoluteClick(
                                            2,
                                            hostOffset.x.toInt(),
                                            hostOffset.y.toInt()
                                        )
                                    } else {
                                        viewModel.sendMouseClick(2)
                                    }
                                }
                            }
                        )
                    }
                    .pointerInput(isStreaming, desktopPrefs.directTouch) {
                        detectDragGestures(
                            onDrag = { change, dragAmount ->
                                if (isStreaming) {
                                    change.consume()

                                    val isStylus = change.type == PointerType.Stylus
                                    if (isStylus || desktopPrefs.directTouch) {
                                        val hostOffset = mapLocalToHost(change.position)
                                        viewModel.sendMouseAbsolute(
                                            hostOffset.x.toInt(),
                                            hostOffset.y.toInt()
                                        )
                                        cursorX = change.position.x
                                        cursorY = change.position.y
                                    } else {
                                        viewModel.sendMouseMove(dragAmount.x, dragAmount.y)
                                        cursorX += dragAmount.x
                                        cursorY += dragAmount.y
                                    }
                                }
                            }
                        )
                    }
                    .pointerInput(isStreaming) {
                        // Multitouch only: pinch to zoom, two-finger pan.
                        // Single-finger drags are handled by detectDragGestures above.
                        awaitPointerEventScope {
                            while (true) {
                                val event = awaitPointerEvent()
                                val pressed = event.changes.filter { it.pressed }
                                if (pressed.size >= 2 && isStreaming) {
                                    // Consume all changes to prevent single-finger handlers from firing
                                    event.changes.forEach { it.consume() }

                                    val p1 = pressed[0].position
                                    val p2 = pressed[1].position
                                    val currentDist = kotlin.math.sqrt(
                                        (p1.x - p2.x) * (p1.x - p2.x) + (p1.y - p2.y) * (p1.y - p2.y)
                                    )
                                    val currentCenter =
                                        Offset((p1.x + p2.x) / 2f, (p1.y + p2.y) / 2f)

                                    val prevP1 = pressed[0].previousPosition
                                    val prevP2 = pressed[1].previousPosition
                                    val prevDist = kotlin.math.sqrt(
                                        (prevP1.x - prevP2.x) * (prevP1.x - prevP2.x) + (prevP1.y - prevP2.y) * (prevP1.y - prevP2.y)
                                    )
                                    val prevCenter = Offset(
                                        (prevP1.x + prevP2.x) / 2f,
                                        (prevP1.y + prevP2.y) / 2f
                                    )

                                    // Pinch zoom
                                    if (prevDist > 10f) {
                                        val zoomDelta = currentDist / prevDist
                                        zoomFactor = (zoomFactor * zoomDelta).coerceIn(1f, 4f)
                                    }

                                    // Two-finger pan
                                    val panDelta = currentCenter - prevCenter
                                    if (zoomFactor > 1f) {
                                        panOffsetX += panDelta.x
                                        panOffsetY += panDelta.y
                                    } else {
                                        panOffsetX = 0f
                                        panOffsetY = 0f
                                        if (panDelta.x != 0f || panDelta.y != 0f) {
                                            viewModel.sendMouseScroll(
                                                panDelta.x.toInt(),
                                                (-panDelta.y).toInt()
                                            )
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

                    // Cursor indicator — adapts to input method
                    if (isStreaming) {
                        val cursorSizeDp = if (isStylusActive) 8.dp else 14.dp
                        Box(
                            modifier = Modifier
                                .offset {
                                    val halfPx = (cursorSizeDp / 2).roundToPx()
                                    IntOffset(
                                        cursorX.roundToInt() - halfPx,
                                        cursorY.roundToInt() - halfPx
                                    )
                                }
                                .size(cursorSizeDp)
                                .clip(CircleShape)
                                .background(
                                    when {
                                        isStylusActive -> MaterialTheme.colorScheme.tertiary.copy(
                                            alpha = 0.8f
                                        )

                                        desktopPrefs.directTouch -> MaterialTheme.colorScheme.primary.copy(
                                            alpha = 0.5f
                                        )

                                        else -> MaterialTheme.colorScheme.inverseSurface.copy(alpha = 0.6f)
                                    }
                                )
                                .border(
                                    width = if (isStylusActive) 1.5.dp else 1.dp,
                                    color = MaterialTheme.colorScheme.outline.copy(alpha = if (isStylusActive) 0.5f else 0.3f),
                                    shape = CircleShape
                                )
                        )
                    }
                } else {
                    // Empty state placeholder
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                            imageVector = Icons.Default.Monitor,
                            contentDescription = null,
                            modifier = Modifier.size(64.dp),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
                        )
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(
                            text = when {
                                desktopError != null -> desktopError!!
                                !capabilityState.supportsRemoteDesktop -> capabilityState.unavailableReason
                                    ?: "Remote desktop unavailable"

                                isStreaming -> "Waiting for frames\u2026"
                                else -> "Stream Stopped"
                            },
                            color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f),
                            style = MaterialTheme.typography.bodyLarge
                        )
                        if (!isStreaming && capabilityState.supportsRemoteDesktop) {
                            Spacer(modifier = Modifier.height(16.dp))
                            FilledTonalButton(onClick = { viewModel.startStreaming() }) {
                                Icon(
                                    Icons.Default.PlayArrow,
                                    contentDescription = null,
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(modifier = Modifier.width(8.dp))
                                Text("Start Streaming")
                            }
                        }
                    }
                }

                // Fullscreen exit FAB
                if (isFullscreen) {
                    FilledTonalIconButton(
                        onClick = { isFullscreen = false },
                        modifier = Modifier
                            .align(Alignment.TopEnd)
                            .padding(16.dp),
                        colors = IconButtonDefaults.filledTonalIconButtonColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerHighest.copy(
                                alpha = 0.7f
                            )
                        )
                    ) {
                        Icon(
                            Icons.Default.FullscreenExit,
                            contentDescription = "Exit Fullscreen",
                            tint = MaterialTheme.colorScheme.onSurface
                        )
                    }

                    // Fullscreen mouse toggle
                    FilledTonalIconButton(
                        onClick = { showMouseControls = !showMouseControls },
                        modifier = Modifier
                            .align(Alignment.TopStart)
                            .padding(16.dp),
                        colors = IconButtonDefaults.filledTonalIconButtonColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerHighest.copy(
                                alpha = 0.7f
                            )
                        )
                    ) {
                        Icon(
                            if (showMouseControls) Icons.Default.Mouse else Icons.Default.TouchApp,
                            contentDescription = "Toggle mouse",
                            tint = MaterialTheme.colorScheme.onSurface
                        )
                    }
                }
            }

            // --- Integrated Remote Mouse Controls ---
            AnimatedVisibility(
                visible = isStreaming && showMouseControls,
                enter = slideInVertically(initialOffsetY = { it }) + fadeIn(),
                exit = slideOutVertically(targetOffsetY = { it }) + fadeOut()
            ) {
                Surface(
                    tonalElevation = 3.dp,
                    shadowElevation = 2.dp,
                    color = MaterialTheme.colorScheme.surfaceContainerHigh,
                    modifier = Modifier
                        .fillMaxWidth()
                        .let { if (isFullscreen) it.windowInsetsPadding(WindowInsets.navigationBars) else it }
                ) {
                    Column(
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                        verticalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        // Left Click / Right Click row
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            FilledTonalButton(
                                onClick = { viewModel.sendMouseClick(1) },
                                modifier = Modifier
                                    .weight(1f)
                                    .height(48.dp),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Icon(
                                    Icons.AutoMirrored.Filled.KeyboardArrowLeft,
                                    contentDescription = null,
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(modifier = Modifier.width(4.dp))
                                Text("Left Click", style = MaterialTheme.typography.labelLarge)
                            }
                            FilledTonalButton(
                                onClick = { viewModel.sendMouseClick(2) },
                                modifier = Modifier
                                    .weight(1f)
                                    .height(48.dp),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Text("Right Click", style = MaterialTheme.typography.labelLarge)
                                Spacer(modifier = Modifier.width(4.dp))
                                Icon(
                                    Icons.AutoMirrored.Filled.KeyboardArrowRight,
                                    contentDescription = null,
                                    modifier = Modifier.size(18.dp)
                                )
                            }
                        }

                        // Scroll + utility row
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceEvenly,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            IconButton(onClick = { viewModel.sendMouseScroll(0, 120) }) {
                                Icon(
                                    Icons.Default.KeyboardDoubleArrowUp,
                                    contentDescription = "Scroll Up",
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            IconButton(onClick = { viewModel.sendMouseScroll(0, -120) }) {
                                Icon(
                                    Icons.Default.KeyboardDoubleArrowDown,
                                    contentDescription = "Scroll Down",
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            // Zoom reset
                            if (zoomFactor > 1.05f) {
                                FilledTonalIconButton(
                                    onClick = {
                                        zoomFactor = 1f
                                        panOffsetX = 0f
                                        panOffsetY = 0f
                                    }
                                ) {
                                    Text(
                                        "1×",
                                        style = MaterialTheme.typography.labelMedium,
                                        fontWeight = FontWeight.Bold
                                    )
                                }
                            }
                        }
                    }
                }
            }

            // --- Settings Bottom Sheet ---
            if (showSettings) {
                ModalBottomSheet(
                    onDismissRequest = { showSettings = false },
                    sheetState = sheetState,
                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(24.dp)
                            .padding(bottom = 32.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                    ) {
                        Text(
                            "Stream Configuration",
                            style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.onSurface
                        )

                        Surface(
                            color = MaterialTheme.colorScheme.surfaceContainerHighest,
                            shape = MaterialTheme.shapes.medium,
                            tonalElevation = 1.dp
                        ) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column {
                                    Text(
                                        "Direct Touch",
                                        style = MaterialTheme.typography.bodyLarge,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Text(
                                        "Tap maps to screen position. S Pen always enabled.",
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                                Switch(
                                    checked = desktopPrefs.directTouch,
                                    onCheckedChange = { viewModel.updateDirectTouch(it) }
                                )
                            }
                        }

                        Spacer(modifier = Modifier.height(4.dp))

                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("Quality: ${config.quality}%", fontWeight = FontWeight.SemiBold)
                            Slider(
                                value = config.quality.toFloat(),
                                onValueChange = { viewModel.updateQuality(it.toInt()) },
                                valueRange = 1f..100f
                            )
                        }

                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(
                                "Target FPS: ${config.targetFps}",
                                fontWeight = FontWeight.SemiBold
                            )
                            Slider(
                                value = config.targetFps.toFloat(),
                                onValueChange = {
                                    viewModel.updateTargetFps(
                                        it.toInt().coerceIn(1, 120)
                                    )
                                },
                                valueRange = 1f..120f
                            )
                        }

                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(
                                "Scale: ${"%.2f".format(config.scale)}",
                                fontWeight = FontWeight.SemiBold
                            )
                            Slider(
                                value = config.scale,
                                onValueChange = { viewModel.updateScale(it) },
                                valueRange = 0.25f..1.0f
                            )
                        }

                        Text(
                            text = "Drag = move cursor \u2022 Pinch = zoom \u2022 Two-finger pan = scroll",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }
    }
}
