package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager

private enum class CommandCategory(@param:StringRes val labelRes: Int) {
    SESSION(R.string.rc_category_session),
    POWER(R.string.rc_category_power),
    ENERGY(R.string.rc_category_energy)
}

private data class RemoteCommandCard(
        val id: String,
        @param:StringRes val titleRes: Int,
        val action: String,
        val icon: ImageVector,
        val requiresConfirmation: Boolean,
        val category: CommandCategory
)

private val remoteCommandCards =
        listOf(
                RemoteCommandCard(
                        "wake",
                        R.string.rc_wake_pc,
                        "WakeOnLan",
                        Icons.Default.Sensors,
                        false,
                        CommandCategory.SESSION
                ),
                RemoteCommandCard(
                        "lock",
                        R.string.rc_lock_pc,
                        "Lock",
                        Icons.Default.Lock,
                        false,
                        CommandCategory.SESSION
                ),
                RemoteCommandCard(
                        "logoff",
                        R.string.rc_logoff,
                        "SignOut",
                        Icons.AutoMirrored.Filled.Logout,
                        false,
                        CommandCategory.SESSION
                ),
                RemoteCommandCard(
                        "shutdown",
                        R.string.rc_shutdown,
                        "Shutdown",
                        Icons.Default.PowerSettingsNew,
                        true,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "force_shutdown",
                        R.string.rc_force_shutdown,
                        "ForceShutdown",
                        Icons.Default.PowerOff,
                        true,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "restart",
                        R.string.rc_restart,
                        "Restart",
                        Icons.Default.RestartAlt,
                        true,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "force_restart",
                        R.string.rc_force_restart,
                        "ForceRestart",
                        Icons.Default.Warning,
                        true,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "uefi",
                        R.string.rc_reboot_uefi,
                        "RestartToUefi",
                        Icons.Default.Refresh,
                        true,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "sleep",
                        R.string.rc_sleep,
                        "Sleep",
                        Icons.Default.Bedtime,
                        false,
                        CommandCategory.ENERGY
                ),
                RemoteCommandCard(
                        "hibernate",
                        R.string.rc_hibernate,
                        "Hibernate",
                        Icons.Default.Bedtime,
                        false,
                        CommandCategory.ENERGY
                ),
                RemoteCommandCard(
                        "monitor_off",
                        R.string.rc_monitor_off,
                        "MonitorOff",
                        Icons.Default.Monitor,
                        false,
                        CommandCategory.ENERGY
                )
        )

data class RemoteControlUiState(
    val commandStatus: String? = null,
    val shapePreset: Float = 0f,
    val cornerRadius: Int = 8,
    val isConnected: Boolean = false
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

    val uiState = RemoteControlUiState(
        commandStatus = commandStatus,
        shapePreset = shapePreset,
        cornerRadius = cornerRadius,
        isConnected = isConnected
    )

    RemoteControlScreenContent(
        uiState = uiState,
        onNavigateToConnection = onNavigateToConnection,
        onWakePc = { viewModel.wakePc() },
        onSendSystemCommand = { action, delay -> viewModel.sendSystemCommand(action, delay) },
        onClearCommandStatus = { viewModel.clearCommandStatus() }
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteControlScreenContent(
    uiState: RemoteControlUiState,
    onNavigateToConnection: () -> Unit,
    onWakePc: () -> Unit,
    onSendSystemCommand: (String, Int) -> Unit,
    onClearCommandStatus: () -> Unit
) {
    var activeConfirmationId by remember { mutableStateOf<String?>(null) }
    val timerInputs = remember { mutableStateMapOf<String, String>() }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.screen_remote_control_title)) }
            )
        }
    ) { innerPadding ->
        val cardsByCategory = remember { remoteCommandCards.groupBy { it.category } }

        LazyVerticalGrid(
                columns = GridCells.Fixed(2),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxSize().padding(innerPadding),
                contentPadding = PaddingValues(16.dp)
        ) {
            item(span = { GridItemSpan(2) }) {
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
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

                    if (!uiState.commandStatus.isNullOrBlank()) {
                        Card(
                            colors = CardDefaults.cardColors(
                                containerColor = MaterialTheme.colorScheme.secondaryContainer,
                                contentColor = MaterialTheme.colorScheme.onSecondaryContainer
                            ),
                            modifier = Modifier.fillMaxWidth(),
                            shape = com.clindsay94.remex.ui.theme.cardShape(uiState.shapePreset, uiState.cornerRadius)
                        ) {
                            Row(
                                modifier = Modifier.fillMaxWidth().padding(12.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                Text(
                                    text = uiState.commandStatus,
                                    modifier = Modifier.weight(1f),
                                    style = MaterialTheme.typography.bodySmall
                                )
                                TextButton(onClick = { onClearCommandStatus() }) {
                                    Text(stringResource(R.string.button_dismiss))
                                }
                            }
                        }
                    }
                    
                    Spacer(modifier = Modifier.height(8.dp))
                }
            }

            CommandCategory.entries.forEach { category ->
                val categoryCards = cardsByCategory[category].orEmpty()
                item(span = { GridItemSpan(2) }) {
                    CommandCategoryHeader(
                        label = stringResource(category.labelRes),
                        category = category
                    )
                }
                items(categoryCards) { cmdCard ->
                    CommandCard(
                        card = cmdCard,
                        isAwaitingConfirmation = activeConfirmationId == cmdCard.id,
                        timerText = timerInputs[cmdCard.id].orEmpty(),
                        shape = com.clindsay94.remex.ui.theme.cardShape(uiState.shapePreset, uiState.cornerRadius),
                        onTimerTextChanged = { timerInputs[cmdCard.id] = it },
                        onPrimaryClick = {
                            if (cmdCard.action == "WakeOnLan") {
                                onWakePc()
                            } else if (cmdCard.requiresConfirmation) {
                                activeConfirmationId = if (activeConfirmationId == cmdCard.id) null else cmdCard.id
                            } else {
                                onSendSystemCommand(cmdCard.action, 0)
                            }
                        },
                        onConfirm = {
                            val delay = timerInputs[cmdCard.id].orEmpty().trim().toIntOrNull()?.coerceAtLeast(0) ?: 0
                            onSendSystemCommand(cmdCard.action, delay)
                            activeConfirmationId = null
                        },
                        onCancel = {
                            activeConfirmationId = null
                            timerInputs[cmdCard.id] = ""
                        }
                    )
                }
            }
        }
    }
}

