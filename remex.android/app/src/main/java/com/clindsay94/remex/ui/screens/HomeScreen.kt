package com.clindsay94.remex.ui.screens

import android.text.format.DateUtils
import android.view.HapticFeedbackConstants
import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Route
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.SheetValue
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.selection.toggleable
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleStartEffect
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.TelemetryDemand
import com.clindsay94.remex.data.HomePinsState
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.ui.components.LinkGlyph
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.navigation.NavDestination
import com.clindsay94.remex.ui.navigation.Screen
import com.clindsay94.remex.ui.telemetry.SensorAccents
import com.clindsay94.remex.ui.theme.CardShapes
import com.clindsay94.remex.ui.theme.AnimatedValueText
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.theme.SENSORS_CONTAINER_KEY
import com.clindsay94.remex.ui.theme.rememberContainerTransformModifier
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes

/** Everything Home draws, assembled by [HomeScreen] so [HomeScreenContent] stays previewable. */
data class HomeUiState(
        val isConnected: Boolean = false,
        val isConnecting: Boolean = false,
        /**
         * Connected, but the PC no longer recognises this phone (sweep P3): the PC card says it
         * needs pairing and offers Pair instead of "Online" and quick actions it would refuse.
         */
        val needsPairing: Boolean = false,
        /** What the PC card calls the connected PC; null while disconnected. */
        val pcName: String? = null,
        val uptime: PcUptime? = null,
        /** Lock / Sleep, already filtered by what the PC advertised. */
        val quickActions: List<String> = HomeLogic.QUICK_ACTIONS,
        /** Paired PCs the connect card offers, most recently connected first. */
        val knownPcs: List<KnownPcEntry> = emptyList(),
        val pins: HomePinsState = HomePinsState(),
        val sensors: List<TelemetrySensor> = emptyList(),
        val recent: List<RecentActivity> = emptyList(),
        val cornerRadius: Int = CardShapes.DEFAULT_CORNER_RADIUS_DP,
)

/** How many paired PCs the connect card lists before "Other PCs" takes over. */
private const val CONNECT_CARD_MAX_PCS = 3

/**
 * Home, the first tab (refresh spec, Navigation slot 1): the PC card, pinned sensors, Open Sensors,
 * shortcuts and recent activity (RemEx-wqo7a.6).
 *
 * The pinned sensors are the PC Home's own list, kept in step both ways through `home_pins_sync`;
 * an older PC gets a list kept on this phone instead ([HomePinsState.syncSupported]).
 */
