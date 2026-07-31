package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.ui.platform.LocalView
import androidx.compose.foundation.gestures.detectDragGestures
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
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardDoubleArrowDown
import androidx.compose.material.icons.filled.KeyboardDoubleArrowUp
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.animation.animateColorAsState
import androidx.compose.runtime.Composable
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.theme.cardShape

@OptIn(ExperimentalMaterial3Api::class, androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemoteMouseScreen(
        onNavigateToConnection: () -> Unit = {},
        viewModel: RemoteControlViewModel = viewModel()
) {
    val shapePreset by viewModel.remoteMouseCardShapePreset.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val vScrollSensitivity by viewModel.verticalScrollSensitivity.collectAsStateWithLifecycle()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()

    RemoteMouseScreenContent(
            isConnected = isConnected,
            shapePreset = shapePreset,
            cornerRadius = cornerRadius,
            vScrollSensitivity = vScrollSensitivity,
            onNavigateToConnection = onNavigateToConnection,
            onMouseMove = { x, y -> viewModel.sendMouseMove(x, y) },
            onMouseMoveEnd = { viewModel.flushPendingMouseMove() },
            onMouseClick = { button -> viewModel.sendMouseClick(button) },
            onScroll = { amount -> viewModel.sendScroll(amount) },
            onTextSent = { text -> viewModel.sendText(text) },
            onSendKeyPress = { keyCode -> viewModel.sendKeyPress(keyCode) }
    )
}

@Composable
fun RemoteMouseScreenContent(
        isConnected: Boolean,
        shapePreset: Float,
        cornerRadius: Int,
        vScrollSensitivity: Float,
        onNavigateToConnection: () -> Unit,
        onMouseMove: (Float, Float) -> Unit,
        onMouseMoveEnd: () -> Unit,
        onMouseClick: (Int) -> Unit,
        onScroll: (Int) -> Unit,
        onTextSent: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit
) {
    val view = LocalView.current
    val focusRequester = remember { FocusRequester() }
    var textValue by remember { mutableStateOf(TextFieldValue("")) }

    Column(modifier = Modifier.fillMaxSize()) {
        RemexFlexibleTopBar(title = stringResource(R.string.screen_remote_mouse_title))
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
                        onValueChange = { newValue ->
                            textValue =
                                    applyRemoteKeyboardEdit(
                                            currentValue = textValue,
                                            newValue = newValue,
                                            onSendText = onTextSent,
                                            onSendKeyPress = onSendKeyPress
                                    )
                        },
                        modifier =
                                Modifier.size(1.dp)
                                        .graphicsLayer { alpha = 0f }
                                        .focusRequester(focusRequester)
                )

                // Touch-down feedback: a pressed flag from onPress (which does not consume or
                // alter tap/drag semantics) drives an animated tonal shift (RemEx-uba6).
                var trackpadPressed by remember { mutableStateOf(false) }
                val trackpadColor by animateColorAsState(
                        targetValue =
                                if (trackpadPressed)
                                        MaterialTheme.colorScheme.surfaceContainerHighest
                                else MaterialTheme.colorScheme.surfaceVariant,
                        animationSpec = MaterialTheme.motionScheme.fastEffectsSpec(),
                        label = "trackpad_pressed"
                )
                Surface(
                        modifier =
                                Modifier.weight(1f)
                                        .fillMaxWidth()
                                        .pointerInput(Unit) {
                                            // Raw floats, NOT dragAmount.x.toInt(): truncating
                                            // each frame independently threw away any drag slower
                                            // than a pixel per frame, which at 120 Hz is most
                                            // careful pointing. The view model accumulates and
                                            // throttles instead (RemEx-3uhp).
                                            detectDragGestures(
                                                    onDragEnd = { onMouseMoveEnd() },
                                                    onDragCancel = { onMouseMoveEnd() }
                                            ) { change, dragAmount ->
                                                change.consume()
                                                onMouseMove(dragAmount.x, dragAmount.y)
                                            }
                                        }
                                        .pointerInput(Unit) {
                                            detectTapGestures(
                                                    onPress = {
                                                        trackpadPressed = true
                                                        try {
                                                            tryAwaitRelease()
                                                        } finally {
                                                            trackpadPressed = false
                                                        }
                                                    },
                                                    onTap = {
                                                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                                        onMouseClick(MouseButtons.LEFT)
                                                    }
                                            )
                                        },
                        shape = cardShape(shapePreset, cornerRadius),
                        color = trackpadColor,
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
                                onMouseClick(MouseButtons.LEFT)
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
                                style = MaterialTheme.typography.labelLargeEmphasized
                        )
                    }
                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                onMouseClick(MouseButtons.RIGHT)
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
                                style = MaterialTheme.typography.labelLargeEmphasized
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

@Preview(showBackground = true)
@Composable
private fun RemoteMouseScreenPreview() {
    RemExTheme {
        RemoteMouseScreenContent(
            isConnected = true,
            shapePreset = 0f,
            cornerRadius = 12,
            vScrollSensitivity = 1.0f,
            onNavigateToConnection = {},
            onMouseMove = { _, _ -> },
            onMouseMoveEnd = {},
            onMouseClick = {},
            onScroll = {},
            onTextSent = {},
            onSendKeyPress = {}
        )
    }
}


