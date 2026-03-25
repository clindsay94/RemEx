package com.clindsay94.remex.ui.screens

import android.app.Activity
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Monitor
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalContext
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

    val activity = LocalContext.current as? Activity
    var isFullscreen by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    val sheetState = rememberModalBottomSheetState()
    
    var zoomFactor by remember { mutableFloatStateOf(1f) }
    var cursorX by remember { mutableFloatStateOf(0f) }
    var cursorY by remember { mutableFloatStateOf(0f) }
    var imageSize by remember { mutableStateOf(IntSize.Zero) }

    DisposableEffect(activity, isFullscreen) {
        if (activity == null) {
            onDispose { }
        } else {
            val window = activity.window
            val controller = WindowInsetsControllerCompat(window, window.decorView)
            WindowCompat.setDecorFitsSystemWindows(window, !isFullscreen)
            if (isFullscreen) {
                controller.hide(WindowInsetsCompat.Type.systemBars())
                controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
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
        
        // This mapping assumes ContentScale.Fit logic:
        // The image is scaled to fit while maintaining aspect ratio.
        val hostAspect = hostW.toFloat() / hostH
        val localAspect = imageSize.width.toFloat() / imageSize.height
        
        var effectiveImageWidth = imageSize.width.toFloat()
        var effectiveImageHeight = imageSize.height.toFloat()
        var offsetX = 0f
        var offsetY = 0f
        
        if (hostAspect > localAspect) {
            // Host is wider, local is limited by width
            effectiveImageHeight = effectiveImageWidth / hostAspect
            offsetY = (imageSize.height - effectiveImageHeight) / 2
        } else {
            // Host is taller, local is limited by height
            effectiveImageWidth = effectiveImageHeight * hostAspect
            offsetX = (imageSize.width - effectiveImageWidth) / 2
        }
        
        val relativeX = (localOffset.x - offsetX) / effectiveImageWidth
        val relativeY = (localOffset.y - offsetY) / effectiveImageHeight
        
        return Offset(
            (relativeX * hostW).coerceIn(0f, hostW.toFloat()),
            (relativeY * hostH).coerceIn(0f, hostH.toFloat())
        )
    }

    Scaffold(
        topBar = {
            if (!isFullscreen) {
                TopAppBar(
                    title = { Text("Remote Desktop", fontWeight = FontWeight.Bold) },
                    actions = {
                        IconButton(onClick = { showSettings = true }) {
                            Icon(Icons.Default.Tune, contentDescription = "Settings")
                        }
                        IconButton(onClick = { isFullscreen = !isFullscreen }) {
                            Icon(
                                imageVector = if (isFullscreen) Icons.Default.FullscreenExit else Icons.Default.Fullscreen,
                                contentDescription = "Toggle fullscreen"
                            )
                        }
                        if (isStreaming) {
                            IconButton(onClick = { viewModel.stopStreaming() }) {
                                Icon(Icons.Default.Stop, contentDescription = "Stop", tint = MaterialTheme.colorScheme.error)
                            }
                        } else {
                            IconButton(
                                onClick = { viewModel.startStreaming() },
                                enabled = capabilityState.supportsRemoteDesktop
                            ) {
                                Icon(Icons.Default.PlayArrow, contentDescription = "Start", tint = Color(0xFF4CAF50))
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
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .onGloballyPositioned { imageSize = it.size }
                    .pointerInput(isStreaming, desktopPrefs.directTouch) {
                        detectTapGestures(
                            onTap = { offset ->
                                if (!isStreaming) return@detectTapGestures
                                // Note: pointerInput doesn't easily expose the current event's pointer type here
                                // But detectDragGestures does. For tap, we'll check settings.
                                if (desktopPrefs.directTouch) {
                                    val hostOffset = mapLocalToHost(offset)
                                    viewModel.sendMouseAbsoluteClick(1, hostOffset.x.toInt(), hostOffset.y.toInt())
                                    cursorX = offset.x
                                    cursorY = offset.y
                                } else {
                                    viewModel.sendMouseClick(1)
                                }
                            },
                            onLongPress = { offset ->
                                if (!isStreaming) return@onLongPress
                                if (desktopPrefs.directTouch) {
                                    val hostOffset = mapLocalToHost(offset)
                                    viewModel.sendMouseAbsoluteClick(2, hostOffset.x.toInt(), hostOffset.y.toInt())
                                } else {
                                    viewModel.sendMouseClick(2)
                                }
                            }
                        )
                    }
                    .pointerInput(isStreaming, desktopPrefs.directTouch) {
                        detectDragGestures(
                            onDrag = { change, dragAmount ->
                                if (!isStreaming) return@detectDragGestures
                                change.consume()
                                
                                val isStylus = change.type == PointerType.Stylus
                                if (isStylus || desktopPrefs.directTouch) {
                                    val hostOffset = mapLocalToHost(change.position)
                                    viewModel.sendMouseAbsolute(hostOffset.x.toInt(), hostOffset.y.toInt())
                                    cursorX = change.position.x
                                    cursorY = change.position.y
                                } else {
                                    viewModel.sendMouseMove(dragAmount.x, dragAmount.y)
                                    cursorX += dragAmount.x
                                    cursorY += dragAmount.y
                                }
                            }
                        )
                    }
                    .pointerInput(isStreaming) {
                        detectTransformGestures { _, pan, zoom, _ ->
                            if (!isStreaming) return@detectTransformGestures
                            zoomFactor = (zoomFactor * zoom).coerceIn(1f, 4f)
                            if (pan.x != 0f || pan.y != 0f) {
                                viewModel.sendMouseScroll(pan.x.toInt(), (-pan.y).toInt())
                            }
                        }
                    },
                contentAlignment = Alignment.Center
            ) {
                if (currentFrame != null) {
                    Image(
                        bitmap = currentFrame!!.asImageBitmap(),
                        contentDescription = "Remote Desktop Frame",
                        modifier = Modifier
                            .fillMaxSize()
                            .graphicsLayer {
                                scaleX = zoomFactor
                                scaleY = zoomFactor
                            },
                        contentScale = ContentScale.Fit
                    )

                    if (isStreaming) {
                        // Improved cursor indicator
                        Box(
                            modifier = Modifier
                                .offset { IntOffset(cursorX.roundToInt() - 7, cursorY.roundToInt() - 7) }
                                .size(14.dp)
                                .clip(CircleShape)
                                .background(
                                    if (desktopPrefs.directTouch) MaterialTheme.colorScheme.primary.copy(alpha = 0.5f)
                                    else Color.White.copy(alpha = 0.6f)
                                )
                                .border(1.dp, Color.Black.copy(alpha = 0.3f), CircleShape)
                        )
                    }
                } else {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                            imageVector = Icons.Default.Monitor,
                            contentDescription = null,
                            modifier = Modifier.size(64.dp),
                            tint = Color.Gray
                        )
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(
                            text = when {
                                desktopError != null -> desktopError!!
                                !capabilityState.supportsRemoteDesktop -> capabilityState.unavailableReason ?: "Remote desktop unavailable"
                                isStreaming -> "Waiting for frames..."
                                else -> "Stream Stopped"
                            },
                            color = Color.Gray
                        )
                        if (!isStreaming && capabilityState.supportsRemoteDesktop) {
                            Button(onClick = { viewModel.startStreaming() }, modifier = Modifier.padding(16.dp)) {
                                Text("Start Streaming")
                            }
                        }
                    }
                }

                if (isFullscreen) {
                    IconButton(
                        onClick = { isFullscreen = false },
                        modifier = Modifier
                            .align(Alignment.TopEnd)
                            .padding(16.dp)
                            .background(Color.Black.copy(alpha = 0.5f), CircleShape)
                    ) {
                        Icon(Icons.Default.FullscreenExit, contentDescription = "Exit Fullscreen", tint = Color.White)
                    }
                }
            }

            if (showSettings) {
                ModalBottomSheet(
                    onDismissRequest = { showSettings = false },
                    sheetState = sheetState
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(24.dp)
                            .padding(bottom = 32.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                    ) {
                        Text("Stream Configuration", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)

                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text("Direct Touch (Absolute)", style = MaterialTheme.typography.bodyLarge)
                            Switch(
                                checked = desktopPrefs.directTouch,
                                onCheckedChange = { viewModel.updateDirectTouch(it) }
                            )
                        }
                        Text(
                            "When enabled, tapping the screen clicks that exact spot. S Pen always uses Direct Touch.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )

                        Spacer(modifier = Modifier.height(8.dp))

                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("Quality: ${config.quality}%", fontWeight = FontWeight.SemiBold)
                            Slider(
                                value = config.quality.toFloat(),
                                onValueChange = { viewModel.updateQuality(it.toInt()) },
                                valueRange = 1f..100f
                            )
                        }

                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("Target FPS: ${config.targetFps} (max 120)", fontWeight = FontWeight.SemiBold)
                            Slider(
                                value = config.targetFps.toFloat(),
                                onValueChange = { viewModel.updateTargetFps(it.toInt().coerceIn(1, 120)) },
                                valueRange = 1f..120f
                            )
                        }

                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("Scale: ${"%.2f".format(config.scale)}", fontWeight = FontWeight.SemiBold)
                            Slider(
                                value = config.scale,
                                onValueChange = { viewModel.updateScale(it) },
                                valueRange = 0.25f..1.0f
                            )
                        }

                        Text(
                            text = "Single finger drag = move cursor, pinch = zoom, two-finger pan = scroll.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }
    }
}