@Composable
fun HomeScreen(
        onNavigateToConnection: () -> Unit,
        onOpenDestination: (NavDestination) -> Unit,
        connectionViewModel: ConnectionViewModel,
        viewModel: HomeViewModel = viewModel(),
) {
    // Home is composed only while it is the visible pager page (beyondViewportPageCount = 0), and
    // this observer also stops the parse when the app goes to the background.
    LifecycleStartEffect(viewModel) {
        viewModel.setVisible(true)
        onStopOrDispose { viewModel.setVisible(false) }
    }

    val isConnected by viewModel.isConnected.collectAsStateWithLifecycle()
    val isConnecting by viewModel.isConnecting.collectAsStateWithLifecycle()
    val needsPairing by RemexClientManager.needsPairing.collectAsStateWithLifecycle()
    val host by viewModel.connectedHost.collectAsStateWithLifecycle()
    val hostMachineName by viewModel.hostMachineName.collectAsStateWithLifecycle()
    val powerVerbs by viewModel.powerVerbs.collectAsStateWithLifecycle()
    val uptime by viewModel.uptime.collectAsStateWithLifecycle()
    val sensors by viewModel.sensors.collectAsStateWithLifecycle()
    val pins by viewModel.homePins.collectAsStateWithLifecycle()
    // Home holds the telemetry lease while it's on screen (Leanness K8): its pinned tiles read it,
    // and so does the PC card's uptime line, which froze when the lease depended on having pins.
    // The PC still pauses the stream on every other tab and in the background.
    TelemetryLeaseEffect(TelemetryDemand.HOME_PINNED)
    val history by viewModel.routineHistory.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val knownPcRows by connectionViewModel.knownPcRows.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }

    // A result is shown only while Home is started, never minutes later somewhere else.
    val lifecycleOwner = LocalLifecycleOwner.current
    LaunchedEffect(lifecycleOwner) {
        lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
            viewModel.messages.collect { message ->
                snackbarHostState.showSnackbar(message, duration = SnackbarDuration.Short)
            }
        }
    }

    val state =
            HomeUiState(
                    isConnected = isConnected,
                    isConnecting = isConnecting,
                    needsPairing = isConnected && needsPairing,
                    pcName = host?.takeIf { isConnected }?.let { HomeLogic.pcName(it, knownPcRows, hostMachineName) },
                    uptime = uptime,
                    quickActions = HomeLogic.QUICK_ACTIONS.filter { HomeLogic.isQuickActionOffered(it, powerVerbs) },
                    knownPcs = knownPcRows.filter { it.isTrusted },
                    pins = pins,
                    sensors = sensors,
                    recent = HomeLogic.recentActivity(history, knownPcRows),
                    cornerRadius = cornerRadius,
            )

    HomeScreenContent(
            state = state,
            snackbarHostState = snackbarHostState,
            onQuickAction = viewModel::sendQuickAction,
            onConnectTo = { entry -> connectionViewModel.connectToKnownPc(entry) },
            onWakePc = viewModel::wakePc,
            onSetPinned = viewModel::setHomePin,
            onNavigateToConnection = onNavigateToConnection,
            onOpenDestination = onOpenDestination,
            onPair = {
                ConnectionOpenRequests.requestAddPc()
                onNavigateToConnection()
            },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreenContent(
        state: HomeUiState,
        onQuickAction: (String) -> Unit,
        onConnectTo: (KnownPcEntry) -> Unit,
        onWakePc: () -> Unit,
        onSetPinned: (String, Boolean) -> Unit,
        onNavigateToConnection: () -> Unit,
        onOpenDestination: (NavDestination) -> Unit,
        snackbarHostState: SnackbarHostState = remember { SnackbarHostState() },
        /** Connection > Add a PC, for a PC that needs pairing again. */
        onPair: () -> Unit = onNavigateToConnection,
) {
    val view = LocalView.current
    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    var showPinSheet by rememberSaveable { mutableStateOf(false) }
    val tileShape = CardShapes.shapeFor(CardShapes.ROUNDED_RECTANGLE, state.cornerRadius)

    Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_home_title),
                        subtitle = stringResource(R.string.screen_home_subtitle),
                        scrollBehavior = scrollBehavior
                )
            },
            snackbarHost = { SnackbarHost(snackbarHostState, modifier = Modifier.navigationBarsPadding()) }
    ) { innerPadding ->
        Column(
                modifier =
                        Modifier.fillMaxSize()
                                .padding(innerPadding)
                                .verticalScroll(rememberScrollState())
                                .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(CardShapes.CARD_SPACING_DP.dp)
        ) {
            if (state.isConnected) {
                PcCard(state = state, shape = tileShape, onQuickAction = onQuickAction, onPair = onPair)
            } else {
                ConnectCard(
                        state = state,
                        shape = tileShape,
                        onConnectTo = onConnectTo,
                        onWakePc = onWakePc,
                        onNavigateToConnection = onNavigateToConnection,
                )
            }

            PinnedSensorsSection(
                    state = state,
                    shape = tileShape,
                    onEdit = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        showPinSheet = true
                    },
            )

            OpenSensorsCard(
                    shape = tileShape,
                    onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onOpenDestination(Screen.Dashboard)
                    }
            )

            ShortcutsSection(shape = tileShape, onOpenDestination = onOpenDestination)

            if (state.recent.isNotEmpty()) {
                RecentActivitySection(recent = state.recent, shape = tileShape)
            }
        }
    }

    if (showPinSheet) {
        PinSheet(
                state = state,
                onSetPinned = onSetPinned,
                onDismiss = { showPinSheet = false },
        )
    }
}

