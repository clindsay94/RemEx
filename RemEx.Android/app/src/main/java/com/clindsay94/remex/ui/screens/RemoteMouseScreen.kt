package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.ui.platform.LocalView
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardDoubleArrowDown
import androidx.compose.material.icons.filled.KeyboardDoubleArrowUp
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material3.*
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexScreenHeader
import com.clindsay94.remex.ui.theme.cardShape
import kotlin.math.sqrt
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteMouseScreen(
        onNavigateToConnection: () -> Unit = {},
        viewModel: RemoteControlViewModel = viewModel()
) {
    val shapePreset by viewModel.remoteMouseCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val vScrollSensitivity by viewModel.verticalScrollSensitivity.collectAsState()
    val isConnected by RemexClientManager.isConnected.collectAsState()

    val scrollBehavior = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()

    Scaffold(
        modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
        topBar = {
            RemexScreenHeader(
                title = stringResource(R.string.screen_remote_mouse_title),
                scrollBehavior = scrollBehavior
            )
        }
    ) { innerPadding ->
        RemoteMouseScreenContent(
                isConnected = isConnected,
                shapePreset = shapePreset,
                cornerRadius = cornerRadius,
                vScrollSensitivity = vScrollSensitivity,
                onNavigateToConnection = onNavigateToConnection,
                onMouseMove = { x, y -> viewModel.sendMouseMove(x, y) },
                onMouseClick = { button -> viewModel.sendMouseClick(button) },
                onScroll = { amount -> viewModel.sendScroll(amount) },
                onTextSent = { text -> viewModel.sendText(text) },
                modifier = Modifier.padding(innerPadding)
        )
    }
}

@Composable
fun RemoteMouseScreenContent(
        isConnected: Boolean,
        shapePreset: Float,
        cornerRadius: Int,
        vScrollSensitivity: Float,
        onNavigateToConnection: () -> Unit,
        onMouseMove: (Int, Int) -> Unit,
        onMouseClick: (Int) -> Unit,
        onScroll: (Int) -> Unit,
        onTextSent: (String) -> Unit,
        modifier: Modifier = Modifier
) {
    val view = LocalView.current
    val focusRequester = remember { FocusRequester() }
    var textValue by remember { mutableStateOf(TextFieldValue("")) }

    Column(modifier = modifier.fillMaxSize()) {
        Column(
                modifier = Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.spacedBy(0.dp)
        ) {
            NotConnectedBanner(
                    isConnected = isConnected,
                    onNavigateToConnection = onNavigateToConnection
            )

            Column(
                    modifier = Modifier.fillMaxSize().padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp)
            ) {
                BasicTextField(
                        value = textValue,
                        onValueChange = {
                            if (it.text.length > textValue.text.length) {
                                val newChar = it.text.last().toString()
                                onTextSent(newChar)
                            }
                            textValue = it
                        },
                        modifier =
                                Modifier.size(1.dp)
                                        .graphicsLayer { alpha = 0f }
                                        .focusRequester(focusRequester)
                )

                Surface(
                        modifier =
                                Modifier.weight(1f)
                                        .fillMaxWidth()
                                        .pointerInput(Unit) {
                                            detectDragGestures { change, dragAmount ->
                                                change.consume()
                                                onMouseMove(
                                                        dragAmount.x.toInt(),
                                                        dragAmount.y.toInt()
                                                )
                                            }
                                        }
                                        .pointerInput(Unit) {
                                            detectTapGestures(
                                                    onTap = {
                                                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                                        onMouseClick(1)
                                                    }
                                            )
                                        },
                        shape = cardShape(shapePreset, cornerRadius),
                        color = MaterialTheme.colorScheme.surfaceVariant,
                        tonalElevation = 4.dp
                ) {
                    Box(contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(
                                    imageVector = Icons.Default.Mouse,
                                    contentDescription = null,
                                    modifier = Modifier.size(48.dp),
                                    tint =
                                            MaterialTheme.colorScheme.onSurfaceVariant.copy(
                                                    alpha = 0.5f
                                            )
                            )
                            Text(
                                    stringResource(R.string.remote_mouse_trackpad_label),
                                    style = MaterialTheme.typography.bodyLarge,
                                    color =
                                            MaterialTheme.colorScheme.onSurfaceVariant.copy(
                                                    alpha = 0.5f
                                            )
                            )
                        }
                    }
                }

                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(16.dp)
                ) {
                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                onMouseClick(1)
                            },
                            modifier = Modifier.weight(1f).height(80.dp),
                            shape = MaterialTheme.shapes.medium,
                            colors =
                                    ButtonDefaults.buttonColors(
                                            containerColor =
                                                    MaterialTheme.colorScheme.secondaryContainer,
                                            contentColor =
                                                    MaterialTheme.colorScheme.onSecondaryContainer
                                    )
                    ) {
                        Text(
                                stringResource(R.string.remote_mouse_left_click),
                                fontWeight = FontWeight.Bold
                        )
                    }
                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                onMouseClick(2)
                            },
                            modifier = Modifier.weight(1f).height(80.dp),
                            shape = MaterialTheme.shapes.medium,
                            colors =
                                    ButtonDefaults.buttonColors(
                                            containerColor =
                                                    MaterialTheme.colorScheme.secondaryContainer,
                                            contentColor =
                                                    MaterialTheme.colorScheme.onSecondaryContainer
                                    )
                    ) {
                        Text(
                                stringResource(R.string.remote_mouse_right_click),
                                fontWeight = FontWeight.Bold
                        )
                    }
                }

                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceEvenly
                ) {
                    IconButton(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                onScroll((-100 * vScrollSensitivity).toInt())
                            }
                    ) {
                        Icon(
                                Icons.Default.KeyboardDoubleArrowUp,
                                contentDescription = stringResource(R.string.cd_scroll_up)
                        )
                    }
                    IconButton(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                onScroll((100 * vScrollSensitivity).toInt())
                            }
                    ) {
                        Icon(
                                Icons.Default.KeyboardDoubleArrowDown,
                                contentDescription = stringResource(R.string.cd_scroll_down)
                        )
                    }
                    IconButton(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                focusRequester.requestFocus()
                            }
                    ) {
                        Icon(
                                Icons.Default.Keyboard,
                                contentDescription = stringResource(R.string.cd_keyboard)
                        )
                    }
                }
            } // end inner Column
        }
    }
}

