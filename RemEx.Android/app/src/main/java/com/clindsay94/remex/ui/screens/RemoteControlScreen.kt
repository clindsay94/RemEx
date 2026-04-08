package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Monitor
import androidx.compose.material.icons.filled.PowerOff
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager

private data class RemoteCommandCard(
    val id: String,
    val title: String,
    val action: String,
    val icon: ImageVector,
    val requiresConfirmation: Boolean
)

private val remoteCommandCards = listOf(
    RemoteCommandCard("wake", "Wake PC", "WakeOnLan", Icons.Default.Sensors, false),
    RemoteCommandCard("lock", "Lock PC", "Lock", Icons.Default.Lock, false),
    RemoteCommandCard("shutdown", "Shutdown", "Shutdown", Icons.Default.PowerSettingsNew, true),
    RemoteCommandCard("restart", "Restart", "Restart", Icons.Default.RestartAlt, true),
    RemoteCommandCard("uefi", "Reboot to UEFI", "RestartToUefi", Icons.Default.Refresh, true),
    RemoteCommandCard("force_shutdown", "Force Shutdown", "ForceShutdown", Icons.Default.PowerOff, true),
    RemoteCommandCard("force_restart", "Force Restart", "ForceRestart", Icons.Default.Sensors, true),
    RemoteCommandCard("sleep", "Sleep", "Sleep", Icons.Default.Bedtime, false),
    RemoteCommandCard("hibernate", "Hibernate", "Hibernate", Icons.Default.Bedtime, false),
    RemoteCommandCard("monitor_off", "Turn Off Monitor", "MonitorOff", Icons.Default.Monitor, false),
    RemoteCommandCard("logoff", "Log Off", "SignOut", Icons.AutoMirrored.Filled.Logout, false)
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteControlScreen(
    onNavigateToConnection: () -> Unit = {},
    viewModel: RemoteControlViewModel = viewModel()
) {
    val commandStatus by viewModel.commandStatus.collectAsState()
    val shapePreset by viewModel.remoteControlCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val isConnected by RemexClientManager.isConnected.collectAsState()
    var activeConfirmationId by remember { mutableStateOf<String?>(null) }
    val timerInputs = remember { mutableStateMapOf<String, String>() }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.screen_remote_control_title), fontWeight = FontWeight.Bold) }
            )
        }
    ) { paddingValues ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues),
            verticalArrangement = Arrangement.spacedBy(0.dp)
        ) {
            NotConnectedBanner(
                isConnected = isConnected,
                onNavigateToConnection = onNavigateToConnection
            )

            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp)
            ) {
            Text(
                text = stringResource(R.string.remote_control_section_header),
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold
            )

            Text(
                text = stringResource(R.string.remote_control_description),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            if (!commandStatus.isNullOrBlank()) {
                Card(
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.secondaryContainer,
                        contentColor = MaterialTheme.colorScheme.onSecondaryContainer
                    ),
                    modifier = Modifier.fillMaxWidth(),
                    shape = com.clindsay94.remex.ui.theme.cardShape(shapePreset, cornerRadius)
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(
                            text = commandStatus.orEmpty(),
                            modifier = Modifier.weight(1f),
                            style = MaterialTheme.typography.bodySmall
                        )
                        TextButton(onClick = { viewModel.clearCommandStatus() }) {
                            Text(stringResource(R.string.button_dismiss))
                        }
                    }
                }
            }

            LazyVerticalGrid(
                columns = GridCells.Fixed(2),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                items(remoteCommandCards) { card ->
                    CommandCard(
                        card = card,
                        isAwaitingConfirmation = activeConfirmationId == card.id,
                        timerText = timerInputs[card.id].orEmpty(),
                        shape = com.clindsay94.remex.ui.theme.cardShape(shapePreset, cornerRadius),
                        onTimerTextChanged = { timerInputs[card.id] = it },
                        onPrimaryClick = {
                            if (card.action == "WakeOnLan") {
                                viewModel.wakePc()
                            } else if (card.requiresConfirmation) {
                                activeConfirmationId = if (activeConfirmationId == card.id) null else card.id
                            } else {
                                viewModel.sendSystemCommand(card.action)
                            }
                        },
                        onConfirm = {
                            val delay = timerInputs[card.id].orEmpty().trim().toIntOrNull()?.coerceAtLeast(0) ?: 0
                            viewModel.sendSystemCommand(card.action, delay)
                            activeConfirmationId = null
                        },
                        onCancel = {
                            activeConfirmationId = null
                            timerInputs[card.id] = ""
                        }
                    )
                }
            }
            } // end inner Column
        }
    }
}

@Composable
private fun CommandCard(
    card: RemoteCommandCard,
    isAwaitingConfirmation: Boolean,
    timerText: String,
    shape: androidx.compose.ui.graphics.Shape,
    onTimerTextChanged: (String) -> Unit,
    onPrimaryClick: () -> Unit,
    onConfirm: () -> Unit,
    onCancel: () -> Unit
) {
    val localizedTitle = when (card.id) {
        "wake" -> stringResource(R.string.rc_wake_pc)
        "lock" -> stringResource(R.string.rc_lock_pc)
        "shutdown" -> stringResource(R.string.rc_shutdown)
        "restart" -> stringResource(R.string.rc_restart)
        "uefi" -> stringResource(R.string.rc_reboot_uefi)
        "force_shutdown" -> stringResource(R.string.rc_force_shutdown)
        "force_restart" -> stringResource(R.string.rc_force_restart)
        "sleep" -> stringResource(R.string.rc_sleep)
        "hibernate" -> stringResource(R.string.rc_hibernate)
        "monitor_off" -> stringResource(R.string.rc_monitor_off)
        "logoff" -> stringResource(R.string.rc_logoff)
        else -> card.title
    }

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .height(if (isAwaitingConfirmation) 220.dp else 140.dp),
        shape = shape,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Icon(
                imageVector = card.icon,
                contentDescription = localizedTitle,
                tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Text(
                text = if (isAwaitingConfirmation) stringResource(R.string.remote_control_confirm_choice) else localizedTitle,
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold
            )

            if (isAwaitingConfirmation) {
                OutlinedTextField(
                    value = timerText,
                    onValueChange = { value -> onTimerTextChanged(value.filter(Char::isDigit).take(6)) },
                    label = { Text(stringResource(R.string.remote_control_timer_label)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Button(onClick = onConfirm, modifier = Modifier.weight(1f)) {
                        Text(stringResource(R.string.button_confirm))
                    }
                    TextButton(onClick = onCancel, modifier = Modifier.weight(1f)) {
                        Text(stringResource(R.string.button_cancel))
                    }
                }
            } else {
                Button(onClick = onPrimaryClick, modifier = Modifier.fillMaxWidth()) {
                    Text(if (card.requiresConfirmation) stringResource(R.string.button_select) else stringResource(R.string.button_run))
                }
            }
        }
    }
}
