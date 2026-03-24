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
    val host by viewModel.host.collectAsState()
    val port by viewModel.port.collectAsState()
    val isConnecting by viewModel.isConnecting.collectAsState()
    val status by viewModel.connectionStatus.collectAsState()
    val capabilitySummary by viewModel.capabilitySummary.collectAsState()

    var hostInput by remember { mutableStateOf("") }
    var portInput by remember { mutableStateOf("") }

    // Initialize inputs from saved values
    LaunchedEffect(host, port) {
        if (hostInput.isEmpty() && host.isNotEmpty()) hostInput = host
        if (portInput.isEmpty()) portInput = port.toString()
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

            Spacer(modifier = Modifier.height(8.dp))

            Button(
                onClick = {
                    val p = portInput.toIntOrNull() ?: 5005
                    viewModel.connect(hostInput, p)
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
                    Text("Connect")
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
