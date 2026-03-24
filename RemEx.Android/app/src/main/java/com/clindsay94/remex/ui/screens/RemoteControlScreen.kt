package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteControlScreen(
    viewModel: RemoteControlViewModel = viewModel(),
    onMenuClick: () -> Unit = {}
) {
    val focusRequester = remember { FocusRequester() }
    val keyboardController = LocalSoftwareKeyboardController.current
    var textValue by remember { mutableStateOf(TextFieldValue("")) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Remote Control", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = onMenuClick) {
                        Icon(Icons.Default.Menu, contentDescription = "Menu")
                    }
                }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            // Invisible TextField to capture keyboard input
            BasicTextField(
                value = textValue,
                onValueChange = {
                    if (it.text.length > textValue.text.length) {
                        val newChar = it.text.last().toString()
                        viewModel.sendText(newChar)
                    }
                    textValue = it
                },
                modifier = Modifier
                    .size(0.dp)
                    .focusRequester(focusRequester)
            )

            // Trackpad Area
            Surface(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .pointerInput(Unit) {
                        detectDragGestures { change, dragAmount ->
                            change.consume()
                            viewModel.sendMouseMove(dragAmount.x.toInt(), dragAmount.y.toInt())
                        }
                    },
                shape = MaterialTheme.shapes.large,
                color = MaterialTheme.colorScheme.surfaceVariant,
                tonalElevation = 4.dp
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                            imageVector = Icons.Default.Mouse,
                            contentDescription = null,
                            modifier = Modifier.size(48.dp),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
                        )
                        Text(
                            "Trackpad",
                            style = MaterialTheme.typography.bodyLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
                        )
                    }
                }
            }

            // Mouse Buttons
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(16.dp)
            ) {
                Button(
                    onClick = { viewModel.sendMouseClick(1) }, // Left Click
                    modifier = Modifier.weight(1f).height(80.dp),
                    shape = MaterialTheme.shapes.medium,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.secondaryContainer, contentColor = MaterialTheme.colorScheme.onSecondaryContainer)
                ) {
                    Text("Left Click", fontWeight = FontWeight.Bold)
                }
                Button(
                    onClick = { viewModel.sendMouseClick(2) }, // Right Click
                    modifier = Modifier.weight(1f).height(80.dp),
                    shape = MaterialTheme.shapes.medium,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.secondaryContainer, contentColor = MaterialTheme.colorScheme.onSecondaryContainer)
                ) {
                    Text("Right Click", fontWeight = FontWeight.Bold)
                }
            }

            // Extra Controls
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceEvenly
            ) {
                IconButton(onClick = { viewModel.sendScroll(-100) }) {
                    Icon(Icons.Default.KeyboardDoubleArrowUp, contentDescription = "Scroll Up")
                }
                IconButton(onClick = { viewModel.sendScroll(100) }) {
                    Icon(Icons.Default.KeyboardDoubleArrowDown, contentDescription = "Scroll Down")
                }
                IconButton(onClick = { 
                    focusRequester.requestFocus()
                    keyboardController?.show()
                }) {
                    Icon(Icons.Default.Keyboard, contentDescription = "Keyboard")
                }
            }
        }
    }
}