// ── PC card ─────────────────────────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PcBadge(online: Boolean) {
    // The cookie is an accent, not a container (refresh spec, "Principles"): it holds one icon.
    Box(
            modifier =
                    Modifier.size(56.dp)
                            .clip(MaterialShapes.Cookie9Sided.toShape())
                            .background(
                                    if (online) MaterialTheme.colorScheme.primaryContainer
                                    else MaterialTheme.colorScheme.surfaceContainerHighest
                            ),
            contentAlignment = Alignment.Center
    ) {
        Icon(
                Icons.Default.Computer,
                contentDescription = null,
                tint =
                        if (online) MaterialTheme.colorScheme.onPrimaryContainer
                        else MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(28.dp)
        )
    }
}

@Composable
private fun StatusChip(@StringRes label: Int, online: Boolean) {
    val container = if (online) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainerHighest
    val content = if (online) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurfaceVariant
    Surface(shape = CircleShape, color = container, contentColor = content) {
        Row(
                modifier = Modifier.padding(horizontal = 10.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                    Modifier.size(8.dp)
                            .clip(CircleShape)
                            .background(if (online) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline)
            )
            Spacer(Modifier.width(6.dp))
            Text(stringResource(label), style = MaterialTheme.typography.labelMedium)
        }
    }
}

@Composable
private fun PcCard(
        state: HomeUiState,
        shape: androidx.compose.ui.graphics.Shape,
        onQuickAction: (String) -> Unit,
        onPair: () -> Unit,
) {
    val view = LocalView.current
    var confirming by rememberSaveable { mutableStateOf<String?>(null) }
    val buttonShapes = rememberRemexButtonShapes()

    Card(
            modifier = Modifier.fillMaxWidth(),
            shape = shape,
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                PcBadge(online = !state.needsPairing)
                Spacer(Modifier.width(16.dp))
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(
                            state.pcName.orEmpty(),
                            style = MaterialTheme.typography.titleMedium,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                    )
                    if (state.needsPairing) {
                        StatusChip(R.string.connection_known_pc_needs_pairing, online = false)
                    } else {
                        StatusChip(R.string.home_pc_online, online = true)
                    }
                    state.uptime?.takeUnless { state.needsPairing }?.let { up ->
                        Text(
                                stringResource(R.string.home_pc_uptime, up.days, up.hours, up.minutes),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
            if (state.needsPairing) {
                // Lock and Sleep would be refused like everything else, so Pair takes their place.
                Text(
                        stringResource(R.string.pairing_needed_body),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Button(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                            onPair()
                        },
                        shapes = buttonShapes,
                        modifier = Modifier.fillMaxWidth()
                ) { Text(stringResource(R.string.connection_known_pc_pair_confirm)) }
            } else if (state.quickActions.isNotEmpty()) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    state.quickActions.forEach { action ->
                        FilledTonalButton(
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                    if (HomeLogic.needsConfirmation(action)) confirming = action else onQuickAction(action)
                                },
                                shapes = buttonShapes,
                                modifier = Modifier.weight(1f)
                        ) {
                            Icon(quickActionIcon(action), contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(8.dp))
                            Text(stringResource(quickActionLabel(action)))
                        }
                    }
                }
            }
        }
    }

    confirming?.let { action ->
        AlertDialog(
                onDismissRequest = { confirming = null },
                title = { Text(stringResource(R.string.remote_control_confirm_choice)) },
                text = { Text(stringResource(quickActionLabel(action))) },
                confirmButton = {
                    TextButton(onClick = {
                        confirming = null
                        onQuickAction(action)
                    }) { Text(stringResource(quickActionLabel(action))) }
                },
                dismissButton = {
                    TextButton(onClick = { confirming = null }) { Text(stringResource(R.string.button_cancel)) }
                }
        )
    }
}