@Composable
fun FloatingMouseIsland(
        viewModel: RemoteControlViewModel,
        shapePreset: Float,
        cornerRadius: Int,
        onDismiss: () -> Unit,
        modifier: Modifier = Modifier,
        onDrag: ((dragAmount: Offset) -> Unit)? = null,
        onDragStart: (() -> Unit)? = null,
        onDragEnd: (() -> Unit)? = null
) {
    val vScrollSensitivity by viewModel.verticalScrollSensitivity.collectAsState()

    FloatingMouseIslandContent(
            shapePreset = shapePreset,
            cornerRadius = cornerRadius,
            vScrollSensitivity = vScrollSensitivity,
            onDismiss = onDismiss,
            onMouseMove = { x, y -> viewModel.sendMouseMove(x, y) },
            onMouseClick = { button -> viewModel.sendMouseClick(button) },
            onScroll = { amount -> viewModel.sendScroll(amount) },
            onTextSent = { text -> viewModel.sendText(text) },
            modifier = modifier,
            onDrag = onDrag,
            onDragStart = onDragStart,
            onDragEnd = onDragEnd
    )
}

@Composable
fun FloatingMouseIslandContent(
        shapePreset: Float,
        cornerRadius: Int,
        vScrollSensitivity: Float,
        onDismiss: () -> Unit,
        onMouseMove: (Float, Float) -> Unit,
        onMouseClick: (Int) -> Unit,
        onScroll: (Int) -> Unit,
        onTextSent: (String) -> Unit,
        modifier: Modifier = Modifier,
        onDrag: ((dragAmount: Offset) -> Unit)? = null,
        onDragStart: (() -> Unit)? = null,
        onDragEnd: (() -> Unit)? = null
) {
    val view = LocalView.current
    val focusRequester = remember { FocusRequester() }
    var textValue by remember { mutableStateOf(TextFieldValue("")) }
    val scope = rememberCoroutineScope()
    var inertiaJob by remember { mutableStateOf<Job?>(null) }

    BasicTextField(
            value = textValue,
            onValueChange = {
                if (it.text.length > textValue.text.length) {
                    val addedText = it.text.substring(textValue.text.length)
                    onTextSent(addedText)
                }
                textValue = it
            },
            modifier =
                    Modifier.size(1.dp).graphicsLayer { alpha = 0f }.focusRequester(focusRequester)
    )

    Card(
            modifier = modifier.width(300.dp),
            shape = cardShape(shapePreset, cornerRadius),
            elevation = CardDefaults.cardElevation(defaultElevation = 8.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
    ) {
        Column(
                modifier = Modifier.padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Row(
                    modifier =
                            Modifier.fillMaxWidth()
                                    .then(
                                            if (onDrag != null) {
                                                Modifier.pointerInput(Unit) {
                                                    detectDragGesturesAfterLongPress(
                                                            onDragStart = { onDragStart?.invoke() },
                                                            onDrag = { _, dragAmount ->
                                                                onDrag.invoke(dragAmount)
                                                            },
                                                            onDragEnd = { onDragEnd?.invoke() }
                                                    )
                                                }
                                            } else Modifier
                                    ),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                        text = stringResource(R.string.screen_remote_mouse_title),
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.Bold
                )
                IconButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onDismiss()
                        },
                        modifier = Modifier.size(28.dp)
                ) {
                    Icon(
                            Icons.Default.Close,
                            contentDescription = stringResource(android.R.string.cancel),
                            modifier = Modifier.size(18.dp)
                    )
                }
            }

            Surface(
                    modifier =
                            Modifier.fillMaxWidth()
                                    .height(180.dp)
                                    .pointerInput(Unit) {
                                        val recentDeltas = ArrayDeque<Offset>(4)
                                        detectDragGestures(
                                                onDragStart = {
                                                    inertiaJob?.cancel()
                                                    inertiaJob = null
                                                    recentDeltas.clear()
                                                },
                                                onDrag = { change, dragAmount ->
                                                    change.consume()
                                                    onMouseMove(
                                                            dragAmount.x,
                                                            dragAmount.y
                                                    )
                                                    recentDeltas.addLast(
                                                            Offset(dragAmount.x, dragAmount.y)
                                                    )
                                                    if (recentDeltas.size > 3)
                                                            recentDeltas.removeFirst()
                                                },
                                                onDragEnd = {
                                                    if (recentDeltas.isNotEmpty()) {
                                                        var avgX = 0f
                                                        var avgY = 0f
                                                        recentDeltas.forEach {
                                                            avgX += it.x
                                                            avgY += it.y
                                                        }
                                                        avgX /= recentDeltas.size
                                                        avgY /= recentDeltas.size
                                                        val velocity =
                                                                sqrt(avgX * avgX + avgY * avgY)
                                                        if (velocity > 2f) {
                                                            inertiaJob =
                                                                    scope.launch {
                                                                        var vx = avgX
                                                                        var vy = avgY
                                                                        while (sqrt(
                                                                                        vx * vx +
                                                                                                vy * vy
                                                                                ) > 0.5f
                                                                        ) {
                                                                            onMouseMove(
                                                                                    vx,
                                                                                    vy
                                                                            )
                                                                            vx *= 0.93f
                                                                            vy *= 0.93f
                                                                            delay(16L)
                                                                        }
                                                                    }
                                                        }
                                                    }
                                                }
                                        )
                                    }
                                    .pointerInput(Unit) {
                                        detectTapGestures(
                                                onTap = {
                                                    inertiaJob?.cancel()
                                                    inertiaJob = null
                                                    view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                                    onMouseClick(1)
                                                }
                                        )
                                    },
                    shape = cardShape(shapePreset, cornerRadius),
                    color = MaterialTheme.colorScheme.surfaceVariant,
                    tonalElevation = 4.dp
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                                imageVector = Icons.Default.Mouse,
                                contentDescription = null,
                                modifier = Modifier.size(32.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
                        )
                        Text(
                                stringResource(R.string.remote_mouse_trackpad_label),
                                style = MaterialTheme.typography.bodySmall,
                                color =
                                        MaterialTheme.colorScheme.onSurfaceVariant.copy(
                                                alpha = 0.4f
                                        )
                        )
                    }
                }
            }

            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Button(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onMouseClick(1)
                        },
                        modifier = Modifier.weight(1f).height(52.dp),
                        shape = MaterialTheme.shapes.medium,
                        colors =
                                ButtonDefaults.buttonColors(
                                        containerColor =
                                                MaterialTheme.colorScheme.secondaryContainer,
                                        contentColor =
                                                MaterialTheme.colorScheme.onSecondaryContainer
                                )
                ) {
                    Text(
                            stringResource(R.string.remote_mouse_left_click),
                            style = MaterialTheme.typography.labelMedium,
                            fontWeight = FontWeight.Bold
                    )
                }
                Button(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onMouseClick(2)
                        },
                        modifier = Modifier.weight(1f).height(52.dp),
                        shape = MaterialTheme.shapes.medium,
                        colors =
                                ButtonDefaults.buttonColors(
                                        containerColor =
                                                MaterialTheme.colorScheme.secondaryContainer,
                                        contentColor =
                                                MaterialTheme.colorScheme.onSecondaryContainer
                                )
                ) {
                    Text(
                            stringResource(R.string.remote_mouse_right_click),
                            style = MaterialTheme.typography.labelMedium,
                            fontWeight = FontWeight.Bold
                    )
                }
            }

            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceEvenly
            ) {
                IconButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                            onScroll((-100 * vScrollSensitivity).toInt())
                        }
                ) {
                    Icon(
                            Icons.Default.KeyboardDoubleArrowUp,
                            contentDescription = stringResource(R.string.cd_scroll_up)
                    )
                }
                IconButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                            onScroll((100 * vScrollSensitivity).toInt())
                        }
                ) {
                    Icon(
                            Icons.Default.KeyboardDoubleArrowDown,
                            contentDescription = stringResource(R.string.cd_scroll_down)
                        )
                }
                IconButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            focusRequester.requestFocus()
                        }
                ) {
                    Icon(
                            Icons.Default.Keyboard,
                            contentDescription = stringResource(R.string.cd_keyboard)
                    )
                }
            }
        }
    }
}
