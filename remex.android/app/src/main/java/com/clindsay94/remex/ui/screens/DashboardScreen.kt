package com.clindsay94.remex.ui.screens

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.snap
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.core.animateIntOffsetAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.SheetValue
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size as GeoSize
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asAndroidPath
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import androidx.lifecycle.compose.LifecycleStartEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.TelemetryDemand
import com.clindsay94.remex.data.HomePinsState
import com.clindsay94.remex.ui.components.DeleteSweepGlyph
import com.clindsay94.remex.ui.components.RedoGlyph
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.UndoGlyph
import com.clindsay94.remex.ui.components.floatingChromeBottomPadding
import com.clindsay94.remex.ui.components.navigationBarBottomInset
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertSeverity
import com.clindsay94.remex.data.SensorAlertsState
import com.clindsay94.remex.ui.screens.sensors.CardPinControl
import com.clindsay94.remex.ui.screens.sensors.SensorAlertBlock
import com.clindsay94.remex.ui.screens.sensors.SensorAlertEditorLogic
import com.clindsay94.remex.ui.screens.sensors.GridWidth
import com.clindsay94.remex.ui.screens.sensors.GridWidthPicker
import com.clindsay94.remex.ui.screens.sensors.SensorAlertEditorSheet
import com.clindsay94.remex.ui.screens.sensors.SensorAlertTarget
import com.clindsay94.remex.ui.screens.sensors.SensorAlertsListSheet
import com.clindsay94.remex.ui.screens.sensors.SensorGrid
import com.clindsay94.remex.ui.screens.sensors.SensorGridCard
import com.clindsay94.remex.ui.screens.sensors.SensorLayout
import com.clindsay94.remex.ui.screens.sensors.SensorsChrome
import com.clindsay94.remex.ui.telemetry.MetricKind
import com.clindsay94.remex.ui.telemetry.SensorAccents
import com.clindsay94.remex.ui.theme.CardShapes
import com.clindsay94.remex.ui.theme.shapeSafeArea
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.theme.cardInnerPadding
import com.clindsay94.remex.ui.theme.cardShape
import kotlin.math.roundToInt
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes
import androidx.compose.material3.ButtonDefaults
import com.clindsay94.remex.ui.components.RemexHaptics

/**
 * How far the Add-card button reaches up from the nav bar: the 56.dp button plus its 16.dp edge
 * margin. The nav-bar inset is added by [floatingChromeBottomPadding] (RemEx-wqo7a.1).
 */
private val DashboardFabFootprint = 56.dp + 16.dp

/** One row of the Add-card list. */
private data class AvailableCardItem(val id: String, val title: String, val subtitle: String, val group: String)

/**
 * The Sensors grid (RemEx-wqo7a.7 / .8): every sensor card on an aligned 2-column grid (4 columns
 * from 600dp), with an explicit edit mode for moving, resizing, removing, adding and pinning cards to
 * Home. Outside edit mode the grid only shows readings; nothing on it can be moved by accident.
 */
