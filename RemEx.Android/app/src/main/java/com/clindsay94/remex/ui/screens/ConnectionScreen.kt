package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreen(
    viewModel: ConnectionViewModel = viewModel(),
    onMenuClick: () -> Unit = {}
) {
    val connectionPrefs by viewModel.connectionPreferences.collectAsState()
    val desktopPrefs by viewModel.remoteDesktopPreferences.collectAsState()
    val isConnecting by viewModel.isConnecting.collectAsState()
    val status by viewModel.connectionStatus.collectAsState()
    val capabilitySummary by viewModel.capabilitySummary.collectAsState()

    var hostInput by remember { mutableStateOf("") }
    var portInput by remember { mutableStateOf("") }
    var macInput by remember { mutableStateOf("") }
    var broadcastInput by remember { mutableStateOf("") }
    var subnetInput by remember { mutableStateOf("") }
    var qualityInput by remember { mutableFloatStateOf(50f) }
    var targetFpsInput by remember { mutableFloatStateOf(30f) }
    var scaleInput by remember { mutableFloatStateOf(0.6f) }

    // Initialize inputs from saved values
    LaunchedEffect(connectionPrefs, desktopPrefs) {
        if (hostInput.isEmpty() && connectionPrefs.host.isNotEmpty()) hostInput = connectionPrefs.host
        if (portInput.isEmpty()) portInput = connectionPrefs.port.toString()
        if (macInput.isEmpty()) macInput = connectionPrefs.macAddress
        if (broadcastInput.isEmpty()) broadcastInput = connectionPrefs.broadcastIp
        if (subnetInput.isEmpty()) subnetInput = connectionPrefs.subnetMask
        qualityInput = desktopPrefs.quality.toFloat()
        targetFpsInput = desktopPrefs.targetFps.toFloat()
        scaleInput = desktopPrefs.scale
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Connection", fontWeight = FontWeight.Bold) },
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
                .padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Icon(
                imageVector = Icons.Default.SettingsEthernet,
                contentDescription = null,
                modifier = Modifier.size(64.dp),
                tint = MaterialTheme.colorScheme.primary
            )

            Text(
                text = "Host Settings",
                style = MaterialTheme.typography.headlineMedium,
                fontWeight = FontWeight.Bold
            )

            OutlinedTextField(
                value = hostInput,
                onValueChange = { hostInput = it },
                label = { Text("Host IP Address") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                leadingIcon = { Icon(Icons.Default.Dns, contentDescription = null) }
            )

            OutlinedTextField(
                value = portInput,
                onValueChange = { portInput = it },
                label = { Text("Port") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                leadingIcon = { Icon(Icons.Default.Numbers, contentDescription = null) }
            )

            OutlinedTextField(
                value = macInput,
                onValueChange = { macInput = it },
                label = { Text("MAC Address (Wake-on-LAN)") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                leadingIcon = { Icon(Icons.Default.Memory, contentDescription = null) }
            )

            OutlinedTextField(
                value = broadcastInput,
                onValueChange = { broadcastInput = it },
                label = { Text("Broadcast IP") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                leadingIcon = { Icon(Icons.Default.Router, contentDescription = null) }
            )

            OutlinedTextField(
                value = subnetInput,
                onValueChange = { subnetInput = it },
                label = { Text("Subnet Mask") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                leadingIcon = { Icon(Icons.Default.Lan, contentDescription = null) }
            )

            Card(modifier = Modifier.fillMaxWidth()) {
                Column(
                    modifier = Modifier.padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text(
                        text = "Remote Desktop Defaults",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold
                    )

                    Text("Quality: ${qualityInput.toInt()}%")
                    Slider(
                        value = qualityInput,
                        onValueChange = { qualityInput = it },
                        valueRange = 1f..100f
                    )

                    Text("Target FPS: ${targetFpsInput.toInt()} (max 120)")
                    Slider(
                        value = targetFpsInput,
                        onValueChange = { targetFpsInput = it },
                        valueRange = 1f..120f
                    )

                    Text("Scale: ${"%.2f".format(scaleInput)}")
                    Slider(
                        value = scaleInput,
                        onValueChange = { scaleInput = it },
                        valueRange = 0.25f..1.0f
                    )
                }
            }

            Spacer(modifier = Modifier.height(8.dp))

            Button(
                onClick = {
                    val p = portInput.toIntOrNull() ?: 5005
                    viewModel.connect(
                        newHost = hostInput.trim(),
                        newPort = p,
                        macAddress = macInput.trim(),
                        broadcastIp = broadcastInput.trim().ifEmpty { "255.255.255.255" },
                        subnetMask = subnetInput.trim().ifEmpty { "255.255.255.0" },
                        desktopQuality = qualityInput.toInt(),
                        desktopTargetFps = targetFpsInput.toInt().coerceIn(1, 120),
                        desktopScale = scaleInput
                    )
                },
                modifier = Modifier.fillMaxWidth(),
                enabled = !isConnecting && hostInput.isNotEmpty()
            ) {
                if (isConnecting) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(24.dp),
                        color = MaterialTheme.colorScheme.onPrimary,
                        strokeWidth = 2.dp
                    )
                } else {
                    Text("Save & Connect")
                }
            }

            Text(
                text = "Status: $status",
                style = MaterialTheme.typography.bodyMedium,
                color = if (status == "Connected") MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
            )

            Text(
                text = capabilitySummary,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}
