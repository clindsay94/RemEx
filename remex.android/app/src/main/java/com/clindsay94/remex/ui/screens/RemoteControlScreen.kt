package com.clindsay94.remex.ui.screens

import com.clindsay94.remex.ui.components.hapticCommandSent
import com.clindsay94.remex.ui.components.hapticCommandAcknowledged
import com.clindsay94.remex.ui.components.hapticCommandFailed
import androidx.annotation.StringRes
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.MediaControlSection
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior

private enum class CommandCategory(@param:StringRes val labelRes: Int) {
    SESSION(R.string.rc_category_session),
    POWER(R.string.rc_category_power),
    ENERGY(R.string.rc_category_energy)
}

/**
 * Whether an action discards the user's work, and therefore must be confirmed.
 *
 * This is the ONE place the question is answered. It used to be a hand-set Boolean on every card,
 * and the original values had been assigned by CATEGORY - every POWER card true, every SESSION and
 * ENERGY card false. Sign Out sits in SESSION beside Wake and Lock, so it inherited `false` by
 * association and shipped a destructive action with no prompt (RemEx-awks). Nothing in the type
 * stopped the twelfth card repeating that, because the property that actually matters - does this
 * close programs or lose unsaved work? - was never expressed, only proxied.
 *
 * The `else` branch confirms. A `when` over a wire string cannot be exhaustive, so the default has
 * to be the SAFE direction: an action nobody classified gets a prompt rather than running silently.
 * RemoteControlConfirmationTests then fails if any card actually relies on that default, so the
 * fallback is a safety net rather than a place for cards to quietly accumulate.
 */
private fun actionDiscardsWork(action: String): Boolean = when (action) {
    "SignOut",
    "Shutdown",
    "ForceShutdown",
    "Restart",
    "ForceRestart",
    "RestartToUefi" -> true

    // Reversible, and none of them closes a program or discards unsaved work. Wake and Lock change
    // nothing the user is holding; Sleep and Hibernate preserve session state by definition;
    // MonitorOff only blanks the display.
    "WakeOnLan",
    "Lock",
    "Sleep",
    "Hibernate",
    "MonitorOff" -> false

    else -> true
}

private data class RemoteCommandCard(
        val id: String,
        @param:StringRes val titleRes: Int,
        val action: String,
        val icon: ImageVector,
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
) {
    /**
     * Derived from [action], never hand-set. Reading it from one classifier is what stops a new
     * card inheriting the wrong answer by sitting next to the wrong neighbour.
     */
    val requiresConfirmation: Boolean
        get() = actionDiscardsWork(action)
}

/**
 * Height the floating quick-actions toolbar occludes at the bottom of the command grid.
 *
 * Shared by the grid's bottom [PaddingValues] and by the confirm-actions bring-into-view request
 * so the two cannot drift apart: a scroll that merely brings the Confirm/Cancel row's trailing
 * edge to the viewport bottom would park it *behind* the toolbar, which is the same
 * discoverability bug in a new place. (RemEx-tgl1.)
 */
private val FloatingToolbarOcclusion = 104.dp

private val remoteCommandCards =
        listOf(
                RemoteCommandCard(
                        "wake",
                        R.string.rc_wake_pc,
                        "WakeOnLan",
                        Icons.Default.Sensors,
                        CommandCategory.SESSION
                ),
                RemoteCommandCard(
                        "lock",
                        R.string.rc_lock_pc,
                        "Lock",
                        Icons.Default.Lock,
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
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "force_shutdown",
                        R.string.rc_force_shutdown,
                        "ForceShutdown",
                        Icons.Default.PowerOff,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "restart",
                        R.string.rc_restart,
                        "Restart",
                        Icons.Default.RestartAlt,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "force_restart",
                        R.string.rc_force_restart,
                        "ForceRestart",
                        Icons.Default.Warning,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "uefi",
                        R.string.rc_reboot_uefi,
                        "RestartToUefi",
                        Icons.Default.Refresh,
                        CommandCategory.POWER
                ),
                RemoteCommandCard(
                        "sleep",
                        R.string.rc_sleep,
                        "Sleep",
                        Icons.Default.Bedtime,
                        CommandCategory.ENERGY
                ),
                RemoteCommandCard(
                        "hibernate",
                        R.string.rc_hibernate,
                        "Hibernate",
                        Icons.Default.Bedtime,
                        CommandCategory.ENERGY
                ),
                RemoteCommandCard(
                        "monitor_off",
                        R.string.rc_monitor_off,
                        "MonitorOff",
                        Icons.Default.Monitor,
                        CommandCategory.ENERGY
                )
        )