@Composable
private fun CommandCategoryHeader(label: String, category: CommandCategory) {
    val icon =
            when (category) {
                CommandCategory.SESSION -> Icons.Default.Sensors
                CommandCategory.POWER -> Icons.Default.PowerSettingsNew
                CommandCategory.ENERGY -> Icons.Default.Bedtime
            }

    val backgroundColor =
            when (category) {
                CommandCategory.SESSION ->
                        MaterialTheme.colorScheme.primaryContainer
                CommandCategory.POWER ->
                        MaterialTheme.colorScheme.errorContainer
                CommandCategory.ENERGY ->
                        MaterialTheme.colorScheme.tertiaryContainer
            }

    val contentColor =
            when (category) {
                CommandCategory.SESSION -> MaterialTheme.colorScheme.onPrimaryContainer
                CommandCategory.POWER -> MaterialTheme.colorScheme.onErrorContainer
                CommandCategory.ENERGY -> MaterialTheme.colorScheme.onTertiaryContainer
            }

    Surface(
            modifier =
                    Modifier.fillMaxWidth()
                            .padding(
                                    top = if (category == CommandCategory.SESSION) 0.dp else 16.dp
                            ),
            color = backgroundColor,
            shape = MaterialTheme.shapes.small,
            tonalElevation = 2.dp
    ) {
        Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Icon(
                    imageVector = icon,
                    contentDescription = stringResource(R.string.cd_category_icon),
                    tint = contentColor,
                    modifier = Modifier.size(20.dp)
            )
            Text(
                    text = label,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold,
                    color = contentColor
            )
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
    val view = LocalView.current
    val localizedTitle = stringResource(card.titleRes)

    Card(
            modifier =
                    Modifier.fillMaxWidth().height(if (isAwaitingConfirmation) 220.dp else 140.dp),
            shape = shape,
            colors =
                    CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceVariant
                    )
    ) {
        Column(
                modifier = Modifier.fillMaxSize().padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Icon(
                    imageVector = card.icon,
                    contentDescription = localizedTitle,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Text(
                    text =
                            if (isAwaitingConfirmation)
                                    stringResource(R.string.remote_control_confirm_choice)
                            else localizedTitle,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold
            )

            if (isAwaitingConfirmation) {
                OutlinedTextField(
                        value = timerText,
                        onValueChange = { value ->
                            onTimerTextChanged(value.filter(Char::isDigit).take(6))
                        },
                        label = { Text(stringResource(R.string.remote_control_timer_label)) },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                )

                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                onConfirm()
                            },
                            modifier = Modifier.weight(1f)
                    ) { Text(stringResource(R.string.button_confirm)) }
                    TextButton(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.REJECT)
                                onCancel()
                            },
                            modifier = Modifier.weight(1f)
                    ) { Text(stringResource(R.string.button_cancel)) }
                }
            } else {
                Button(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onPrimaryClick()
                        },
                        modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                            if (card.requiresConfirmation) stringResource(R.string.button_select)
                            else stringResource(R.string.button_run)
                    )
                }
            }
        }
    }
}
