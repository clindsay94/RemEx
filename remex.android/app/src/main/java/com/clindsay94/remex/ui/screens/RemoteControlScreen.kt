package com.clindsay94.remex.ui.screens

import androidx.annotation.StringRes
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import com.clindsay94.remex.ui.components.MediaMiniPlayer
import com.clindsay94.remex.ui.components.MediaNowPlayingSheet
import com.clindsay94.remex.ui.components.MediaVirtualKeys
import com.clindsay94.remex.ui.components.MiniPlayerHeight
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.floatingChromeBottomPadding
import com.clindsay94.remex.ui.components.navigationBarBottomInset
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import android.graphics.Bitmap
import androidx.compose.animation.core.animateDpAsState
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import androidx.compose.material3.ButtonDefaults

/**
 * Whether an action discards the user's work, and therefore must be confirmed.
 *
 * This is the ONE place the question is answered, for the Commands grid and for Home's Lock and Sleep
 * buttons alike (RemEx-wqo7a.6), which is why it is internal. It used to be a hand-set Boolean on every card,
 * and the original values had been assigned by CATEGORY - every POWER card true, every SESSION and
 * ENERGY card false. Sign Out sat in SESSION beside Wake and Lock, so it inherited `false` by
 * association and shipped a destructive action with no prompt (RemEx-awks). Nothing in the type
 * stopped the twelfth card repeating that, because the property that actually matters - does this
 * close programs or lose unsaved work? - was never expressed, only proxied. The groups changed again
 * in RemEx-kq10x.3 (Sign out now sits in Standard beside Sleep), which is exactly why the answer must
 * never be read off the group.
 *
 * The `else` branch confirms. A `when` over a wire string cannot be exhaustive, so the default has
 * to be the SAFE direction: an action nobody classified gets a prompt rather than running silently.
 * RemoteControlConfirmationTests then fails if any card actually relies on that default, so the
 * fallback is a safety net rather than a place for cards to quietly accumulate.
 */
internal fun actionDiscardsWork(action: String): Boolean = when (action) {
    "SignOut",
    "Shutdown",
    "ForceShutdown",
    "Restart",
    "ForceRestart",
    "RestartToUefi" -> true

    // Reversible, and none of them closes a program or discards unsaved work. Wake and Lock change
    // nothing the user is holding; Sleep and Hibernate preserve session state by definition;
    // MonitorOff only blanks the display. A screenshot only reads the PC's screen, and the clipboard
    // pair writes either the PC's clipboard or the phone's - the one thing a person restores by
    // copying again (RemEx-hgqs).
    "WakeOnLan",
    "Lock",
    "Sleep",
    "Hibernate",
    "MonitorOff",
    "Screenshot",
    "SendClipboard",
    "FetchClipboard" -> false

    else -> true
}

