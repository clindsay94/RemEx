package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import com.clindsay94.remex.ui.components.hapticCommandSent
import com.clindsay94.remex.ui.components.hapticCommandAcknowledged
import com.clindsay94.remex.ui.components.hapticCommandFailed
import androidx.annotation.StringRes
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior

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
        val category: CommandCategory,
        /**
         * Optional short consequence shown only while the card is awaiting confirmation, for commands
         * whose effect is not obvious. Note the confirm face REPLACES the card title with a generic
         * "Confirm choice", so this text is the only place the action's effect appears — state the
         * consequence first. Keep it to a sentence or two: these cards sit in a two-column grid, so
         * roughly half the screen width, and every extra line pushes the buttons further down a card
         * the grid does not scroll into view (RemEx-tgl1).
         *
         * `requiresConfirmation` must be true for this to ever render.
         */
        @param:StringRes val warningRes: Int? = null,
        /**
         * Whether the host honours a delay for this action. False hides the timer field in confirm
         * mode, because offering one the host ignores is worse than offering none: the command would
         * run immediately and still report success.
         *
         * SignOut is the case that matters — PingPongHandler's SIGNOUT branch never reads
         * CommandParameters, and ISystemCommandService.SignOut() takes no delay, so nothing could
         * honour it. Sleep/Hibernate/MonitorOff are equally delay-less but never show the field
         * because they do not require confirmation.
         */
        val supportsDelay: Boolean = true
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
                // Signing out closes every open program on the PC and discards unsaved work, exactly
                // like Shutdown - so it confirms, like Shutdown. It previously did not, because this
                // flag had been set from the CATEGORY rather than from destructiveness, and Sign Out
                // sits in SESSION beside Wake and Lock, which are both harmless. Category stays
                // SESSION (that grouping is correct); only the confirmation changes. (RemEx-awks.)
                RemoteCommandCard(
                        "logoff",
                        R.string.rc_logoff,
                        "SignOut",
                        Icons.AutoMirrored.Filled.Logout,
                        true,
                        CommandCategory.SESSION,
                        // The consequence a phone user cannot see: the PC stays ON but becomes
                        // unreachable, because remex.agent lives in the signed-in session and is
                        // started by a per-user logon task. Whoever taps this is by definition not
                        // sitting at the PC, so they need telling BEFORE, not after.
                        warningRes = R.string.rc_logoff_warning,
                        // The host signs out immediately and cannot delay it, so do not offer a timer
                        // the command will ignore while still reporting success. Only surfaced at all
                        // because requiresConfirmation now shows the confirm face for this card.
                        supportsDelay = false
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
    val commandStatus by viewModel.commandStatus.collectAsStateWithLifecycle()
    val shapePreset by viewModel.remoteControlCardShapePreset.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()

    val uiState =
            RemoteControlUiState(
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

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
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
    val snackbarHostState = remember { SnackbarHostState() }

    LaunchedEffect(uiState.commandStatus) {
        if (!uiState.commandStatus.isNullOrBlank()) {
            snackbarHostState.showSnackbar(
                    message = uiState.commandStatus,
                    duration = SnackbarDuration.Short
            )
            onClearCommandStatus()
        }
    }

    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_remote_control_title),
                        scrollBehavior = scrollBehavior
                )
            },
            snackbarHost = { SnackbarHost(snackbarHostState) }
    ) { innerPadding ->
        val cardsByCategory = remember { remoteCommandCards.groupBy { it.category } }
        val view = LocalView.current

      Box(modifier = Modifier.fillMaxSize().padding(innerPadding)) {
        LazyVerticalGrid(
                columns = GridCells.Fixed(2),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxSize(),
                // Extra bottom inset so the floating toolbar never covers the last row.
                contentPadding = PaddingValues(start = 16.dp, top = 16.dp, end = 16.dp, bottom = 104.dp)
        ) {
            item(span = { GridItemSpan(2) }) {
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    Text(
                            text = stringResource(R.string.remote_control_section_header),
                            style = MaterialTheme.typography.headlineSmallEmphasized
                    )

                    Text(
                            text = stringResource(R.string.remote_control_description),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

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
                items(categoryCards, key = { it.id }) { cmdCard ->
                    CommandCard(
                            card = cmdCard,
                            isAwaitingConfirmation = activeConfirmationId == cmdCard.id,
                            timerText = timerInputs[cmdCard.id].orEmpty(),
                            shape =
                                    com.clindsay94.remex.ui.theme.cardShape(
                                            uiState.shapePreset,
                                            uiState.cornerRadius
                                    ),
                            onTimerTextChanged = { timerInputs[cmdCard.id] = it },
                            onPrimaryClick = {
                                if (cmdCard.action == "WakeOnLan") {
                                    onWakePc()
                                } else if (cmdCard.requiresConfirmation) {
                                    activeConfirmationId =
                                            if (activeConfirmationId == cmdCard.id) null
                                            else cmdCard.id
                                } else {
                                    onSendSystemCommand(cmdCard.action, 0)
                                }
                            },
                            onConfirm = {
                                val delay =
                                        timerInputs[cmdCard.id]
                                                .orEmpty()
                                                .trim()
                                                .toIntOrNull()
                                                ?.coerceAtLeast(0)
                                                ?: 0
                                onSendSystemCommand(cmdCard.action, delay)
                                activeConfirmationId = null
                            },
                            onCancel = {
                                activeConfirmationId = null
                                timerInputs[cmdCard.id] = ""
                            },
                            modifier = Modifier.animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec())
                    )
                }
            }
        }

        // M3 Expressive: floating quick-actions for the most-used safe commands.
        HorizontalFloatingToolbar(
            expanded = true,
            modifier = Modifier.align(Alignment.BottomCenter)
                .navigationBarsPadding()
                .padding(bottom = 16.dp)
        ) {
            FilledTonalIconButton(onClick = {
                view.hapticCommandSent()
                onWakePc()
            }) {
                Icon(Icons.Default.Sensors, contentDescription = stringResource(R.string.rc_wake_pc))
            }
            IconButton(onClick = {
                view.hapticCommandSent()
                onSendSystemCommand("Lock", 0)
            }) {
                Icon(Icons.Default.Lock, contentDescription = stringResource(R.string.rc_lock_pc))
            }
            IconButton(onClick = {
                view.hapticCommandSent()
                onSendSystemCommand("Sleep", 0)
            }) {
                Icon(Icons.Default.Bedtime, contentDescription = stringResource(R.string.rc_sleep))
            }
        }
      }
    }
}