data class RemoteControlUiState(
        val commandStatus: String? = null,
        val shapePreset: Float = 0f,
        val cornerRadius: Int = 8,
        val isConnected: Boolean = false,
        /**
         * Whether the host will act on key presses. ONE OF TWO gates on the media row - being
         * connected is the other, because the capability flow replays its last value and so
         * outlives the connection it described. Input travelling this path is silently dropped when
         * the capability is absent, with no error anywhere (RemEx-hulc).
         */
        val supportsInputSimulation: Boolean = false
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
    val supportsInputSimulation by
            viewModel.supportsInputSimulation.collectAsStateWithLifecycle()

    val uiState =
            RemoteControlUiState(
                    commandStatus = commandStatus,
                    shapePreset = shapePreset,
                    cornerRadius = cornerRadius,
                    isConnected = isConnected,
                    supportsInputSimulation = supportsInputSimulation
            )

    RemoteControlScreenContent(
            uiState = uiState,
            onNavigateToConnection = onNavigateToConnection,
            onWakePc = { viewModel.wakePc() },
            onSendSystemCommand = { action, delay -> viewModel.sendSystemCommand(action, delay) },
            onTakeScreenshot = { viewModel.takeScreenshot() },
            onSendKey = { virtualKey -> viewModel.sendKeyPress(virtualKey) },
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
        onTakeScreenshot: () -> Unit = {},
        onSendKey: (Int) -> Unit,
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
                // imePadding as well as the toolbar inset below: the delay field sits
                // mid-grid, so without it the keyboard covers the row being typed into
                // (RemEx-a9ci).
                modifier = Modifier.fillMaxSize().imePadding(),
                // Extra bottom inset so the floating toolbar never covers the last row.
                contentPadding =
                        PaddingValues(
                                start = 16.dp,
                                top = 16.dp,
                                end = 16.dp,
                                bottom = FloatingToolbarOcclusion
                        )
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

            // Media sits FIRST, above Session/Power/Energy. It is the only group here that is
            // reversible, repeatable and used casually - everything below it either interrupts the
            // session or shuts the machine down, and half of it shows a confirm face. Putting the
            // harmless controls where the thumb lands keeps the destructive ones further away.
            item(span = { GridItemSpan(2) }) {
                SectionHeader(
                        label = stringResource(R.string.rc_category_media),
                        icon = Icons.Default.MusicNote,
                        backgroundColor = MaterialTheme.colorScheme.secondaryContainer,
                        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                        topPadding = 0.dp
                )
            }
            item(span = { GridItemSpan(2) }) {
                MediaControlSection(
                        connected = uiState.isConnected,
                        inputSupported = uiState.supportsInputSimulation,
                        shape =
                                com.clindsay94.remex.ui.theme.cardShape(
                                        uiState.shapePreset,
                                        uiState.cornerRadius
                                ),
                        onSendKey = onSendKey
                )
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
            // Safe like its neighbours - nothing is lost or interrupted on the PC - so it belongs on
            // the quick-actions bar rather than behind a confirmation. ScreenshotMonitor, not
            // Screenshot: the plain glyph is a phone, and this captures the PC's screen.
            //
            // Its own callback rather than onSendSystemCommand("SCREENSHOT", 0), which would also
            // have worked: that path reports the response's message field verbatim, and for a command
            // dispatch that field is the native layer's untranslated "Command dispatched.". See
            // RemoteControlViewModel.takeScreenshot.
            IconButton(onClick = {
                view.hapticCommandSent()
                onTakeScreenshot()
            }) {
                Icon(
                        Icons.Default.ScreenshotMonitor,
                        contentDescription = stringResource(R.string.action_take_screenshot)
                )
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
                isConnected = true,
                // Otherwise the preview renders the greyed-out "not set up to accept key presses"
                // face, which is a real state but the least useful one to design against.
                supportsInputSimulation = true
            ),
            onNavigateToConnection = {},
            onWakePc = {},
            onSendSystemCommand = { _, _ -> },
            onSendKey = {},
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

    SectionHeader(
            label = label,
            icon = icon,
            backgroundColor = backgroundColor,
            contentColor = contentColor,
            // Uniform now that the media section precedes Session; the zero-padding special case
            // existed only because Session used to be the first band under the intro text.
            topPadding = 16.dp
    )
}

/**
 * The banded header used by every section of the command grid.
 *
 * Split out of [CommandCategoryHeader] so the media section (RemEx-hulc) can sit under the same
 * band without being forced into [CommandCategory] — media keys are not [RemoteCommandCard]s, they
 * never confirm and they take no delay, so joining that enum to borrow a header would have meant a
 * category the grid then has to special-case out of its own `forEach`.
 */
@Composable
private fun SectionHeader(
        label: String,
        icon: ImageVector,
        backgroundColor: androidx.compose.ui.graphics.Color,
        contentColor: androidx.compose.ui.graphics.Color,
        topPadding: androidx.compose.ui.unit.Dp
) {
    Surface(
            modifier = Modifier.fillMaxWidth().padding(top = topPadding),
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
                    contentDescription = null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
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

    // Bringing the Confirm/Cancel row into view when the card arms. See RemEx-tgl1: the card
    // grows downward on its confirm face and the LazyVerticalGrid does not follow, so on a card
    // in the last visible row the buttons can sit entirely off-screen with a destructive action
    // already armed.
    //
    // TIMING IS THE WHOLE PROBLEM, and the obvious version does nothing. ContentInViewNode
    // .bringChildIntoView opens with `if (localRect()?.isMaxVisible() != false) return` - it is
    // ONE-SHOT with no retry path. Request it from a LaunchedEffect when the confirm face enters
    // composition and the row has not been measured yet, so its rect degenerates to zero-height
    // at the card's top-left; the card's top is on screen by definition, so the request is judged
    // already-visible and silently dropped. It compiles, it lints, and it scrolls nothing.
    //
    // So the request is driven from two places that both run AFTER layout:
    //   - onSizeChanged, i.e. as soon as the row has real bounds, and
    //   - animateContentSize's finishedListener, i.e. once the growth settles.
    // The second is the one that must exist: while the card is still animating taller the grid's
    // scroll extent is still short, and an under-consumed scroll makes ContentInViewNode cancel
    // the request outright rather than resume it.
    val scope = rememberCoroutineScope()
    val confirmActionsRequester = remember { BringIntoViewRequester() }
    var confirmActionsSize by remember { mutableStateOf(IntSize.Zero) }
    val toolbarOcclusionPx = with(LocalDensity.current) { FloatingToolbarOcclusion.toPx() }

    suspend fun revealConfirmActions() {
        val size = confirmActionsSize
        if (size == IntSize.Zero) return
        // Explicit rect extended below the row: the default request stops as soon as the trailing
        // edge reaches the viewport bottom, which is exactly where the floating toolbar sits.
        confirmActionsRequester.bringIntoView(
                Rect(
                        left = 0f,
                        top = 0f,
                        right = size.width.toFloat(),
                        bottom = size.height.toFloat() + toolbarOcclusionPx
                )
        )
    }

    LaunchedEffect(isAwaitingConfirmation, confirmActionsSize) {
        if (isAwaitingConfirmation) revealConfirmActions()
    }

    Card(
            // M3: animateContentSize replaces fixed height toggle for organic transitions
            modifier =
                    modifier.fillMaxWidth()
                            .animateContentSize(
                                    animationSpec =
                                            MaterialTheme.motionScheme.fastSpatialSpec(),
                                    finishedListener = { _, _ ->
                                        if (isAwaitingConfirmation) {
                                            scope.launch { revealConfirmActions() }
                                        }
                                    }
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
                        // Tie the measured size to THIS confirm face, not to the card. Without
                        // the reset, a second arm of the same card starts with the stale size
                        // from the first: the IntSize.Zero guard no longer fires, so the request
                        // runs at composition time against an unplaced Row and scrolls to the
                        // wrong offset - and because onSizeChanged then writes the same value,
                        // mutableStateOf sees no change, the LaunchedEffect never re-keys, and
                        // the fast path silently disappears after the first arm.
                        DisposableEffect(Unit) { onDispose { confirmActionsSize = IntSize.Zero } }

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

                        // The requester is anchored to the BUTTON ROW rather than to the card,
                        // because the row is what actually has to be reachable. onSizeChanged is
                        // what makes the request fire against real bounds instead of the
                        // zero-height rect a composition-time request would produce.
                        //
                        // Deliberately NOT "fixed" by capping the consequence line with maxLines:
                        // truncating a safety warning is worse than making the user scroll.
                        Row(
                                modifier =
                                        Modifier.fillMaxWidth()
                                                .onSizeChanged { confirmActionsSize = it }
                                                .bringIntoViewRequester(confirmActionsRequester),
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
