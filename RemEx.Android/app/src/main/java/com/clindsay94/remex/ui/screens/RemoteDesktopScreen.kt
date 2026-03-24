package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteDesktopScreen(
    viewModel: RemoteDesktopViewModel = viewModel(),
    onMenuClick: () -> Unit = {}
) {
    val currentFrame by viewModel.currentFrame.collectAsState()
    val isStreaming by viewModel.isStreaming.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Remote Desktop", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = onMenuClick) {
                        Icon(Icons.Default.Menu, contentDescription = "Menu")
                    }
                },
                actions = {
                    if (isStreaming) {
                        IconButton(onClick = { viewModel.stopStreaming() }) {
                            Icon(Icons.Default.Stop, contentDescription = "Stop", tint = MaterialTheme.colorScheme.error)
                        }
                    } else {
                        IconButton(onClick = { viewModel.startStreaming() }) {
                            Icon(Icons.Default.PlayArrow, contentDescription = "Start", tint = Color(0xFF4CAF50))
                        }
                    }
                }
            )
        }
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .background(Color.Black),
            contentAlignment = Alignment.Center
        ) {
            if (currentFrame != null) {
                Image(
                    bitmap = currentFrame!!.asImageBitmap(),
                    contentDescription = "Remote Desktop Frame",
                    modifier = Modifier.fillMaxSize(),
                    contentScale = ContentScale.Fit
                )
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
                        text = if (isStreaming) "Waiting for frames..." else "Stream Stopped",
                        color = Color.Gray
                    )
                    if (!isStreaming) {
                        Button(onClick = { viewModel.startStreaming() }, modifier = Modifier.padding(16.dp)) {
                            Text("Start Streaming")
                        }
                    }
                }
            }
        }
    }
}