@Preview(showBackground = true)
@Composable
private fun RemoteControlScreenPreview() {
    RemExTheme {
        RemoteControlScreenContent(
            uiState = RemoteControlUiState(
                commandStatus = null,
                shapePreset = 1f,
                cornerRadius = 12,
                isConnected = true
            ),
            onNavigateToConnection = {},
            onWakePc = {},
            onSendSystemCommand = { _, _ -> },
            onClearCommandStatus = {}
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun CommandCardPreview() {
    RemExTheme {
        Row(modifier = Modifier.padding(16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            CommandCard(
                card = remoteCommandCards.first(),
                isAwaitingConfirmation = false,
                timerText = "",
                shape = com.clindsay94.remex.ui.theme.cardShape(0f, 8),
                onTimerTextChanged = {},
                onPrimaryClick = {},
                onConfirm = {},
                onCancel = {}
            )
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
                CommandCategory.SESSION -> MaterialTheme.colorScheme.primaryContainer
                CommandCategory.POWER -> MaterialTheme.colorScheme.errorContainer
                CommandCategory.ENERGY -> MaterialTheme.colorScheme.tertiaryContainer
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
                    style = MaterialTheme.typography.titleSmallEmphasized,
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
        onCancel: () -> Unit,
        modifier: Modifier = Modifier
) {
    val view = LocalView.current
    val localizedTitle = stringResource(card.titleRes)
    val motionScheme = MaterialTheme.motionScheme

    Card(
            // M3: animateContentSize replaces fixed height toggle for organic transitions
            modifier =
                    modifier.fillMaxWidth()
                            .animateContentSize(
                                    animationSpec =
                                            MaterialTheme.motionScheme.fastSpatialSpec()
                            ),
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
            AnimatedContent(
                    targetState = isAwaitingConfirmation,
                    transitionSpec = {
                        val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                        fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                    },
                    label = "commandCardConfirmMode"
            ) { awaitingConfirmation ->
                Column(
                        verticalArrangement = Arrangement.spacedBy(10.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                            text =
                                    if (awaitingConfirmation)
                                            stringResource(R.string.remote_control_confirm_choice)
                                    else localizedTitle,
                            style = MaterialTheme.typography.titleSmallEmphasized
                    )

                    if (awaitingConfirmation) {
                        // Consequence line, only for commands that declare one. Placed above the
                        // timer field and the buttons so it is read before the destructive action is
                        // reachable, not after it. Theme role rather than a literal colour, so it
                        // survives monochrome and contrast 1.0. (RemEx-awks.)
                        card.warningRes?.let { warningRes ->
                            Text(
                                    text = stringResource(warningRes),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    textAlign = TextAlign.Center
                            )
                        }

                        if (card.supportsDelay) {
                            OutlinedTextField(
                                    value = timerText,
                                    onValueChange = { value ->
                                        onTimerTextChanged(value.filter(Char::isDigit).take(6))
                                    },
                                    label = {
                                        Text(stringResource(R.string.remote_control_timer_label))
                                    },
                                    singleLine = true,
                                    modifier = Modifier.fillMaxWidth()
                            )
                        }

                        Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            // M3: error colors for destructive confirmation button
                            Button(
                                    onClick = {
                                        view.hapticCommandAcknowledged()
                                        onConfirm()
                                    },
                                    colors =
                                            ButtonDefaults.buttonColors(
                                                    containerColor = MaterialTheme.colorScheme.error,
                                                    contentColor = MaterialTheme.colorScheme.onError
                                            ),
                                    modifier = Modifier.weight(1f)
                            ) { Text(stringResource(R.string.button_confirm)) }
                            TextButton(
                                    onClick = {
                                        view.hapticCommandFailed()
                                        onCancel()
                                    },
                                    modifier = Modifier.weight(1f)
                            ) { Text(stringResource(R.string.button_cancel)) }
                        }
                    } else {
                        // M3: FilledTonalButton for lower-emphasis non-destructive actions
                        FilledTonalButton(
                                onClick = {
                                    view.hapticCommandSent()
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
    }
}