@Composable
fun DashboardScreen(
        viewModel: DashboardViewModel = viewModel(),
        onNavigateToConnection: () -> Unit = {},
        isVisible: Boolean = true,
) {
    // P1-16: mirrors TaskManagerScreen's own LifecycleStartEffect (P0-1) - isVisible is pager-only
    // (current page, not mid-scroll) and never reflects the app backgrounding; LifecycleStartEffect
    // is a lifecycle OBSERVER rather than a recomposition, so it still fires onStopOrDispose on
    // ON_STOP even though Compose pauses the frame clock there.
    LifecycleStartEffect(viewModel, isVisible) {
        viewModel.setVisible(isVisible)
        onStopOrDispose { viewModel.setVisible(false) }
    }
    // The canvas reads live telemetry only while it is the visible page (Leanness K8).
    TelemetryLeaseEffect(TelemetryDemand.SENSORS_CANVAS, active = isVisible)

    val isConnected by viewModel.isConnected.collectAsStateWithLifecycle()
    val needsPairing by com.clindsay94.remex.RemexClientManager.needsPairing.collectAsStateWithLifecycle()
    val telemetrySensors by viewModel.telemetrySensors.collectAsStateWithLifecycle()
    val telemetryHistory by viewModel.telemetryHistory.collectAsStateWithLifecycle()
    val layout by viewModel.layout.collectAsStateWithLifecycle()
    val editMode by viewModel.editMode.collectAsStateWithLifecycle()
    val draggingCardId by viewModel.draggingCardId.collectAsStateWithLifecycle()
    val canUndo by viewModel.canUndo.collectAsStateWithLifecycle()
    val canRedo by viewModel.canRedo.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val cardOpacity by viewModel.cardOpacity.collectAsStateWithLifecycle()
    val pcCardShapePreset by viewModel.pcCardShapePreset.collectAsStateWithLifecycle()
    val telemetryCardShapePreset by viewModel.telemetryCardShapePreset.collectAsStateWithLifecycle()
    val categoryShapePresets by viewModel.categoryShapePresets.collectAsStateWithLifecycle(initialValue = emptyMap())
    val homePins by viewModel.homePins.collectAsStateWithLifecycle()
    val sensorAlerts by viewModel.sensorAlerts.collectAsStateWithLifecycle()
    val pcAlertsEnabled by viewModel.pcAlertsEnabled.collectAsStateWithLifecycle()
    val gridWidth by viewModel.gridWidth.collectAsStateWithLifecycle()
    val coachStep by viewModel.coachStep.collectAsStateWithLifecycle()
    // Live on-screen centre of the ⋮ menu, so the coach pointer lands on it whatever the insets.
    var menuAnchor by remember { mutableStateOf(Offset.Zero) }

    // Back leaves edit mode before it leaves the screen.
    BackHandler(enabled = editMode) { viewModel.exitEditMode() }

    Box(modifier = Modifier.fillMaxSize()) {
        DashboardScreenContent(
                isConnected = isConnected,
                telemetrySensors = telemetrySensors,
                telemetryHistory = telemetryHistory,
                layout = layout,
                editMode = editMode,
                draggingCardId = draggingCardId,
                homePins = homePins,
                sensorAlerts = sensorAlerts,
                pcAlertsEnabled = pcAlertsEnabled,
                gridWidth = gridWidth,
                onSetGridWidth = viewModel::setGridWidth,
                onSetPcAlertsEnabled = viewModel::setPcAlertsEnabled,
                onSetSensorAlert = viewModel::setSensorAlert,
                sensorAlertRefusals = viewModel.sensorAlertRefusals,
                onRemoveSensorAlert = viewModel::removeSensorAlert,
                onRefreshSensorAlerts = viewModel::refreshSensorAlerts,
                cornerRadius = cornerRadius,
                cardOpacity = cardOpacity,
                // ONE tile shape for the whole grid: the global telemetry / per-category choice,
                // never a card's own stored override (kept in storage, not shown).
                shapeIndexFor = { card ->
                    DashboardShapes.resolveShapeIndex(
                            card.copy(shapePreset = DashboardShapes.SHAPE_PRESET_INHERIT),
                            pcCardShapePreset,
                            telemetryCardShapePreset,
                            categoryShapePresets
                    )
                },
                canUndo = canUndo,
                canRedo = canRedo,
                onNavigateToConnection = onNavigateToConnection,
                onEnterEditMode = viewModel::enterEditMode,
                onExitEditMode = viewModel::exitEditMode,
                onBeginDrag = viewModel::beginCardDrag,
                onDragTo = viewModel::dragCardTo,
                onEndDrag = viewModel::endCardDrag,
                onMoveCard = { id, index -> viewModel.moveCard(id, index) },
                onCycleSpan = { viewModel.cycleCardSpan(it) },
                onRemoveCard = { viewModel.removeCard(it) },
                onUndo = { viewModel.undo() },
                onRedo = { viewModel.redo() },
                onClearAllCards = { viewModel.clearAllCards() },
                onSetCardEnabled = viewModel::setCardEnabled,
                onSetHomePin = viewModel::setHomePin,
                onPickDisplayMode = viewModel::setTelemetryDisplayMode,
                onSetCardTitle = viewModel::setCardCustomTitle,
                onSetValueOverlay = viewModel::setCardValueOverlay,
                onReplayCoach = viewModel::replayCoach,
                onMenuAnchor = { menuAnchor = it },
                editRevision = viewModel.editRevision,
                onUndoIfUnchanged = { viewModel.undoIfUnchanged(it) },
                needsPairing = isConnected && needsPairing,
                onPair = {
                    ConnectionOpenRequests.requestAddPc()
                    onNavigateToConnection()
                },
        )
        // First-run coach marks (RemEx-km0i.10), mounted last = top of z-order. Never over edit mode.
        AnimatedVisibility(
                visible = coachStep >= 0 && !editMode,
                enter = fadeIn(animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec()),
                exit = fadeOut(animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec()),
        ) {
            DashboardCoachOverlay(
                    step = coachStep,
                    menuAnchor = menuAnchor,
                    onAdvance = { viewModel.advanceCoach() },
                    onDismiss = { viewModel.dismissCoach() },
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun DashboardScreenContent(
        isConnected: Boolean,
        telemetrySensors: List<TelemetrySensor>,
        telemetryHistory: Map<String, List<Float>>,
        layout: SensorLayout,
        editMode: Boolean,
        draggingCardId: String?,
        homePins: HomePinsState,
        cornerRadius: Int,
        cardOpacity: Float,
        shapeIndexFor: (HomeCardState) -> Float,
        canUndo: Boolean,
        canRedo: Boolean,
        onNavigateToConnection: () -> Unit,
        onEnterEditMode: () -> Unit,
        onExitEditMode: () -> Unit,
        onBeginDrag: (String) -> Boolean,
        onDragTo: (Int) -> Unit,
        onEndDrag: () -> Unit,
        onMoveCard: (String, Int) -> Unit,
        onCycleSpan: (String) -> Unit,
        onRemoveCard: (String) -> Unit,
        onUndo: () -> Unit,
        onRedo: () -> Unit,
        onClearAllCards: () -> Unit,
        onSetCardEnabled: (String, Boolean) -> Unit,
        onSetHomePin: (String, Boolean) -> Unit,
        onPickDisplayMode: (String, TelemetryDisplayMode, String?) -> Unit,
        onSetCardTitle: (String, String?) -> Unit,
        onSetValueOverlay: (String, Boolean) -> Unit,
        onReplayCoach: () -> Unit = {},
        onMenuAnchor: (Offset) -> Unit = {},
        /** Counts layout edits, so the card-removed Undo targets only its own removal (review R5). */
        editRevision: StateFlow<Long> = MutableStateFlow(0L),
        onUndoIfUnchanged: (Long) -> Unit = {},
        /** The PC's alert rules (RemEx-pp4cm.12): the cards' bells, the "Alert me..." sheet and the Alerts list. */
        sensorAlerts: SensorAlertsState = SensorAlertsState(),
        pcAlertsEnabled: Boolean = true,
        /** Auto, 2, 3 or 4 columns, chosen in edit mode (RemEx-pp4cm.16). */
        gridWidth: GridWidth = GridWidth.AUTO,
        onSetGridWidth: (GridWidth) -> Unit = {},
        onSetPcAlertsEnabled: (Boolean) -> Unit = {},
        onSetSensorAlert: suspend (String, String, String?, Double, SensorAlertDirection, SensorAlertSeverity) -> Boolean =
                { _, _, _, _, _, _ -> true },
        /** Sensors whose alert the PC answered without keeping, for a snackbar (RemEx-pp4cm.12). */
        sensorAlertRefusals: Flow<String> = emptyFlow(),
        onRemoveSensorAlert: (String) -> Unit = {},
        onRefreshSensorAlerts: () -> Unit = {},
        /**
         * Connected, but the PC no longer recognises this phone, so it sends no readings: the grid
         * says so instead of sitting on empty tiles (3.0 comb, needs-pairing-gaps).
         */
        needsPairing: Boolean = false,
        onPair: () -> Unit = onNavigateToConnection,
) {
    val haptics = rememberRemexHaptics()
    val chrome = SensorsChrome.forMode(editMode)
    val pairingBlocksGrid =
            !editMode && ConnectedPane.state(isConnected, needsPairing) == ConnectedPaneState.NeedsPairing
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val telemetryFallback = stringResource(R.string.dashboard_telemetry_fallback)
    val cardRemovedText = stringResource(R.string.dashboard_card_removed)
    val undoText = stringResource(R.string.dashboard_menu_undo)
    val pinUnavailableText = stringResource(R.string.dashboard_pin_unavailable)

    // ONE INDEX PER TICK, INSTEAD OF TWO SCANS PER VISIBLE CARD (RemEx-cite item 4). Keyed on
    // telemetrySensors itself: it holds the values, which change every tick.
    val sensorIndex = remember(telemetrySensors) { SensorIndex(telemetrySensors) }

    // Keyed on what the Add-card list is BUILT FROM - id, name, category, group - not on `value`,
    // which changes every tick and which no list row reads (RemEx-cite item 3).
    val availableCardsKey =
            remember(telemetrySensors) {
                telemetrySensors.fold(7) { acc, sensor ->
                    acc * 31 + sensor.id.hashCode() + sensor.name.hashCode() * 3 +
                            sensor.category.hashCode() * 5 + sensor.group.hashCode() * 7
                }
            }
    val availableCards =
            remember(availableCardsKey, telemetryFallback) {
                telemetrySensors
                        .map { sensor ->
                            AvailableCardItem(
                                    id = sensor.id,
                                    title = sensor.name,
                                    subtitle = sensor.category.ifBlank { telemetryFallback },
                                    group = sensor.group.ifBlank { sensor.category.ifBlank { telemetryFallback } }
                            )
                        }
                        .distinctBy { it.id }
                        .sortedBy { it.group }
            }

    var showCardDrawer by remember { mutableStateOf(false) }
    var pickerCardId by remember { mutableStateOf<String?>(null) }
    var menuOpen by remember { mutableStateOf(false) }
    var alertTarget by remember { mutableStateOf<SensorAlertTarget?>(null) }
    // Why the sheet's last Save was not sent; a different sheet starts clean.
    var alertSaveFailure by remember(alertTarget) { mutableStateOf<SensorAlertBlock?>(null) }
    val alertRefusedFormat = stringResource(R.string.sensor_alert_refused_snackbar)
    LaunchedEffect(sensorAlertRefusals) {
        sensorAlertRefusals.collect { name ->
            snackbarHostState.showSnackbar(alertRefusedFormat.format(name), duration = SnackbarDuration.Long)
        }
    }
    var showAlertList by remember { mutableStateOf(false) }
    val visibleCards = layout.visibleCards

    val topBarScrollBehavior = rememberRemexTopBarScrollBehavior()
    Surface(modifier = Modifier.fillMaxSize()) {
        Box(modifier = Modifier.fillMaxSize()) {
            Column(modifier = Modifier.fillMaxSize().nestedScroll(topBarScrollBehavior.nestedScrollConnection)) {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_dashboard_title),
                        subtitle = stringResource(R.string.screen_dashboard_subtitle),
                        scrollBehavior = topBarScrollBehavior,
                        actions = {
                            if (chrome.showEditActions) {
                                IconButton(onClick = onUndo, enabled = canUndo, shapes = rememberRemexIconButtonShapes()) {
                                    Icon(UndoGlyph, contentDescription = undoText)
                                }
                                IconButton(onClick = onRedo, enabled = canRedo, shapes = rememberRemexIconButtonShapes()) {
                                    Icon(
                                            RedoGlyph,
                                            contentDescription = stringResource(R.string.dashboard_menu_redo)
                                    )
                                }
                                TextButton(onClick = {
                                    haptics.perform(RemexHapticEvent.Press)
                                    onExitEditMode()
                                },
                                    shapes = rememberRemexButtonShapes(),
                                    contentPadding = ButtonDefaults.TextButtonContentPadding,
                                ) { Text(stringResource(R.string.button_done)) }
                            } else {
                                if (sensorAlerts.canEdit) {
                                    IconButton(
                                            onClick = {
                                                haptics.perform(RemexHapticEvent.Press)
                                                onRefreshSensorAlerts()
                                                showAlertList = true
                                            },
                                            shapes = rememberRemexIconButtonShapes(),
                                    ) {
                                        Icon(
                                                Icons.Filled.Notifications,
                                                contentDescription = stringResource(R.string.cd_sensor_alerts_open),
                                        )
                                    }
                                }
                                IconButton(onClick = onReplayCoach, shapes = rememberRemexIconButtonShapes()) {
                                    Icon(
                                            Icons.AutoMirrored.Filled.HelpOutline,
                                            contentDescription = stringResource(R.string.coach_replay),
                                    )
                                }
                            }
                            Box {
                                IconButton(
                                        modifier = Modifier.onGloballyPositioned { onMenuAnchor(it.boundsInRoot().center) },
                                        onClick = {
                                            haptics.perform(RemexHapticEvent.Press)
                                            menuOpen = true
                                        },
                                        shapes = rememberRemexIconButtonShapes(),
                                ) {
                                    Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.cd_more_options))
                                }
                                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                                    if (!editMode) {
                                        DropdownMenuItem(
                                                text = { Text(stringResource(R.string.dashboard_menu_edit)) },
                                                leadingIcon = { Icon(Icons.Default.Edit, contentDescription = null) },
                                                onClick = {
                                                    menuOpen = false
                                                    onEnterEditMode()
                                                }
                                        )
                                    } else {
                                        DropdownMenuItem(
                                                text = { Text(stringResource(R.string.dashboard_menu_clear_cards)) },
                                                leadingIcon = {
                                                    Icon(
                                                            DeleteSweepGlyph,
                                                            contentDescription = null,
                                                            tint = MaterialTheme.colorScheme.error
                                                    )
                                                },
                                                onClick = {
                                                    haptics.perform(RemexHapticEvent.Confirm)
                                                    menuOpen = false
                                                    onClearAllCards()
                                                }
                                        )
                                    }
                                }
                            }
                        }
                )

                val dashboardScroll = rememberScrollState()
                // Tapping the tab you are on goes back to the top (RemEx-pp4cm.3).
                com.clindsay94.remex.ui.navigation.TabReselectEffect(com.clindsay94.remex.ui.navigation.Screen.Dashboard) { dashboardScroll.animateScrollTo(0) }
                Column(
                        modifier =
                                Modifier.fillMaxSize()
                                        .verticalScroll(dashboardScroll)
                                        .padding(horizontal = 16.dp)
                                        .padding(top = 8.dp)
                                        .padding(
                                                // Scroll room below the grid for the Add-card button, so
                                                // the last card can always be scrolled out from under it.
                                                bottom =
                                                        floatingChromeBottomPadding(
                                                                floatingFootprint = if (chrome.showAddButton) DashboardFabFootprint else 0.dp,
                                                                navBarInset = navigationBarBottomInset()
                                                        )
                                        ),
                        verticalArrangement = Arrangement.spacedBy(CardShapes.CARD_SPACING_DP.dp)
                ) {
                    if (!isConnected && !editMode) {
                        NotConnectedBanner(onNavigateToConnection = onNavigateToConnection)
                    }
                    if (editMode) {
                        Text(
                                stringResource(R.string.dashboard_edit_banner),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        GridWidthPicker(selected = gridWidth, onSelect = onSetGridWidth)
                    }
                    if (pairingBlocksGrid) {
                        NeedsPairingContent(onPair = onPair)
                    } else if (visibleCards.isEmpty()) {
                        Text(
                                stringResource(if (editMode) R.string.dashboard_empty_grid_edit else R.string.dashboard_empty_grid),
                                style = MaterialTheme.typography.bodyLarge,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(vertical = 24.dp)
                        )
                    } else {
                        SensorGridLayout(
                                cards = visibleCards,
                                gridWidth = gridWidth,
                                editMode = editMode,
                                draggingCardId = draggingCardId,
                                onEnterEditMode = onEnterEditMode,
                                onBeginDrag = onBeginDrag,
                                onDragTo = onDragTo,
                                onEndDrag = onEndDrag,
                        ) { card, index, isDragging ->
                            val sensor = sensorIndex.select(card.sensorId)
                            val sensorName = sensor?.name ?: card.title
                            val shapeIndex = shapeIndexFor(card)
                            val moveEarlier = stringResource(R.string.dashboard_move_earlier)
                            val moveLater = stringResource(R.string.dashboard_move_later)
                            SensorGridCard(
                                    shape = cardShape(shapeIndex, cornerRadius),
                                    cardOpacity = cardOpacity,
                                    editMode = chrome.showCardControls,
                                    isDragging = isDragging,
                                    pin =
                                            CardPinControl(
                                                    pinned = homePins.isPinned(sensorName),
                                                    canPin = homePins.connected && homePins.canPin(sensorName),
                                                    connected = homePins.connected,
                                            ),
                                    onTogglePin = { onSetHomePin(sensorName, !homePins.isPinned(sensorName)) },
                                    onPinUnavailable = {
                                        scope.launch {
                                            snackbarHostState.showSnackbar(pinUnavailableText, duration = SnackbarDuration.Short)
                                        }
                                    },
                                    onCycleSize = { onCycleSpan(card.id) },
                                    onRemove = {
                                        onRemoveCard(card.id)
                                        // The Undo here is for THIS removal. Read the revision right after
                                        // it; a newer edit (a resize, another removal) dismisses the
                                        // snackbar, and a late tap still cannot undo that newer edit.
                                        val offeredFor = editRevision.value
                                        scope.launch {
                                            val dismissOnNewerEdit =
                                                    launch {
                                                        editRevision.first { it != offeredFor }
                                                        snackbarHostState.currentSnackbarData
                                                                ?.takeIf { it.visuals.message == cardRemovedText }
                                                                ?.dismiss()
                                                    }
                                            val result =
                                                    snackbarHostState.showSnackbar(
                                                            cardRemovedText,
                                                            actionLabel = undoText,
                                                            duration = SnackbarDuration.Short
                                                    )
                                            dismissOnNewerEdit.cancel()
                                            if (result == SnackbarResult.ActionPerformed) onUndoIfUnchanged(offeredFor)
                                        }
                                    },
                                    modifier =
                                            Modifier.fillMaxSize().semantics {
                                                if (editMode) {
                                                    customActions =
                                                            buildList {
                                                                if (index > 0) {
                                                                    add(CustomAccessibilityAction(moveEarlier) { onMoveCard(card.id, index - 1); true })
                                                                }
                                                                if (index < visibleCards.lastIndex) {
                                                                    add(CustomAccessibilityAction(moveLater) { onMoveCard(card.id, index + 1); true })
                                                                }
                                                            }
                                                }
                                            }
                            ) {
                                val history = telemetryHistory[sensor?.id].orEmpty()
                                val secondarySensor = sensorIndex.select(card.secondarySensorId)
                                TelemetryCardContent(
                                        title = card.customTitle?.takeIf { it.isNotBlank() } ?: card.title,
                                        sensor = sensor,
                                        history = history,
                                        mode = card.displayMode,
                                        secondarySensor = secondarySensor,
                                        secondaryHistory = telemetryHistory[secondarySensor?.id].orEmpty(),
                                        shapeIndex = shapeIndex,
                                        showValueOverlay = card.showValueOverlay,
                                        selectionActive = !chrome.showViewPicker,
                                        hasAlert = sensorAlerts.ruleFor(sensorName) != null,
                                        onOpenPicker = { pickerCardId = card.id }
                                )
                            }
                        }
                    }
                }
            }

            AnimatedVisibility(
                    visible = chrome.showAddButton,
                    enter = scaleIn(MaterialTheme.motionScheme.defaultSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
                    exit = scaleOut(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                    modifier = Modifier.align(Alignment.BottomEnd).navigationBarsPadding().padding(16.dp)
            ) {
                ExtendedFloatingActionButton(
                        onClick = {
                            haptics.perform(RemexHapticEvent.Press)
                            showCardDrawer = true
                        },
                        icon = { Icon(Icons.Default.Add, contentDescription = null) },
                        text = { Text(stringResource(R.string.dashboard_add_cards)) }
                )
            }

            SnackbarHost(
                    hostState = snackbarHostState,
                    modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding()
            )
        }
    }

    if (showCardDrawer) {
        CardDrawerSheet(
                availableCards = availableCards,
                enabled = layout.enabled,
                onSetCardEnabled = onSetCardEnabled,
                onDismiss = { showCardDrawer = false }
        )
    }

    pickerCardId?.let { cardId ->
        val pickedCard = layout.cards.firstOrNull { it.id == cardId }
        if (pickedCard != null) {
            val pickedSensor = sensorIndex.select(pickedCard.sensorId)
            DisplayModePickerSheet(
                    cardId = cardId,
                    sensor = pickedSensor,
                    history = telemetryHistory[pickedSensor?.id].orEmpty(),
                    currentMode = pickedCard.displayMode,
                    currentTitle = pickedCard.customTitle?.takeIf { it.isNotBlank() } ?: pickedCard.title,
                    currentShowValueOverlay = pickedCard.showValueOverlay,
                    otherSensors = telemetrySensors.filter { it.id != pickedCard.sensorId },
                    onDismiss = { pickerCardId = null },
                    onPickDisplayMode = { id, mode, secondary ->
                        onPickDisplayMode(id, mode, secondary)
                        pickerCardId = null
                    },
                    onSetTitle = onSetCardTitle,
                    onSetValueOverlay = onSetValueOverlay,
                    onAlertMe =
                            if (sensorAlerts.canEdit && pickedSensor != null) {
                                {
                                    pickerCardId = null
                                    alertTarget =
                                            SensorAlertTarget(
                                                    sensorName = pickedSensor.name,
                                                    displayName = pickedSensor.name,
                                                    unit = formatSensor(pickedSensor).unit.ifBlank { null },
                                                    currentValue = pickedSensor.value,
                                            )
                                }
                            } else {
                                null
                            },
                    hasAlert = pickedSensor != null && sensorAlerts.ruleFor(pickedSensor.name) != null
            )
        }
    }

    alertTarget?.let { target ->
        SensorAlertEditorSheet(
                target = target,
                state = sensorAlerts,
                saveFailure = alertSaveFailure,
                onSave = { threshold, direction, severity ->
                    scope.launch {
                        val sent = onSetSensorAlert(target.sensorName, target.displayName, target.unit, threshold, direction, severity)
                        if (sent) {
                            alertTarget = null
                        } else {
                            alertSaveFailure = SensorAlertEditorLogic.failureReason(sensorAlerts, sensorAlerts.ruleFor(target.sensorName))
                        }
                    }
                },
                onRemove = {
                    onRemoveSensorAlert(target.sensorName)
                    alertTarget = null
                },
                onDismiss = { alertTarget = null },
        )
    }

    if (showAlertList) {
        SensorAlertsListSheet(
                state = sensorAlerts,
                pcAlertsEnabled = pcAlertsEnabled,
                onSetPcAlertsEnabled = onSetPcAlertsEnabled,
                onEdit = { rule ->
                    showAlertList = false
                    alertTarget = SensorAlertTarget(rule.sensorName, rule.displayName, rule.unit, rule.currentValue)
                },
                onRemove = { rule -> onRemoveSensorAlert(rule.sensorName) },
                onDismiss = { showAlertList = false },
        )
    }
}

/**
 * Lays [cards] out with [SensorGrid.pack] and draws each with [cardContent]. In edit mode a long
 * press lifts a card and dragging moves it: the card follows the finger, and whenever its centre
 * crosses another card the order changes and the grid re-packs around it. Outside edit mode the same
 * long press opens edit mode and lifts the card in one go.
 */
@Composable
private fun SensorGridLayout(
        cards: List<HomeCardState>,
        gridWidth: GridWidth,
        editMode: Boolean,
        draggingCardId: String?,
        onEnterEditMode: () -> Unit,
        onBeginDrag: (String) -> Boolean,
        onDragTo: (Int) -> Unit,
        onEndDrag: () -> Unit,
        cardContent: @Composable (card: HomeCardState, index: Int, isDragging: Boolean) -> Unit,
) {
    val haptics = rememberRemexHaptics()
    BoxWithConstraints(modifier = Modifier.fillMaxWidth()) {
        val density = LocalDensity.current
        val gutter = CardShapes.CARD_SPACING_DP.toFloat()
        val widthDp = maxWidth.value
        // The breakpoint is the window's width class; the grid itself sits inside 16dp margins.
        val columns = SensorGrid.columnsFor(widthDp + 32f, gridWidth)
        val cell = SensorGrid.cellWidth(widthDp, columns, gutter)
        val rowHeight = SensorGrid.rowHeight(cell)
        val placements = remember(cards, columns) { SensorGrid.pack(cards.map { it.id to it.span }, columns) }
        val gridHeight = SensorGrid.height(SensorGrid.rowCount(placements), rowHeight, gutter)

        // Read through the latest values from inside the long-lived gesture coroutines.
        val currentPlacements by rememberUpdatedState(placements)
        val currentCards by rememberUpdatedState(cards)
        val currentEditMode by rememberUpdatedState(editMode)
        // The cell size changes with the width (MainActivity handles rotation itself, so the gesture
        // coroutine below survives a 2 -> 4 column switch); reading the captured values made a drag
        // after rotating jump and drop at the wrong index (phase 3 review R1).
        val currentCell by rememberUpdatedState(cell)
        val currentRowHeight by rememberUpdatedState(rowHeight)
        // Where the lifted card's top-left is, in px, while it follows the finger.
        var dragTopLeft by remember { mutableStateOf<Offset?>(null) }

        Box(modifier = Modifier.fillMaxWidth().height(gridHeight.dp)) {
            cards.forEachIndexed { index, card ->
                val placement = placements.firstOrNull { it.id == card.id }
                if (placement != null) key(card.id) {
                    val isDragging = card.id == draggingCardId
                    val slot =
                            with(density) {
                                IntOffset(
                                        SensorGrid.x(placement.col, cell, gutter).dp.roundToPx(),
                                        SensorGrid.y(placement.row, rowHeight, gutter).dp.roundToPx()
                                )
                            }
                    val lifted = dragTopLeft?.takeIf { isDragging }
                    val target = lifted?.let { IntOffset(it.x.roundToInt(), it.y.roundToInt()) } ?: slot
                    val animated by
                            animateIntOffsetAsState(
                                    targetValue = target,
                                    animationSpec =
                                            if (lifted != null) snap<IntOffset>()
                                            else MaterialTheme.motionScheme.defaultSpatialSpec<IntOffset>(),
                                    label = "gridCardOffset"
                            )
                    Box(
                            modifier =
                                    Modifier.offset { animated }
                                            .size(
                                                    SensorGrid.extent(placement.colSpan, cell, gutter).dp,
                                                    SensorGrid.extent(placement.rowSpan, rowHeight, gutter).dp
                                            )
                                            .zIndex(if (isDragging) 1f else 0f)
                                            .pointerInput(card.id) {
                                                detectDragGesturesAfterLongPress(
                                                        onDragStart = {
                                                            // One long-press tick for whatever the hold did: entered
                                                            // edit mode, picked the card up, or both.
                                                            val entering = !currentEditMode
                                                            if (entering) onEnterEditMode()
                                                            val began = onBeginDrag(card.id)
                                                            if (entering || began) haptics.perform(RemexHapticEvent.LongPress)
                                                            if (began) {
                                                                val p = currentPlacements.first { it.id == card.id }
                                                                dragTopLeft =
                                                                        Offset(
                                                                                SensorGrid.x(p.col, currentCell, gutter).dp.toPx(),
                                                                                SensorGrid.y(p.row, currentRowHeight, gutter).dp.toPx()
                                                                        )
                                                            }
                                                        },
                                                        onDrag = { change, amount ->
                                                            val start = dragTopLeft ?: return@detectDragGesturesAfterLongPress
                                                            change.consume()
                                                            val moved = start + amount
                                                            dragTopLeft = moved
                                                            val p = currentPlacements.firstOrNull { it.id == card.id } ?: return@detectDragGesturesAfterLongPress
                                                            val cellNow = currentCell
                                                            val rowNow = currentRowHeight
                                                            val centreX = moved.x.toDp().value + SensorGrid.extent(p.colSpan, cellNow, gutter) / 2f
                                                            val centreY = moved.y.toDp().value + SensorGrid.extent(p.rowSpan, rowNow, gutter) / 2f
                                                            val over = SensorGrid.indexAt(currentPlacements, centreX, centreY, cellNow, rowNow, gutter)
                                                            val from = currentCards.indexOfFirst { it.id == card.id }
                                                            if (over != null && over != from) {
                                                                // The card took a new slot: a detent under the finger.
                                                                haptics.perform(RemexHapticEvent.Detent)
                                                                onDragTo(over)
                                                            }
                                                        },
                                                        onDragEnd = {
                                                            if (dragTopLeft != null) haptics.perform(RemexHapticEvent.Release)
                                                            dragTopLeft = null
                                                            onEndDrag()
                                                        },
                                                        onDragCancel = {
                                                            dragTopLeft = null
                                                            onEndDrag()
                                                        }
                                                )
                                            }
                    ) {
                        cardContent(card, index, isDragging)
                    }
                }
            }
        }
    }
}

@Composable
private fun NotConnectedBanner(onNavigateToConnection: () -> Unit) {
    Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh)
    ) {
        Row(modifier = Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                        stringResource(R.string.dashboard_not_connected),
                        style = MaterialTheme.typography.titleSmall,
                        color = MaterialTheme.colorScheme.onSurface
                )
                Text(
                        stringResource(R.string.dashboard_offline_body),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Spacer(Modifier.width(12.dp))
            Button(onClick = onNavigateToConnection, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) { Text(stringResource(R.string.button_connect)) }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CardDrawerSheet(
        availableCards: List<AvailableCardItem>,
        enabled: Set<String>,
        onSetCardEnabled: (String, Boolean) -> Unit,
        onDismiss: () -> Unit,
) {
    val haptics = rememberRemexHaptics()
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 16.dp)) {
            Text(stringResource(R.string.dashboard_card_drawer_title), style = MaterialTheme.typography.titleLarge)
            Text(
                    stringResource(R.string.dashboard_card_drawer_hint),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp, bottom = 8.dp)
            )
            Column(modifier = Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState())) {
                if (availableCards.isEmpty()) {
                    Text(
                            stringResource(R.string.dashboard_card_drawer_empty),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(vertical = 16.dp)
                    )
                }
                var lastGroup: String? = null
                availableCards.forEach { item ->
                    if (item.group != lastGroup) {
                        lastGroup = item.group
                        Text(
                                item.group,
                                style = MaterialTheme.typography.labelLarge,
                                color = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.padding(top = 12.dp, bottom = 4.dp)
                        )
                    }
                    val checked = item.id in enabled
                    Row(
                            modifier =
                                    Modifier.fillMaxWidth()
                                            .heightIn(min = 48.dp)
                                            .toggleable(
                                                    value = checked,
                                                    role = Role.Checkbox,
                                                    onValueChange = { on ->
                                                        haptics.perform(RemexHaptics.toggle(on))
                                                        onSetCardEnabled(item.id, on)
                                                    }
                                            ),
                            verticalAlignment = Alignment.CenterVertically
                    ) {
                        Checkbox(checked = checked, onCheckedChange = null)
                        Spacer(Modifier.width(12.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(item.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                    item.subtitle,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }
            }
            Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth().padding(top = 12.dp), shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) {
                Text(stringResource(R.string.button_done))
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun DashboardScreenPreview() {
    RemExTheme {
        DashboardScreenContent(
                isConnected = true,
                telemetrySensors =
                        listOf(
                                TelemetrySensor("sensor:cpu", "CPU Usage", "CPU", 25.0, "%"),
                                TelemetrySensor("sensor:ram", "RAM Usage", "Memory", 60.0, "%")
                        ),
                telemetryHistory = emptyMap(),
                layout =
                        SensorLayout(
                                cards =
                                        listOf(
                                                HomeCardState("sensor:cpu", "CPU", HomeCardType.TELEMETRY, "sensor:cpu"),
                                                HomeCardState("sensor:ram", "RAM", HomeCardType.TELEMETRY, "sensor:ram")
                                        ),
                                enabled = setOf("sensor:cpu", "sensor:ram")
                        ),
                editMode = false,
                draggingCardId = null,
                homePins = HomePinsState(),
                cornerRadius = 16,
                cardOpacity = 1f,
                shapeIndexFor = { CardShapes.ROUNDED_RECTANGLE },
                canUndo = false,
                canRedo = false,
                onNavigateToConnection = {},
                onEnterEditMode = {},
                onExitEditMode = {},
                onBeginDrag = { false },
                onDragTo = {},
                onEndDrag = {},
                onMoveCard = { _, _ -> },
                onCycleSpan = {},
                onRemoveCard = {},
                onUndo = {},
                onRedo = {},
                onClearAllCards = {},
                onSetCardEnabled = { _, _ -> },
                onSetHomePin = { _, _ -> },
                onPickDisplayMode = { _, _, _ -> },
                onSetCardTitle = { _, _ -> },
                onSetValueOverlay = { _, _ -> },
        )
    }
}

/**
 * Horizontal intrusion (px from each card edge) of the clipping shape into the title row's
 * vertical band (RemEx-0ukf). Measured against the same path the card clips with, at the top
 * and bottom of the band, worst case per side — so a title inset derived from this is exact
 * for every shape and morph progress, including resized cards, with no per-shape constants.
 * Returns 0f to 0f for the discrete rounded-rectangle preset (no polygon clipping).
 */
private fun titleBandIntrusion(
        shapePreset: Float,
        cardWidthPx: Int,
        cardHeightPx: Int,
        bandTopPx: Float,
        bandBottomPx: Float,
        density: Density
): Pair<Float, Float> {
    // Polygons are already confined to their safe rectangle (shapeSafeArea), so only the cut corner
    // could intrude on the title band; it sits at the top of the range and measures zero here today.
    if (shapePreset >= 23.5f || CardShapes.isPolygon(shapePreset) || cardWidthPx <= 0 || cardHeightPx <= 0) return 0f to 0f
    val outline =
            cardShape(shapePreset, 0)
                    .createOutline(GeoSize(cardWidthPx.toFloat(), cardHeightPx.toFloat()), LayoutDirection.Ltr, density)
    val path = (outline as? Outline.Generic)?.path ?: return 0f to 0f
    val shapeRegion = android.graphics.Region()
    shapeRegion.setPath(path.asAndroidPath(), android.graphics.Region(0, 0, cardWidthPx, cardHeightPx))
    var left = 0f
    var right = 0f
    for (y in floatArrayOf(bandTopPx, bandBottomPx)) {
        val yTop = y.toInt().coerceIn(0, cardHeightPx - 1)
        val slice = android.graphics.Region(0, yTop, cardWidthPx, yTop + 1)
        slice.op(shapeRegion, android.graphics.Region.Op.INTERSECT)
        if (slice.isEmpty) continue
        val b = slice.bounds
        left = maxOf(left, b.left.toFloat())
        right = maxOf(right, (cardWidthPx - b.right).toFloat())
    }
    return left to right
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun TelemetryCardContent(
        title: String,
        sensor: TelemetrySensor?,
        history: List<Float>,
        mode: TelemetryDisplayMode,
        secondarySensor: TelemetrySensor?,
        secondaryHistory: List<Float>,
        shapeIndex: Float,
        showValueOverlay: Boolean,
        selectionActive: Boolean,
        hasAlert: Boolean,
        onOpenPicker: () -> Unit
) {
    val dynamicPadding = cardInnerPadding()
    // Same category -> colour rule as the PC (SensorAccents mirrors SensorFamilies.For).
    val scheme = MaterialTheme.colorScheme
    val accent = SensorAccents.accentFor(sensor?.kind ?: MetricKind.UNKNOWN, scheme)
    val density = LocalDensity.current
    val titleLineHeight = MaterialTheme.typography.labelSmall.lineHeight

    // A polygon shape (Slanted, Clover...) confines the content to its measured safe rectangle
    // (RemEx-pp4cm.14); the rounded rectangle and cut corner use the whole tile.
    BoxWithConstraints(modifier = Modifier.fillMaxSize().shapeSafeArea(shapeIndex)) {
        val contentWidthPx = constraints.maxWidth
        val contentHeightPx = constraints.maxHeight
        // Extra start/end inset for the title row (RemEx-0ukf): the uniform adaptive padding is
        // measured at the card's widest band, but a cut corner intrudes further at the title row's
        // height near the top. Measure the real intrusion at the title band against the clip path and
        // pad only what the uniform inset doesn't cover. Rounded rectangles measure zero.
        val (titleExtraStart, titleExtraEnd) =
                remember(shapeIndex, contentWidthPx, contentHeightPx, dynamicPadding, titleLineHeight, density) {
                    with(density) {
                        // SensorGridCard's interior Box adds a 4dp frame around this content, so the
                        // clip shape spans 8dp more than our constraints in each dimension.
                        val framePx = 4.dp.toPx()
                        val paddingPx = dynamicPadding.toPx()
                        val rowHeightPx = maxOf(24.dp.toPx(), titleLineHeight.toPx())
                        val bandTop = framePx + paddingPx
                        val (leftPx, rightPx) =
                                titleBandIntrusion(
                                        shapePreset = shapeIndex,
                                        cardWidthPx = (contentWidthPx + 2 * framePx).roundToInt(),
                                        cardHeightPx = (contentHeightPx + 2 * framePx).roundToInt(),
                                        bandTopPx = bandTop,
                                        bandBottomPx = bandTop + rowHeightPx,
                                        density = density
                                )
                        val alreadyInset = framePx + paddingPx
                        (leftPx - alreadyInset).coerceAtLeast(0f).toDp() to (rightPx - alreadyInset).coerceAtLeast(0f).toDp()
                    }
                }

        Column(
                modifier = Modifier.fillMaxSize().padding(dynamicPadding),
                verticalArrangement = Arrangement.SpaceBetween,
                horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Row(
                    modifier = Modifier.fillMaxWidth().padding(start = titleExtraStart, end = titleExtraEnd),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
            ) {
                // Label chip, top-left, in the sensor's family colours - the PC's card-pill +
                // sensor-title (SensorCardContent.axaml), RemEx-kq10x.5.
                Box(modifier = Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
                    Text(
                            title,
                            style = MaterialTheme.typography.labelMedium,
                            color = accent.onChip,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier =
                                    Modifier.clip(RoundedCornerShape(6.dp))
                                            .background(accent.chip)
                                            .padding(horizontal = 6.dp, vertical = 2.dp)
                    )
                }
                // A small bell on a sensor the PC watches with an alert rule (RemEx-pp4cm.12). Always shown,
                // edit mode included: it is a status, not a control.
                if (hasAlert) {
                    Icon(
                            Icons.Filled.Notifications,
                            contentDescription = stringResource(R.string.cd_sensor_alert_bell),
                            tint = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.padding(horizontal = 4.dp).size(14.dp)
                    )
                }
                // Hidden in edit mode: a card has one set of controls at a time.
                if (!selectionActive) {
                    IconButton(onClick = onOpenPicker, modifier = Modifier.size(24.dp), shapes = rememberRemexIconButtonShapes()) {
                        Icon(
                                Icons.Default.GridView,
                                contentDescription = stringResource(R.string.cd_open_view_picker),
                                modifier = Modifier.size(16.dp)
                        )
                    }
                }
            }

            // The telemetry views paint their value and sparkline/gauge in `primary`, so the family
            // accent reaches all of them by re-pointing primary for this subtree only. Every other
            // role, the type scale and the shapes are inherited unchanged.
            MaterialTheme(colorScheme = scheme.copy(primary = accent.series)) {
                TelemetryViewDispatch(
                        mode = mode,
                        sensor = sensor,
                        history = history,
                        secondarySensor = secondarySensor,
                        secondaryHistory = secondaryHistory,
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        showValueOverlay = showValueOverlay
                )
            }
        }
    }
}