private fun quickActionIcon(action: String): ImageVector =
        when (action) {
            "Sleep" -> Icons.Default.Bedtime
            else -> Icons.Default.Lock
        }

@StringRes
private fun quickActionLabel(action: String): Int =
        when (action) {
            "Sleep" -> R.string.rc_sleep
            else -> R.string.rc_lock_pc
        }

/** Disconnected, the PC card is the connect card: known PCs, Connect, and Wake. */
@Composable
private fun ConnectCard(
        state: HomeUiState,
        shape: androidx.compose.ui.graphics.Shape,
        onConnectTo: (KnownPcEntry) -> Unit,
        onWakePc: () -> Unit,
        onNavigateToConnection: () -> Unit,
) {
    val view = LocalView.current
    val buttonShapes = rememberRemexButtonShapes()
    Card(
            modifier = Modifier.fillMaxWidth(),
            shape = shape,
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                PcBadge(online = false)
                Spacer(Modifier.width(16.dp))
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(
                            stringResource(R.string.dashboard_not_connected),
                            style = MaterialTheme.typography.titleMedium,
                            color = MaterialTheme.colorScheme.onSurface
                    )
                    StatusChip(
                            if (state.isConnecting) R.string.home_pc_connecting else R.string.home_pc_offline,
                            online = false
                    )
                }
            }

            val pcs = state.knownPcs.take(CONNECT_CARD_MAX_PCS)
            if (pcs.isEmpty()) {
                Text(
                        stringResource(R.string.home_connect_body_none),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Button(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onNavigateToConnection()
                        },
                        shapes = buttonShapes,
                        modifier = Modifier.fillMaxWidth()
                ) { Text(stringResource(R.string.button_connect)) }
            } else {
                pcs.forEach { pc ->
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                    pc.displayName,
                                    style = MaterialTheme.typography.bodyLarge,
                                    color = MaterialTheme.colorScheme.onSurface,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                            )
                            if (pc.isNamed) {
                                Text(
                                        pc.address,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis
                                )
                            }
                        }
                        Spacer(Modifier.width(8.dp))
                        Button(
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                    onConnectTo(pc)
                                },
                                shapes = buttonShapes,
                                enabled = !state.isConnecting
                        ) { Text(stringResource(R.string.button_connect)) }
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedButton(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                            onWakePc()
                        },
                        shapes = buttonShapes,
                ) {
                    Icon(Icons.Default.PowerSettingsNew, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text(stringResource(R.string.dashboard_wake_pc))
                }
                if (pcs.isNotEmpty()) {
                    TextButton(onClick = onNavigateToConnection) { Text(stringResource(R.string.home_connect_other)) }
                }
            }
        }
    }
}

// ── Pinned sensors ──────────────────────────────────────────────────────────────────────────────

@Composable
private fun SectionHeader(@StringRes title: Int, action: (@Composable () -> Unit)? = null) {
    Row(
            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
            verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
                stringResource(title),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f)
        )
        action?.invoke()
    }
}