private data class RemoteCommandCard(
        val id: String,
        @param:StringRes val titleRes: Int,
        val action: String,
        val icon: ImageVector,
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
         * Whether the host honours a delay for this action. False hides the wait field in confirm
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
 * How much of the bottom of the screen the docked [MediaMiniPlayer] occludes right now, animated as
 * it appears and disappears above the nav bar.
 *
 * The grid's bottom [PaddingValues] and the RemEx-tgl1 bring-into-view maths both read this one
 * value, so a track starting or stopping mid-session can never leave one of the two out of step with
 * the other. The floating quick-actions toolbar that used to sit above the mini-player is gone
 * (RemEx-kq10x.3): every one of its actions is a card in the grid now, so only the bar remains.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun rememberBottomChromeOcclusion(miniPlayerShown: Boolean): Dp {
    val target = if (miniPlayerShown) MiniPlayerHeight else 0.dp
    val animated by
            animateDpAsState(
                    targetValue = target,
                    animationSpec = MaterialTheme.motionScheme.defaultSpatialSpec(),
                    label = "bottomChromeOcclusion"
            )
    // Plus the nav-bar inset: the mini-player sits on navigationBarsPadding(), so it rises by that
    // inset too. Without it the last tile stayed half-covered on three-button navigation
    // (RemEx-wqo7a.1). The default 16dp gap is the breathing room under the last row.
    return floatingChromeBottomPadding(
            floatingFootprint = animated,
            navBarInset = navigationBarBottomInset()
    )
}

/**
 * Every card the screen can draw, keyed by action. The ORDER and grouping are not here: they live in
 * [CommandsLayout.groups], which the screen walks, so this list only says what each action looks like.
 */
private val remoteCommandCards =
        listOf(
                RemoteCommandCard(
                        "wake",
                        R.string.rc_wake_pc,
                        "WakeOnLan",
                        Icons.Default.Sensors
                ),
                RemoteCommandCard(
                        "lock",
                        R.string.rc_lock_pc,
                        "Lock",
                        Icons.Default.Lock
                ),
                // Signing out closes every open program on the PC and discards unsaved work, exactly
                // like Shutdown - so it confirms, like Shutdown. (RemEx-awks.)
                RemoteCommandCard(
                        "logoff",
                        R.string.rc_logoff,
                        "SignOut",
                        Icons.AutoMirrored.Filled.Logout,
                        // The consequence a phone user cannot see: the PC stays ON but becomes
                        // unreachable, because remex.agent lives in the signed-in session and is
                        // started by a per-user logon task. Whoever taps this is by definition not
                        // sitting at the PC, so they need telling BEFORE, not after.
                        warningRes = R.string.rc_logoff_warning,
                        // The host signs out immediately and cannot delay it, so do not offer a wait
                        // the command will ignore while still reporting success.
                        supportsDelay = false
                ),
                RemoteCommandCard(
                        "shutdown",
                        R.string.rc_shutdown,
                        "Shutdown",
                        Icons.Default.PowerSettingsNew
                ),
                RemoteCommandCard(
                        "restart",
                        R.string.rc_restart,
                        "Restart",
                        Icons.Default.RestartAlt
                ),
                RemoteCommandCard(
                        "sleep",
                        R.string.rc_sleep,
                        "Sleep",
                        Icons.Default.Bedtime
                ),
                RemoteCommandCard(
                        "hibernate",
                        R.string.rc_hibernate,
                        "Hibernate",
                        Icons.Default.Bedtime
                ),
                RemoteCommandCard(
                        "force_shutdown",
                        R.string.rc_force_shutdown,
                        "ForceShutdown",
                        Icons.Default.PowerOff
                ),
                RemoteCommandCard(
                        "force_restart",
                        R.string.rc_force_restart,
                        "ForceRestart",
                        Icons.Default.Warning
                ),
                RemoteCommandCard(
                        "uefi",
                        R.string.rc_reboot_uefi,
                        "RestartToUefi",
                        Icons.Default.Refresh
                ),
                RemoteCommandCard(
                        "monitor_off",
                        R.string.rc_monitor_off,
                        "MonitorOff",
                        Icons.Default.Monitor
                ),
                // ScreenshotMonitor, not Screenshot: the plain glyph is a phone, and this captures
                // the PC's screen. Its own callback rather than onSendSystemCommand("SCREENSHOT", 0):
                // that path reports the response's message field verbatim, and for a command dispatch
                // that field is the native layer's untranslated "Command dispatched.". See
                // RemoteControlViewModel.takeScreenshot.
                RemoteCommandCard(
                        "screenshot",
                        R.string.action_take_screenshot,
                        "Screenshot",
                        Icons.Default.ScreenshotMonitor
                ),
                // THE CLIPBOARD PAIR IS CHOSEN TOGETHER (RemEx-hgqs). Direction is the ONLY thing
                // distinguishing these two, so the glyphs must carry it - but Upload/Download already
                // mean FILE TRANSFER in this app (FileManagerToolbar, FileManagerQueuePanel,
                // FileTransferScreen). ContentPasteGo / ContentPaste keeps the clipboard metaphor and
                // puts the direction on top of it. The refusals send can produce - nothing copied, too
                // large - are decided on the phone before anything is sent.
                RemoteCommandCard(
                        "clipboard_send",
                        R.string.clipboard_send_button,
                        "SendClipboard",
                        Icons.Default.ContentPasteGo
                ),
                RemoteCommandCard(
                        "clipboard_fetch",
                        R.string.clipboard_fetch_button,
                        "FetchClipboard",
                        Icons.Default.ContentPaste
                )
        )

private val remoteCommandCardsByAction = remoteCommandCards.associateBy { it.action }

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
        val supportsInputSimulation: Boolean = false,
        /**
         * What the PC reports it is playing (RemEx-xx6xf). Drives the play/pause face and the
         * now-playing line (RemEx-nmvz6) — it is NOT a third gate on the row, because not knowing
         * what is playing is no reason to refuse to send a key.
         *
         * THE WHOLE SNAPSHOT, NOT JUST THE STATUS. This carried only `playbackStatus` while the face
         * was the only consumer, and the track metadata sat parsed and unread one layer below —
         * which is precisely how a wire field that nothing renders stays that way.
         */
        val playback: MediaPlaybackSnapshot = MediaPlaybackSnapshot.Unknown,
        /** The bitmap for [playback]'s current `artworkId`, or null. Drives [MediaMiniPlayer]/[MediaNowPlayingSheet]. */
        val artwork: Bitmap? = null,
        /**
         * The power actions the PC says it can do, or null when it does not say. Actions it leaves
         * out are hidden; null shows every action (RemEx-kq10x.3, [CommandsLayout.isOffered]).
         */
        val powerVerbs: List<String>? = null
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteControlScreen(
        onNavigateToConnection: () -> Unit = {},
        viewModel: RemoteControlViewModel = viewModel(),
        headerAccessory: @Composable () -> Unit = {},
) {
    val commandStatus by viewModel.commandStatus.collectAsStateWithLifecycle()
    val shapePreset by viewModel.remoteControlCardShapePreset.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val supportsInputSimulation by
            viewModel.supportsInputSimulation.collectAsStateWithLifecycle()
    val powerVerbs by viewModel.powerVerbs.collectAsStateWithLifecycle()
    // Straight off the manager, like isConnected above rather than through the view model: it is a
    // snapshot the manager already resets on connect and disconnect, and a pass-through flow would be
    // a second place for that lifetime rule to drift out of step (RemEx-xx6xf).
    val mediaState by RemexClientManager.mediaState.collectAsStateWithLifecycle()
    // Same lifetime rule as mediaState above: RemexClientManager resets this on disconnect, so a
    // pass-through view-model flow would be a second place for that reset to drift out of step.
    val mediaArtwork by RemexClientManager.mediaArtwork.collectAsStateWithLifecycle()

    val uiState =
            RemoteControlUiState(
                    commandStatus = commandStatus,
                    shapePreset = shapePreset,
                    cornerRadius = cornerRadius,
                    isConnected = isConnected,
                    supportsInputSimulation = supportsInputSimulation,
                    playback = mediaState,
                    artwork = mediaArtwork,
                    powerVerbs = powerVerbs
            )

    // "Your routines" (routines spec A14, R-UX-06): the connected PC's Tap Run routines.
    val routines = com.clindsay94.remex.ui.routines.rememberRemoteControlRoutines()
    val routineContext = androidx.compose.ui.platform.LocalContext.current
    val routineScope = rememberCoroutineScope()

    RemoteControlScreenContent(
            uiState = uiState,
            routines = routines,
            onRunRoutine = { id ->
                routineScope.launch { com.clindsay94.remex.ui.routines.runRoutineFromRemoteControl(routineContext, id) }
            },
            onNavigateToConnection = onNavigateToConnection,
            onWakePc = { viewModel.wakePc() },
            onSendSystemCommand = { action, delay -> viewModel.sendSystemCommand(action, delay) },
            onTakeScreenshot = { viewModel.takeScreenshot() },
            onSendClipboard = { viewModel.sendClipboardToPc() },
            onFetchClipboard = { viewModel.fetchClipboardFromPc() },
            onSendKey = { virtualKey -> viewModel.sendKeyPress(virtualKey) },
            onSeek = RemexClientManager::seekMedia,
            onClearCommandStatus = { viewModel.clearCommandStatus() },
            headerAccessory = headerAccessory
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
        onSendClipboard: () -> Unit = {},
        onFetchClipboard: () -> Unit = {},
        onSendKey: (Int) -> Unit,
        onSeek: (Long) -> Unit = {},
        onClearCommandStatus: () -> Unit,
        routines: List<com.clindsay94.remex.ui.routines.RemoteRoutine> = emptyList(),
        onRunRoutine: (String) -> Unit = {},
        /**
         * Shown directly under the header, inside the top bar slot so the content pads below it:
         * the Control tab's Commands | Processes switch (RemEx-wqo7a.2). Empty by default.
         */
        headerAccessory: @Composable () -> Unit = {}
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
                Column {
                    RemexFlexibleTopBar(
                            title = stringResource(R.string.screen_remote_control_title),
                            subtitle = stringResource(R.string.screen_remote_control_subtitle),
                            scrollBehavior = scrollBehavior
                    )
                    headerAccessory()
                }
            },
            snackbarHost = { SnackbarHost(snackbarHostState) }
    ) { innerPadding ->
        val visibleGroups = remember(uiState.powerVerbs) { CommandsLayout.visibleGroups(uiState.powerVerbs) }
        // The last shared group still on screen. A PC that can do none of the Forced actions drops
        // that group, and the routines must not vanish with it; Wake is always shown.
        val routinesFollow =
                visibleGroups.lastOrNull { (group, _) -> group == CommandGroup.FORCED || group == CommandGroup.STANDARD }
                        ?.first ?: CommandGroup.WAKE
        val haptics = rememberRemexHaptics()

        // UNKNOWN has no reading to dock a bar about (RemEx-nmvz6's reasoning applied to layout):
        // the whole stacking/occlusion story below is driven off this one flag rather than
        // repeating the status check at each of its use sites. NONE stays shown - it is the bar's
        // only route to MediaNowPlayingSheet's volume/transport controls when nothing is playing
        // (RemEx-vtorl.5 review round 2 - reverted the NONE hide).
        val miniPlayerShown = uiState.playback.status != MediaPlaybackStatus.UNKNOWN
        val bottomOcclusion = rememberBottomChromeOcclusion(miniPlayerShown)
        var sheetOpen by remember { mutableStateOf(false) }

      Box(modifier = Modifier.fillMaxSize().padding(innerPadding)) {
        LazyVerticalGrid(
                columns = GridCells.Fixed(2),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                // imePadding as well as the bottom inset below: the wait field sits mid-grid, so
                // without it the keyboard covers the row being typed into (RemEx-a9ci).
                modifier = Modifier.fillMaxSize().imePadding(),
                // Extra bottom inset so the docked mini-player never covers the last row.
                contentPadding =
                        PaddingValues(
                                start = 16.dp,
                                top = 16.dp,
                                end = 16.dp,
                                bottom = bottomOcclusion
                        )
        ) {
            item(span = { GridItemSpan(2) }) {
                Text(
                        text = stringResource(R.string.remote_control_description),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            // Media moved out of the grid onto the docked mini-player (RemEx-vtorl.5): it was the
            // only reversible, casually-used group here, which is exactly why it now lives where
            // the thumb always lands rather than scrolling away with the rest of the grid.

            visibleGroups.forEach { (group, actions) ->
                group.labelRes?.let { labelRes ->
                    item(span = { GridItemSpan(2) }, key = "group-" + group.name) {
                        CommandSectionLabel(stringResource(labelRes))
                    }
                }
                val groupCards = actions.mapNotNull { remoteCommandCardsByAction[it] }
                items(
                        groupCards,
                        key = { it.id },
                        // Wake is alone at the top and the most likely first tap, so it spans the row.
                        span = { GridItemSpan(if (group == CommandGroup.WAKE) 2 else 1) }
                ) { cmdCard ->
                    CommandCard(
                            card = cmdCard,
                            forced = group == CommandGroup.FORCED,
                            isAwaitingConfirmation = activeConfirmationId == cmdCard.id,
                            timerText = timerInputs[cmdCard.id].orEmpty(),
                            bottomOcclusion = bottomOcclusion,
                            onTimerTextChanged = { timerInputs[cmdCard.id] = it },
                            onPrimaryClick = {
                                when {
                                    cmdCard.action == "WakeOnLan" -> onWakePc()
                                    cmdCard.action == "Screenshot" -> onTakeScreenshot()
                                    cmdCard.action == "SendClipboard" -> onSendClipboard()
                                    cmdCard.action == "FetchClipboard" -> onFetchClipboard()
                                    cmdCard.requiresConfirmation ->
                                            activeConfirmationId =
                                                    if (activeConfirmationId == cmdCard.id) null
                                                    else cmdCard.id
                                    else -> onSendSystemCommand(cmdCard.action, 0)
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

                // "Your routines" (spec A14) follows the two groups the PC page shares, ahead of the
                // phone-only extras, and is absent when the connected PC has no Tap Run routines.
                if (group == routinesFollow && routines.isNotEmpty()) {
                    item(span = { GridItemSpan(2) }, key = "routines-header") {
                        CommandSectionLabel(stringResource(R.string.rc_section_your_routines))
                    }
                    items(routines, key = { "routine-" + it.id }) { routine ->
                        val confirmId = "routine-" + routine.id
                        com.clindsay94.remex.ui.routines.RemoteRoutineCard(
                                routine = routine,
                                awaitingConfirmation = activeConfirmationId == confirmId,
                                shape = MaterialTheme.shapes.large,
                                onPrimaryClick = {
                                    haptics.perform(RemexHapticEvent.CommandSent)
                                    if (routine.destructive) {
                                        activeConfirmationId = if (activeConfirmationId == confirmId) null else confirmId
                                    } else {
                                        onRunRoutine(routine.id)
                                    }
                                },
                                onConfirm = {
                                    haptics.perform(RemexHapticEvent.Confirm)
                                    activeConfirmationId = null
                                    onRunRoutine(routine.id)
                                },
                                onCancel = {
                                    haptics.perform(RemexHapticEvent.Reject)
                                    activeConfirmationId = null
                                },
                                modifier = Modifier.animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec())
                        )
                    }
                }
            }
        }

        // Docked directly on the nav bar (spec 4.1). AnimatedVisibility inside MediaMiniPlayer
        // itself handles the UNKNOWN-status show/hide, so this call is unconditional.
        MediaMiniPlayer(
                playback = uiState.playback,
                artwork = uiState.artwork,
                onOpen = { sheetOpen = true },
                onPlayPause = { onSendKey(MediaVirtualKeys.MEDIA_PLAY_PAUSE) },
                modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding()
        )
      }

        if (sheetOpen) {
            MediaNowPlayingSheet(
                    connected = uiState.isConnected,
                    inputSupported = uiState.supportsInputSimulation,
                    playback = uiState.playback,
                    artwork = uiState.artwork,
                    shape =
                            com.clindsay94.remex.ui.theme.cardShape(
                                    uiState.shapePreset,
                                    uiState.cornerRadius
                            ),
                    onSendKey = onSendKey,
                    onSeek = onSeek,
                    onDismiss = { sheetOpen = false }
            )
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
                supportsInputSimulation = true,
                // The pause face, for the same reason: it is the state the default UNKNOWN cannot
                // show, so leaving it out would mean the preview never exercised the icon this
                // feature added (RemEx-xx6xf). A title and artist come with it, because UNKNOWN
                // draws no now-playing line at all and the preview would not show that either
                // (RemEx-nmvz6).
                playback =
                        MediaPlaybackSnapshot(
                                status = MediaPlaybackStatus.PLAYING,
                                title = "Sound of Silence",
                                artist = "Simon & Garfunkel"
                        )
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
                forced = false,
                isAwaitingConfirmation = false,
                timerText = "",
                onTimerTextChanged = {},
                onPrimaryClick = {},
                onConfirm = {},
                onCancel = {},
                modifier = Modifier.weight(1f)
            )
            CommandCard(
                card = remoteCommandCardsByAction.getValue("ForceShutdown"),
                forced = true,
                isAwaitingConfirmation = false,
                timerText = "",
                onTimerTextChanged = {},
                onPrimaryClick = {},
                onConfirm = {},
                onCancel = {},
                modifier = Modifier.weight(1f)
            )
        }
    }
}

/**
 * A group label: plain text in onSurfaceVariant, not a coloured band (android UI refresh spec,
 * "Control > Commands"). The Forced group says what it costs in its own label, so it needs no colour
 * here to warn; its cards carry the error roles instead.
 */
@Composable
private fun CommandSectionLabel(label: String) {
    Text(
            text = label,
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
    )
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun CommandCard(
        card: RemoteCommandCard,
        /** A Forced-group card: errorContainer / onErrorContainer instead of the neutral surface roles. */
        forced: Boolean,
        isAwaitingConfirmation: Boolean,
        timerText: String,
        // Defaults to no mini-player so CommandCardPreview keeps compiling without wiring up the
        // animated value RemoteControlScreenContent computes for the real screen.
        bottomOcclusion: androidx.compose.ui.unit.Dp = 16.dp,
        onTimerTextChanged: (String) -> Unit,
        onPrimaryClick: () -> Unit,
        onConfirm: () -> Unit,
        onCancel: () -> Unit,
        modifier: Modifier = Modifier
) {
    val haptics = rememberRemexHaptics()
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
    val bottomOcclusionPx = with(LocalDensity.current) { bottomOcclusion.toPx() }

    suspend fun revealConfirmActions() {
        val size = confirmActionsSize
        if (size == IntSize.Zero) return
        // Explicit rect extended below the row: the default request stops as soon as the trailing
        // edge reaches the viewport bottom, which is exactly where the docked mini-player sits.
        confirmActionsRequester.bringIntoView(
                Rect(
                        left = 0f,
                        top = 0f,
                        right = size.width.toFloat(),
                        bottom = size.height.toFloat() + bottomOcclusionPx
                )
        )
    }

    LaunchedEffect(isAwaitingConfirmation, confirmActionsSize) {
        if (isAwaitingConfirmation) revealConfirmActions()
    }

    // Expressive shape morph on press: the rounded rectangle tightens its corners while held. The
    // content keeps 16dp of padding against the largest radius, so no corner ever reaches the icon
    // or the label - the hexagon tiles this replaces clipped both (RemEx-kq10x.3).
    val interactionSource = remember { MutableInteractionSource() }
    val pressed by interactionSource.collectIsPressedAsState()
    val corner by
            animateDpAsState(
                    targetValue = if (pressed) 12.dp else 24.dp,
                    animationSpec = motionScheme.fastSpatialSpec(),
                    label = "commandCardCorner"
            )
    val containerColor =
            if (forced) MaterialTheme.colorScheme.errorContainer
            else MaterialTheme.colorScheme.surfaceContainerHigh
    val contentColor =
            if (forced) MaterialTheme.colorScheme.onErrorContainer
            else MaterialTheme.colorScheme.onSurface

    // The whole card is the button: tapping it runs the action, or arms the confirm face for one
    // that loses work. While armed it stops being clickable, so a stray tap on the card's edge can
    // neither cancel nor re-arm it - only the Confirm and Cancel buttons act.
    Surface(
            onClick = {
                haptics.perform(RemexHapticEvent.CommandSent)
                onPrimaryClick()
            },
            enabled = !isAwaitingConfirmation,
            interactionSource = interactionSource,
            shape = RoundedCornerShape(corner),
            color = containerColor,
            contentColor = contentColor,
            modifier =
                    modifier.fillMaxWidth()
                            .heightIn(min = 96.dp)
                            .animateContentSize(
                                    animationSpec = motionScheme.fastSpatialSpec(),
                                    finishedListener = { _, _ ->
                                        if (isAwaitingConfirmation) {
                                            scope.launch { revealConfirmActions() }
                                        }
                                    }
                            )
    ) {
        Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
                horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Icon(
                    imageVector = card.icon,
                    // Decorative: the label right under it says the same thing (RemEx-xqli).
                    contentDescription = null
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
                            style = MaterialTheme.typography.titleSmallEmphasized,
                            textAlign = TextAlign.Center
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
                        // wait field and the buttons so it is read before the destructive action is
                        // reachable, not after it. LocalContentColor, so it reads on both the
                        // neutral and the error container. (RemEx-awks.)
                        card.warningRes?.let { warningRes ->
                            Text(
                                    text = stringResource(warningRes),
                                    style = MaterialTheme.typography.bodySmall,
                                    textAlign = TextAlign.Center
                            )
                        }

                        // The optional wait before the PC acts, in plain words on the field itself.
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
                                        haptics.perform(RemexHapticEvent.Confirm)
                                        onConfirm()
                                    },
                                    colors =
                                            ButtonDefaults.buttonColors(
                                                    containerColor = MaterialTheme.colorScheme.error,
                                                    contentColor = MaterialTheme.colorScheme.onError
                                            ),
                                    modifier = Modifier.weight(1f),
                                    shapes = rememberRemexButtonShapes(),
                                    contentPadding = ButtonDefaults.ContentPadding
                            ) { Text(stringResource(R.string.button_confirm)) }
                            TextButton(
                                    onClick = {
                                        haptics.perform(RemexHapticEvent.Reject)
                                        onCancel()
                                    },
                                    colors = ButtonDefaults.textButtonColors(contentColor = contentColor),
                                    modifier = Modifier.weight(1f),
                                    shapes = rememberRemexButtonShapes(),
                                    contentPadding = ButtonDefaults.TextButtonContentPadding
                            ) { Text(stringResource(R.string.button_cancel)) }
                        }
                    }
                }
            }
        }
    }
}