@Composable
private fun PinnedSensorsSection(state: HomeUiState, shape: androidx.compose.ui.graphics.Shape, onEdit: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionHeader(R.string.home_pinned_title) {
            TextButton(onClick = onEdit, enabled = state.isConnected) {
                Icon(Icons.Default.Edit, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.home_pinned_edit))
            }
        }
        val tiles = HomeLogic.pinnedTiles(state.pins.pinned, state.sensors)
        val emptyText =
                when {
                    !state.isConnected -> R.string.home_pinned_waiting
                    // The PC sends no readings to a phone it doesn't recognise.
                    state.needsPairing -> R.string.pairing_needed_title
                    state.pins.pinned.isEmpty() -> R.string.home_pinned_empty
                    tiles.isEmpty() -> R.string.home_pinned_none_reporting
                    else -> null
                }
        if (emptyText != null) {
            Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = shape,
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)
            ) {
                Text(
                        stringResource(emptyText),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(16.dp)
                )
            }
        } else {
            tiles.chunked(2).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(CardShapes.CARD_SPACING_DP.dp)) {
                    row.forEach { sensor -> PinnedTile(sensor, shape, Modifier.weight(1f)) }
                    if (row.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

@Composable
private fun PinnedTile(sensor: TelemetrySensor, shape: androidx.compose.ui.graphics.Shape, modifier: Modifier) {
    val accent = SensorAccents.accentFor(sensor.kind, MaterialTheme.colorScheme)
    Card(
            modifier = modifier.height(88.dp),
            shape = shape,
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
    ) {
        Column(modifier = Modifier.fillMaxSize().padding(12.dp), verticalArrangement = Arrangement.SpaceBetween) {
            Text(
                    sensor.name,
                    style = MaterialTheme.typography.labelMedium,
                    color = accent.onChip,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.background(accent.chip, RoundedCornerShape(6.dp)).padding(horizontal = 6.dp, vertical = 2.dp)
            )
            // Rolls up or down as the reading changes; still text while it holds (phase 6).
            AnimatedValueText(
                    formatSensor(sensor).text,
                    style = MaterialTheme.typography.headlineSmall,
                    color = accent.series,
                    maxLines = 1
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PinSheet(state: HomeUiState, onSetPinned: (String, Boolean) -> Unit, onDismiss: () -> Unit) {
    val view = LocalView.current
    // The sheet lists what the PC reports right now, so it reads telemetry while open (Leanness K8).
    TelemetryLeaseEffect(TelemetryDemand.HOME_PIN_SHEET)
    val groups = HomeLogic.pinSheet(state.pins, state.sensors)
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 16.dp)) {
            Text(stringResource(R.string.home_pin_sheet_title), style = MaterialTheme.typography.titleLarge)
            Text(
                    stringResource(
                            if (state.pins.syncSupported) R.string.home_pin_sheet_body_synced else R.string.home_pin_sheet_body_local
                    ),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp, bottom = 8.dp)
            )
            Column(modifier = Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState())) {
                if (groups.isEmpty()) {
                    Text(
                            stringResource(R.string.home_pin_sheet_empty),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(vertical = 16.dp)
                    )
                }
                groups.forEach { group ->
                    Text(
                            group.label ?: stringResource(R.string.home_pin_sheet_not_reporting),
                            style = MaterialTheme.typography.labelLarge,
                            color = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.padding(top = 12.dp, bottom = 4.dp)
                    )
                    group.entries.forEach { entry ->
                        // Pinned rows stay tappable even when the PC can no longer pin them, so a
                        // stale pin can always be removed.
                        val enabled = entry.pinned || entry.canPin
                        Row(
                                modifier =
                                        Modifier.fillMaxWidth()
                                                .heightIn(min = 48.dp)
                                                .toggleable(
                                                        value = entry.pinned,
                                                        enabled = enabled,
                                                        role = Role.Checkbox,
                                                        onValueChange = { pinned ->
                                                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                                            onSetPinned(entry.name, pinned)
                                                        }
                                                ),
                                verticalAlignment = Alignment.CenterVertically
                        ) {
                            Checkbox(checked = entry.pinned, onCheckedChange = null, enabled = enabled)
                            Spacer(Modifier.width(12.dp))
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                        entry.name,
                                        style = MaterialTheme.typography.bodyLarge,
                                        color =
                                                if (enabled) MaterialTheme.colorScheme.onSurface
                                                else MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                if (!entry.canPin && !entry.pinned) {
                                    Text(
                                            stringResource(R.string.dashboard_pin_unavailable),
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }
                    }
                }
            }
            Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth().padding(top = 12.dp)) {
                Text(stringResource(R.string.button_done))
            }
        }
    }
}

// ── Open Sensors, shortcuts, recent activity ────────────────────────────────────────────────────

@Composable
private fun OpenSensorsCard(shape: androidx.compose.ui.graphics.Shape, onClick: () -> Unit) {
    val sensorsTitle = stringResource(R.string.screen_dashboard_title)
    // One end of the container transform: this card grows into the Sensors route (phase 6).
    val containerTransform = rememberContainerTransformModifier(SENSORS_CONTAINER_KEY, shape)
    Card(
            onClick = onClick,
            modifier = Modifier.fillMaxWidth().then(containerTransform),
            shape = shape,
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
    ) {
        Row(modifier = Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(
                    Icons.Default.Dashboard,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(32.dp)
            )
            Spacer(Modifier.width(16.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                        stringResource(R.string.home_open_sensors_title, sensorsTitle),
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface
                )
                Text(
                        stringResource(R.string.home_open_sensors_body),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Icon(
                    Icons.AutoMirrored.Filled.KeyboardArrowRight,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

private data class Shortcut(val destination: NavDestination, val icon: ImageVector)

private val SHORTCUTS =
        listOf(
                Shortcut(Screen.Desktop, Icons.Default.Computer),
                Shortcut(Screen.FileTransfer, Icons.Default.FolderOpen),
                Shortcut(Screen.Routines, Icons.Default.Route),
        )

@Composable
private fun ShortcutsSection(shape: androidx.compose.ui.graphics.Shape, onOpenDestination: (NavDestination) -> Unit) {
    val view = LocalView.current
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionHeader(R.string.home_shortcuts_title)
        Row(horizontalArrangement = Arrangement.spacedBy(CardShapes.CARD_SPACING_DP.dp)) {
            SHORTCUTS.forEach { shortcut ->
                Card(
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                            onOpenDestination(shortcut.destination)
                        },
                        modifier = Modifier.weight(1f).semantics(mergeDescendants = true) {},
                        shape = shape,
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
                ) {
                    Column(
                            modifier = Modifier.fillMaxWidth().padding(vertical = 16.dp, horizontal = 8.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(shortcut.icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Text(
                                stringResource(shortcut.destination.titleRes),
                                style = MaterialTheme.typography.labelLarge,
                                color = MaterialTheme.colorScheme.onSurface,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun RecentActivitySection(recent: List<RecentActivity>, shape: androidx.compose.ui.graphics.Shape) {
    val context = LocalContext.current
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionHeader(R.string.home_recent_title)
        Card(
                modifier = Modifier.fillMaxWidth(),
                shape = shape,
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
        ) {
            Column(modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
                val now = System.currentTimeMillis()
                recent.forEach { item ->
                    val time = DateUtils.getRelativeTimeSpanString(item.atMillis, now, DateUtils.MINUTE_IN_MILLIS).toString()
                    val (icon, title, status) =
                            when (item) {
                                is RecentActivity.RoutineRan ->
                                        Triple(
                                                Icons.Default.Route,
                                                item.routineName,
                                                if (item.succeeded) stringResource(R.string.home_recent_status_ran)
                                                else RoutineReasonText.history(context, item.reasonCode, item.reasonArgs)
                                        )
                                is RecentActivity.Connected ->
                                        Triple(LinkGlyph, item.pcName, stringResource(R.string.home_recent_status_connected))
                            }
                    Row(
                            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically
                    ) {
                        Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.width(16.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                    title,
                                    style = MaterialTheme.typography.bodyLarge,
                                    color = MaterialTheme.colorScheme.onSurface,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                    stringResource(R.string.home_recent_detail, status, time),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun HomeScreenPreview() {
    RemExTheme {
        HomeScreenContent(
                state = HomeUiState(),
                onQuickAction = {},
                onConnectTo = {},
                onWakePc = {},
                onSetPinned = { _, _ -> },
                onNavigateToConnection = {},
                onOpenDestination = {},
        )
    }
}
