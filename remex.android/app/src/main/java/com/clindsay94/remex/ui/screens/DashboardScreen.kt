package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.rememberTransformableState
import androidx.compose.foundation.gestures.transformable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Redo
import androidx.compose.material.icons.automirrored.filled.Undo
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.FilterCenterFocus
import androidx.compose.material.icons.filled.HelpOutline
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.LockOpen
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.OpenInFull
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FloatingActionButtonMenu
import androidx.compose.material3.FloatingActionButtonMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ToggleFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.material3.SheetValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius as GeoCornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size as GeoSize
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexScreenHeader
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding
import com.clindsay94.remex.ui.theme.cardShape
import com.clindsay94.remex.ui.theme.materialShapesList
import kotlin.math.roundToInt
import kotlinx.coroutines.delay

// Wrapper that isolates AnimatedVisibility from outer ColumnScope receiver.
@Composable
private fun PlainAnimatedVisibility(
        visible: Boolean,
        modifier: Modifier = Modifier,
        content: @Composable AnimatedVisibilityScope.() -> Unit
) {
        AnimatedVisibility(visible = visible, modifier = modifier, content = content)
}

private data class AvailableCardItem(val id: String, val title: String, val subtitle: String, val group: String = "")

private data class CardSizeDp(val widthDp: Float, val heightDp: Float)

private fun defaultCardSizeFor(id: String): CardSizeDp {
        return if (id == "pc_status") CardSizeDp(200f, 150f) else CardSizeDp(150f, 150f)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
        viewModel: DashboardViewModel = viewModel(),
        onNavigateToConnection: () -> Unit = {}
) {
        val isConnected by viewModel.isConnected.collectAsStateWithLifecycle()
        val isConnecting by viewModel.isConnecting.collectAsStateWithLifecycle()
        val telemetrySensors by viewModel.telemetrySensors.collectAsStateWithLifecycle()
        val telemetryHistory by viewModel.telemetryHistory.collectAsStateWithLifecycle()
        val cards by viewModel.homeCards.collectAsStateWithLifecycle()
        val enabledCards by viewModel.enabledCardIds.collectAsStateWithLifecycle()
        val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
        val cardOpacity by viewModel.cardOpacity.collectAsStateWithLifecycle()
        val pcCardShapePreset by viewModel.pcCardShapePreset.collectAsStateWithLifecycle()
        val telemetryCardShapePreset by viewModel.telemetryCardShapePreset.collectAsStateWithLifecycle()
        val canUndo by viewModel.canUndo.collectAsStateWithLifecycle()
        val canRedo by viewModel.canRedo.collectAsStateWithLifecycle()
        val draggingCardId by viewModel.draggingCardId.collectAsStateWithLifecycle()
        val selectedCardIds by viewModel.selectedCardIds.collectAsStateWithLifecycle()
        val coachStep by viewModel.coachStep.collectAsStateWithLifecycle()
        // Live on-screen centres of the ⋮ menu and + button, captured via onGloballyPositioned so the
        // coach pointers land exactly on them regardless of device insets (RemEx-km0i.10).
        var menuAnchor by remember { mutableStateOf(Offset.Zero) }
        var addAnchor by remember { mutableStateOf(Offset.Zero) }

        val snackbarHostState = remember { androidx.compose.material3.SnackbarHostState() }

        LaunchedEffect(Unit) {
                viewModel.wakeStatus.collect { message ->
                        snackbarHostState.showSnackbar(message, duration = androidx.compose.material3.SnackbarDuration.Short)
                }
        }

        Box(modifier = Modifier.fillMaxSize()) {
                DashboardScreenContent(
                        isConnected = isConnected,
                        isConnecting = isConnecting,
                        telemetrySensors = telemetrySensors,
                        telemetryHistory = telemetryHistory,
                        cards = cards,
                        enabledCards = enabledCards,
                        cornerRadius = cornerRadius,
                        cardOpacity = cardOpacity,
                        pcCardShapePreset = pcCardShapePreset,
                        telemetryCardShapePreset = telemetryCardShapePreset,
                        draggingCardId = draggingCardId,
                        selectedCardIds = selectedCardIds,
                        onNavigateToConnection = onNavigateToConnection,
                        onResizeCard = { cardId, dw, dh -> viewModel.resizeCard(cardId, dw, dh) },
                        onSaveCardLayout = { viewModel.saveCardLayout() },
                        onToggleConnection = { viewModel.toggleConnection() },
                        onWakePc = { viewModel.wakePc() },
                        onPickDisplayMode = { cardId, mode, secondarySensorId ->
                                viewModel.setTelemetryDisplayMode(cardId, mode, secondarySensorId)
                        },
                        onSetCardTitle = { cardId, title -> viewModel.setCardCustomTitle(cardId, title) },
                        onSetValueOverlay = { cardId, enabled -> viewModel.setCardValueOverlay(cardId, enabled) },
                        onPlaceCardAt = { cardId, x, y -> viewModel.placeCardAt(cardId, x, y) },
                        onSetCardEnabled = { cardId, enabled -> viewModel.setCardEnabled(cardId, enabled) },
                        onBeginCardDrag = { cardId -> viewModel.beginCardDrag(cardId) },
                        onDragCardBy = { dx, dy -> viewModel.dragCardBy(dx, dy) },
                        onEndCardDrag = { viewModel.endCardDrag() },
                        onSelectCard = { cardId -> viewModel.selectCard(cardId) },
                        onToggleCardSelection = { cardId -> viewModel.toggleCardInSelection(cardId) },
                        onMoveSelection = { dx, dy -> viewModel.moveSelection(dx, dy) },
                        onTogglePinSelection = { viewModel.togglePinSelection() },
                        onRemoveSelection = { viewModel.removeSelection() },
                        onClearSelection = { viewModel.clearSelection() },
                        onSetGroupShape = { cardIds, idx -> viewModel.setGroupShape(cardIds, idx) },
                        canUndo = canUndo,
                        canRedo = canRedo,
                        onUndo = { viewModel.undo() },
                        onRedo = { viewModel.redo() },
                        onBeginInteraction = { viewModel.beginInteraction() },
                        onTogglePin = { cardId -> viewModel.togglePin(cardId) },
                        onClearAllCards = { viewModel.clearAllCards() },
                        onReplayCoach = { viewModel.replayCoach() },
                        onMenuAnchor = { menuAnchor = it },
                        onAddAnchor = { addAnchor = it }
                )
                androidx.compose.material3.SnackbarHost(
                        hostState = snackbarHostState,
                        modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding()
                )
                // First-run coach marks (RemEx-km0i.10), mounted last = top of z-order. Suppressed while a
                // card is lifted or a group is selected (locked decision #7); the scrim blocks input, so
                // no bottom sheet can open underneath a live hint.
                if (coachStep >= 0 && draggingCardId == null && selectedCardIds.isEmpty()) {
                        DashboardCoachOverlay(
                                step = coachStep,
                                menuAnchor = menuAnchor,
                                addAnchor = addAnchor,
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
        isConnecting: Boolean,
        telemetrySensors: List<TelemetrySensor>,
        telemetryHistory: Map<String, List<Float>>,
        cards: List<HomeCardState>,
        enabledCards: Set<String>,
        cornerRadius: Int,
        cardOpacity: Float,
        pcCardShapePreset: Float,
        telemetryCardShapePreset: Float,
        draggingCardId: String?,
        selectedCardIds: Set<String>,
        onNavigateToConnection: () -> Unit,
        onResizeCard: (String, Float, Float) -> Unit,
        onSaveCardLayout: () -> Unit,
        onToggleConnection: () -> Unit,
        onWakePc: () -> Unit,
        onPickDisplayMode: (String, TelemetryDisplayMode, String?) -> Unit,
        onSetCardTitle: (String, String?) -> Unit,
        onSetValueOverlay: (String, Boolean) -> Unit,
        onPlaceCardAt: (String, Float, Float) -> Unit,
        onSetCardEnabled: (String, Boolean) -> Unit,
        onBeginCardDrag: (String) -> Unit,
        onDragCardBy: (Float, Float) -> Unit,
        onEndCardDrag: () -> Unit,
        onSelectCard: (String) -> Unit,
        onToggleCardSelection: (String) -> Unit,
        onMoveSelection: (Float, Float) -> Unit,
        onTogglePinSelection: () -> Unit,
        onRemoveSelection: () -> Unit,
        onClearSelection: () -> Unit,
        onSetGroupShape: (Set<String>, Float) -> Unit,
        canUndo: Boolean = false,
        canRedo: Boolean = false,
        onUndo: () -> Unit = {},
        onRedo: () -> Unit = {},
        onBeginInteraction: () -> Unit = {},
        onTogglePin: (String) -> Unit = {},
        onClearAllCards: () -> Unit = {},
        onReplayCoach: () -> Unit = {},
        onMenuAnchor: (Offset) -> Unit = {},
        onAddAnchor: (Offset) -> Unit = {}
) {
        val view = LocalView.current

        val pcStatusTitle = stringResource(R.string.dashboard_pc_status)
        val pcStatusSubtitle = stringResource(R.string.dashboard_pc_status_subtitle)
        val wakePcTitle = stringResource(R.string.dashboard_wake_pc)
        val wakePcSubtitle = stringResource(R.string.dashboard_wake_pc_subtitle)
        val telemetryFallback = stringResource(R.string.dashboard_telemetry_fallback)

        val availableCards =
                remember(telemetrySensors, pcStatusTitle, wakePcTitle, telemetryFallback) {
                        buildList {
                                add(AvailableCardItem("pc_status", pcStatusTitle, pcStatusSubtitle))
                                add(AvailableCardItem("wake_pc", wakePcTitle, wakePcSubtitle))
                                telemetrySensors.forEach { sensor ->
                                        add(
                                                AvailableCardItem(
                                                        id = sensor.id,
                                                        title = sensor.name,
                                                        subtitle =
                                                                sensor.category.ifBlank {
                                                                        telemetryFallback
                                                                },
                                                        group =
                                                                sensor.group.ifBlank {
                                                                        sensor.category.ifBlank {
                                                                                telemetryFallback
                                                                        }
                                                                }
                                                )
                                        )
                                }
                        }
                                .distinctBy { it.id }
                                .sortedBy { it.group }
                }

        val density = androidx.compose.ui.platform.LocalDensity.current.density

        var showCardDrawer by remember { mutableStateOf(false) }
        var pickerCardId by remember { mutableStateOf<String?>(null) }
        val onOpenViewPicker: (String) -> Unit = { cardId -> pickerCardId = cardId }
        var layoutLocked by rememberSaveable { mutableStateOf(false) }
        var canvasMenuOpen by remember { mutableStateOf(false) }
        var canvasTopLeftPx by remember { mutableStateOf(Offset.Zero) }
        var drawerLeftPx by remember { mutableFloatStateOf(Float.MAX_VALUE) }
        var draggingCardId by remember { mutableStateOf<String?>(null) }
        var draggingPointerPx by remember { mutableStateOf(Offset.Zero) }

        val canvasScaleState = remember { mutableFloatStateOf(1f) }
        var canvasScale by canvasScaleState
        @Suppress("DEPRECATION")
        val transformableState = rememberTransformableState { zoomChange, _, _ ->
                canvasScale = (canvasScale * zoomChange).coerceIn(0.4f, 3f)
        }

        val draggingCard = availableCards.firstOrNull { it.id == draggingCardId }
        val draggingCardSize =
                remember(draggingCard?.id, cards) {
                        draggingCard?.let { item ->
                                cards.firstOrNull { it.id == item.id }?.let { card ->
                                        CardSizeDp(card.widthDp, card.heightDp)
                                }
                                        ?: defaultCardSizeFor(item.id)
                        }
                }
        val canDropOnCanvas = draggingCardId != null && draggingPointerPx.x < drawerLeftPx - 24f
        val dragPointerCanvasPx =
                Offset(
                        x = draggingPointerPx.x - canvasTopLeftPx.x,
                        y = draggingPointerPx.y - canvasTopLeftPx.y
                )
        val dropTargetXDp =
                draggingCardSize?.let { size ->
                        ((dragPointerCanvasPx.x / (density * canvasScaleState.floatValue)) -
                                        (size.widthDp / 2f))
                                .coerceAtLeast(0f)
                }
                        ?: 0f
        val dropTargetYDp =
                draggingCardSize?.let { size ->
                        ((dragPointerCanvasPx.y / (density * canvasScaleState.floatValue)) -
                                        (size.heightDp / 2f))
                                .coerceAtLeast(0f)
                }
                        ?: 0f

        val visibleCards = cards.filter { enabledCards.contains(it.id) }

        val canvasWidthDp =
                remember(visibleCards) {
                        val maxRight = visibleCards.maxOfOrNull { it.xDp + it.widthDp } ?: 0f
                        (maxRight + 200f).coerceAtLeast(800f)
                }
        val canvasHeightDp =
                remember(visibleCards) {
                        val maxBottom = visibleCards.maxOfOrNull { it.yDp + it.heightDp } ?: 0f
                        (maxBottom + 200f).coerceAtLeast(1200f)
                }

        val hScrollState = rememberScrollState()
        val vScrollState = rememberScrollState()

        Surface(modifier = Modifier.fillMaxSize()) {
                Column(modifier = Modifier.fillMaxSize()) {
                        RemexScreenHeader(
                                title = stringResource(R.string.screen_dashboard_title),
                                actions = {
                                        // Replay the first-run coach marks anytime; disabled mid-gesture
                                        // so it never fights an active lift/selection (RemEx-km0i.10).
                                        IconButton(
                                                onClick = onReplayCoach,
                                                enabled = selectedCardIds.isEmpty(),
                                        ) {
                                                Icon(
                                                        Icons.Filled.HelpOutline,
                                                        contentDescription = stringResource(R.string.coach_replay),
                                                )
                                        }
                                        Box {
                                                IconButton(
                                                        modifier = Modifier.onGloballyPositioned {
                                                                onMenuAnchor(it.boundsInRoot().center)
                                                        },
                                                        onClick = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                                )
                                                                canvasMenuOpen = true
                                                        },
                                                ) {
                                                        Icon(
                                                                Icons.Default.MoreVert,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string
                                                                                        .dashboard_menu_options
                                                                        )
                                                        )
                                                }
                                                // M3 Expressive overflow menu — canvas management.
                                                DropdownMenu(
                                                        expanded = canvasMenuOpen,
                                                        onDismissRequest = { canvasMenuOpen = false }
                                                ) {
                                                        DropdownMenuItem(
                                                                text = {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .dashboard_menu_undo
                                                                                )
                                                                        )
                                                                },
                                                                leadingIcon = {
                                                                        Icon(
                                                                                Icons.AutoMirrored
                                                                                        .Filled.Undo,
                                                                                contentDescription =
                                                                                        null
                                                                        )
                                                                },
                                                                enabled = canUndo,
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .KEYBOARD_TAP
                                                                        )
                                                                        canvasMenuOpen = false
                                                                        onUndo()
                                                                }
                                                        )
                                                        DropdownMenuItem(
                                                                text = {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .dashboard_menu_redo
                                                                                )
                                                                        )
                                                                },
                                                                leadingIcon = {
                                                                        Icon(
                                                                                Icons.AutoMirrored
                                                                                        .Filled.Redo,
                                                                                contentDescription =
                                                                                        null
                                                                        )
                                                                },
                                                                enabled = canRedo,
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .KEYBOARD_TAP
                                                                        )
                                                                        canvasMenuOpen = false
                                                                        onRedo()
                                                                }
                                                        )
                                                        HorizontalDivider()
                                                        DropdownMenuItem(
                                                                text = {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_customize_cards
                                                                                )
                                                                        )
                                                                },
                                                                leadingIcon = {
                                                                        Icon(
                                                                                Icons.Default.Tune,
                                                                                contentDescription =
                                                                                        null
                                                                        )
                                                                },
                                                                onClick = {
                                                                        canvasMenuOpen = false
                                                                        showCardDrawer = true
                                                                }
                                                        )
                                                        DropdownMenuItem(
                                                                text = {
                                                                        Text(
                                                                                stringResource(
                                                                                        if (layoutLocked)
                                                                                                R.string
                                                                                                        .dashboard_menu_unlock_layout
                                                                                        else
                                                                                                R.string
                                                                                                        .dashboard_menu_lock_layout
                                                                                )
                                                                        )
                                                                },
                                                                leadingIcon = {
                                                                        Icon(
                                                                                if (layoutLocked)
                                                                                        Icons.Default
                                                                                                .LockOpen
                                                                                else
                                                                                        Icons.Default
                                                                                                .Lock,
                                                                                contentDescription =
                                                                                        null
                                                                        )
                                                                },
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .CONFIRM
                                                                        )
                                                                        canvasMenuOpen = false
                                                                        layoutLocked = !layoutLocked
                                                                }
                                                        )
                                                        DropdownMenuItem(
                                                                text = {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_reset_zoom
                                                                                )
                                                                        )
                                                                },
                                                                leadingIcon = {
                                                                        Icon(
                                                                                Icons.Default
                                                                                        .FilterCenterFocus,
                                                                                contentDescription =
                                                                                        null
                                                                        )
                                                                },
                                                                onClick = {
                                                                        canvasMenuOpen = false
                                                                        canvasScale = 1f
                                                                }
                                                        )
                                                        HorizontalDivider()
                                                        DropdownMenuItem(
                                                                text = {
                                                                        Text(
                                                                                stringResource(
                                                                                        R.string
                                                                                                .dashboard_menu_clear_cards
                                                                                )
                                                                        )
                                                                },
                                                                leadingIcon = {
                                                                        Icon(
                                                                                Icons.Default
                                                                                        .DeleteSweep,
                                                                                contentDescription =
                                                                                        null,
                                                                                tint =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .error
                                                                        )
                                                                },
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .CONFIRM
                                                                        )
                                                                        canvasMenuOpen = false
                                                                        onClearAllCards()
                                                                }
                                                        )
                                                }
                                        }
                                }
                        )
                        Box(
                                modifier =
                                        Modifier.fillMaxSize()
                                                .transformable(state = transformableState)
                        ) {
                                Box(
                                        modifier =
                                                Modifier.fillMaxSize()
                                                        .horizontalScroll(hScrollState)
                                                        .verticalScroll(vScrollState)
                                ) {
                                        Box(
                                                modifier =
                                                        Modifier.width(canvasWidthDp.dp)
                                                                .height(canvasHeightDp.dp)
                                                                .onGloballyPositioned {
                                                                        canvasTopLeftPx =
                                                                                it.positionInRoot()
                                                                }
                                                                .background(
                                                                        MaterialTheme.colorScheme
                                                                                .background
                                                                )
                                                                .graphicsLayer {
                                                                        scaleX =
                                                                                canvasScaleState
                                                                                        .floatValue
                                                                        scaleY =
                                                                                canvasScaleState
                                                                                        .floatValue
                                                                        transformOrigin =
                                                                                androidx.compose.ui
                                                                                        .graphics
                                                                                        .TransformOrigin(
                                                                                                0f,
                                                                                                0f
                                                                                        )
                                                                }
                                        ) {
                                                visibleCards.forEach { card ->
                                                        val xPx = (card.xDp * density).roundToInt()
                                                        val yPx = (card.yDp * density).roundToInt()
                                                        val cardShapePreset =
                                                                DashboardShapes.resolveShapeIndex(
                                                                        card,
                                                                        pcCardShapePreset,
                                                                        telemetryCardShapePreset
                                                                )

                                                        DraggableDashboardCard(
                                                                card = card,
                                                                selectionActive = selectedCardIds.isNotEmpty(),
                                                                isSelected = card.id in selectedCardIds,
                                                                isDragging = card.id == draggingCardId,
                                                                layoutLocked = layoutLocked,
                                                                density = density,
                                                                cornerRadius = cornerRadius,
                                                                cardOpacity = cardOpacity,
                                                                shapeIndex = cardShapePreset,
                                                                canvasScale = { canvasScaleState.floatValue },
                                                                onBeginCardDrag = onBeginCardDrag,
                                                                onDragCardBy = onDragCardBy,
                                                                onEndCardDrag = onEndCardDrag,
                                                                onSelectCard = onSelectCard,
                                                                onToggleSelect = onToggleCardSelection,
                                                                onBeginInteraction = onBeginInteraction,
                                                                onMoveSelection = onMoveSelection,
                                                                onSaveLayout = onSaveCardLayout,
                                                                onTogglePin = onTogglePin,
                                                                onResize = onResizeCard,
                                                                modifier =
                                                                        Modifier.offset {
                                                                                IntOffset(xPx, yPx)
                                                                        }
                                                                                .width(card.widthDp.dp)
                                                                                .height(card.heightDp.dp)
                                                        ) {
                                                                when (card.type.name) {
                                                                        "PC_STATUS" -> {
                                                                                ConnectionOrbCard(
                                                                                        isConnected =
                                                                                                isConnected,
                                                                                        isConnecting =
                                                                                                isConnecting,
                                                                                        shapePreset =
                                                                                                pcCardShapePreset,
                                                                                        cornerRadius =
                                                                                                cornerRadius,
                                                                                        onToggle = {
                                                                                                view.performHapticFeedback(
                                                                                                        HapticFeedbackConstants
                                                                                                                .CONFIRM
                                                                                                )
                                                                                                onToggleConnection()
                                                                                        },
                                                                                        onNavigateToConnection =
                                                                                                onNavigateToConnection
                                                                                )
                                                                        }
                                                                        "WAKE_ON_LAN" -> {
                                                                                WakeOnLanCard(
                                                                                        onWake = {
                                                                                                onWakePc()
                                                                                        }
                                                                                )
                                                                        }
                                                                        "TELEMETRY" -> {
                                                                                val sensor =
                                                                                        selectSensor(card.sensorId, telemetrySensors)
                                                                                // Keyed by the RESOLVED sensor's own id, not the card's declared
                                                                                // sensorId - curated ids without a stable cardSlug (e.g.
                                                                                // sensor:cputemp) resolve to a different real host id, and
                                                                                // telemetryHistory is populated keyed by that real id.
                                                                                val history =
                                                                                        telemetryHistory[
                                                                                                        sensor?.id]
                                                                                                .orEmpty()
                                                                                val secondarySensor =
                                                                                        selectSensor(card.secondarySensorId, telemetrySensors)
                                                                                val secondaryHistory =
                                                                                        telemetryHistory[
                                                                                                        secondarySensor?.id]
                                                                                                .orEmpty()
                                                                                TelemetryCardContent(
                                                                                        title =
                                                                                                card.customTitle?.takeIf { it.isNotBlank() }
                                                                                                        ?: card.title,
                                                                                        sensor =
                                                                                                sensor,
                                                                                        history =
                                                                                                history,
                                                                                        mode =
                                                                                                card.displayMode,
                                                                                        secondarySensor =
                                                                                                secondarySensor,
                                                                                        secondaryHistory =
                                                                                                secondaryHistory,
                                                                                        shapeIndex =
                                                                                                cardShapePreset,
                                                                                        showValueOverlay =
                                                                                                card.showValueOverlay,
                                                                                        selectionActive =
                                                                                                selectedCardIds.isNotEmpty(),
                                                                                        onOpenPicker = {
                                                                                                onOpenViewPicker(
                                                                                                        card.id
                                                                                                )
                                                                                        }
                                                                                )
                                                                        }
                                                                }
                                                        }
                                                }
                                        }
                                }

                                if (showCardDrawer) {
                                        ModalBottomSheet(
                                                onDismissRequest = { showCardDrawer = false },
                                                sheetState =
                                                        rememberBottomSheetState(
                                                                initialValue = SheetValue.Hidden,
                                                                enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded)
                                                        )
                                        ) {
                                                Column(
                                                        modifier =
                                                                Modifier.fillMaxWidth()
                                                                        .padding(
                                                                                horizontal = 12.dp,
                                                                                vertical = 8.dp
                                                                        )
                                                ) {
                                                        Text(
                                                                text =
                                                                        stringResource(
                                                                                R.string
                                                                                        .dashboard_card_drawer_title
                                                                        ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleLarge,
                                                                fontWeight = FontWeight.Bold
                                                        )
                                                        Text(
                                                                text =
                                                                        stringResource(
                                                                                R.string
                                                                                        .dashboard_card_drawer_hint
                                                                        ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodySmall,
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant,
                                                                modifier =
                                                                        Modifier.padding(
                                                                                top = 4.dp,
                                                                                bottom = 12.dp
                                                                        )
                                                        )

                                                        val drawerScrollState =
                                                                rememberScrollState()
                                                        val scrollbarColor =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant.copy(
                                                                        alpha = 0.4f
                                                                )
                                                        val scrollbarTrackColor =
                                                                MaterialTheme.colorScheme
                                                                        .surfaceVariant.copy(
                                                                        alpha = 0.3f
                                                                )

                                                        Box(
                                                                modifier =
                                                                        Modifier.heightIn(
                                                                                max = 400.dp
                                                                        )
                                                        ) {
                                                                Column(
                                                                        modifier =
                                                                                Modifier.fillMaxSize()
                                                                                        .padding(
                                                                                                end =
                                                                                                        12.dp
                                                                                        )
                                                                                        .verticalScroll(
                                                                                                drawerScrollState
                                                                                        ),
                                                                        verticalArrangement =
                                                                                Arrangement
                                                                                        .spacedBy(
                                                                                                8.dp
                                                                                        )
                                                                ) {
                                                                        var lastDrawerGroup: String? = null
                                                                        availableCards.forEach {
                                                                                availableCard ->
                                                                                if (availableCard.group.isNotBlank() && availableCard.group != lastDrawerGroup) {
                                                                                        lastDrawerGroup = availableCard.group
                                                                                        Text(
                                                                                                text = availableCard.group,
                                                                                                style = MaterialTheme.typography.labelLarge,
                                                                                                fontWeight = FontWeight.Bold,
                                                                                                color = MaterialTheme.colorScheme.primary,
                                                                                                modifier = Modifier.padding(top = 12.dp, bottom = 2.dp, start = 4.dp)
                                                                                        )
                                                                                }
                                                                                var itemTopLeftPx by
                                                                                        remember(
                                                                                                availableCard
                                                                                                        .id
                                                                                        ) {
                                                                                                mutableStateOf(
                                                                                                        Offset.Zero
                                                                                                )
                                                                                        }
                                                                                Card(
                                                                                        modifier =
                                                                                                Modifier.fillMaxWidth()
                                                                                                        .onGloballyPositioned {
                                                                                                                itemTopLeftPx =
                                                                                                                        it.positionInRoot()
                                                                                                        }
                                                                                                        .pointerInput(
                                                                                                                availableCard
                                                                                                                        .id,
                                                                                                                density,
                                                                                                                drawerLeftPx,
                                                                                                                canvasTopLeftPx,
                                                                                                                cards
                                                                                                        ) {
                                                                                                                detectDragGesturesAfterLongPress(
                                                                                                                        onDragStart = {
                                                                                                                                startOffset
                                                                                                                                ->
                                                                                                                                view.performHapticFeedback(
                                                                                                                                        HapticFeedbackConstants
                                                                                                                                                .LONG_PRESS
                                                                                                                                )
                                                                                                                                draggingCardId =
                                                                                                                                        availableCard
                                                                                                                                                .id
                                                                                                                                draggingPointerPx =
                                                                                                                                        Offset(
                                                                                                                                                x =
                                                                                                                                                        itemTopLeftPx
                                                                                                                                                                .x +
                                                                                                                                                                startOffset
                                                                                                                                                                        .x,
                                                                                                                                                y =
                                                                                                                                                        itemTopLeftPx
                                                                                                                                                                .y +
                                                                                                                                                                startOffset
                                                                                                                                                                        .y
                                                                                                                                        )
                                                                                                                        },
                                                                                                                        onDragEnd = {
                                                                                                                                val draggingId =
                                                                                                                                        draggingCardId
                                                                                                                                if (draggingId ==
                                                                                                                                                availableCard
                                                                                                                                                        .id &&
                                                                                                                                                draggingPointerPx
                                                                                                                                                        .x <
                                                                                                                                                        drawerLeftPx -
                                                                                                                                                                24f
                                                                                                                                ) {
                                                                                                                                        val dragSize =
                                                                                                                                                cards
                                                                                                                                                        .firstOrNull {
                                                                                                                                                                it.id ==
                                                                                                                                                                        draggingId
                                                                                                                                                        }
                                                                                                                                                        ?.let {
                                                                                                                                                                CardSizeDp(
                                                                                                                                                                        it.widthDp,
                                                                                                                                                                        it.heightDp
                                                                                                                                                                )
                                                                                                                                                        }
                                                                                                                                                        ?: defaultCardSizeFor(
                                                                                                                                                                draggingId
                                                                                                                                                        )
                                                                                                                                        val dropXDp =
                                                                                                                                                ((draggingPointerPx
                                                                                                                                                                .x -
                                                                                                                                                                canvasTopLeftPx
                                                                                                                                                                        .x) /
                                                                                                                                                                (density *
                                                                                                                                                                        canvasScale) -
                                                                                                                                                                (dragSize.widthDp /
                                                                                                                                                                        2f))
                                                                                                                                                        .coerceAtLeast(
                                                                                                                                                                0f
                                                                                                                                                        )
                                                                                                                                        val dropYDp =
                                                                                                                                                ((draggingPointerPx
                                                                                                                                                                .y -
                                                                                                                                                                canvasTopLeftPx
                                                                                                                                                                        .y) /
                                                                                                                                                                (density *
                                                                                                                                                                        canvasScale) -
                                                                                                                                                                (dragSize.heightDp /
                                                                                                                                                                        2f))
                                                                                                                                                        .coerceAtLeast(
                                                                                                                                                                0f
                                                                                                                                                        )
                                                                                                                                        onPlaceCardAt(
                                                                                                                                                draggingId,
                                                                                                                                                dropXDp,
                                                                                                                                                dropYDp
                                                                                                                                        )
                                                                                                                                }
                                                                                                                                draggingCardId =
                                                                                                                                        null
                                                                                                                        },
                                                                                                                        onDragCancel = {
                                                                                                                                draggingCardId =
                                                                                                                                        null
                                                                                                                        },
                                                                                                                        onDrag = {
                                                                                                                                change,
                                                                                                                                dragAmount
                                                                                                                                ->
                                                                                                                                change.consume()
                                                                                                                                draggingPointerPx =
                                                                                                                                        Offset(
                                                                                                                                                x =
                                                                                                                                                        draggingPointerPx
                                                                                                                                                                .x +
                                                                                                                                                                dragAmount
                                                                                                                                                                        .x,
                                                                                                                                                y =
                                                                                                                                                        draggingPointerPx
                                                                                                                                                                .y +
                                                                                                                                                                dragAmount
                                                                                                                                                                        .y
                                                                                                                                        )
                                                                                                                        }
                                                                                                                )
                                                                                                        },
                                                                                        onClick = {
                                                                                                view.performHapticFeedback(
                                                                                                        HapticFeedbackConstants
                                                                                                                .KEYBOARD_TAP
                                                                                                )
                                                                                                val checked =
                                                                                                        !enabledCards
                                                                                                                .contains(
                                                                                                                        availableCard
                                                                                                                                .id
                                                                                                                )
                                                                                                onSetCardEnabled(
                                                                                                        availableCard
                                                                                                                .id,
                                                                                                        checked
                                                                                                )
                                                                                        }
                                                                                ) {
                                                                                        Row(
                                                                                                modifier =
                                                                                                        Modifier.fillMaxWidth()
                                                                                                                .padding(
                                                                                                                        10.dp
                                                                                                                ),
                                                                                                verticalAlignment =
                                                                                                        Alignment
                                                                                                                .CenterVertically,
                                                                                                horizontalArrangement =
                                                                                                        Arrangement
                                                                                                                .spacedBy(
                                                                                                                        10.dp
                                                                                                                )
                                                                                        ) {
                                                                                                val checked =
                                                                                                        enabledCards
                                                                                                                .contains(
                                                                                                                        availableCard
                                                                                                                                .id
                                                                                                                )
                                                                                                Icon(
                                                                                                        imageVector =
                                                                                                                if (checked
                                                                                                                )
                                                                                                                        Icons.Default
                                                                                                                                .CheckBox
                                                                                                                else
                                                                                                                        Icons.Default
                                                                                                                                .CheckBoxOutlineBlank,
                                                                                                        contentDescription =
                                                                                                                null
                                                                                                )
                                                                                                Column(
                                                                                                        modifier =
                                                                                                                Modifier.weight(
                                                                                                                        1f
                                                                                                                )
                                                                                                ) {
                                                                                                        Text(
                                                                                                                availableCard
                                                                                                                        .title,
                                                                                                                fontWeight =
                                                                                                                        FontWeight
                                                                                                                                .SemiBold
                                                                                                        )
                                                                                                        Text(
                                                                                                                availableCard
                                                                                                                        .subtitle,
                                                                                                                style =
                                                                                                                        MaterialTheme
                                                                                                                                .typography
                                                                                                                                .bodySmall,
                                                                                                                color =
                                                                                                                        MaterialTheme
                                                                                                                                .colorScheme
                                                                                                                                .onSurfaceVariant
                                                                                                        )
                                                                                                }
                                                                                        }
                                                                                }
                                                                        }
                                                                }

                                                                val scrollFraction =
                                                                        if (drawerScrollState
                                                                                        .maxValue >
                                                                                        0
                                                                        ) {
                                                                                drawerScrollState
                                                                                        .value
                                                                                        .toFloat() /
                                                                                        drawerScrollState
                                                                                                .maxValue
                                                                                                .toFloat()
                                                                        } else 0f
                                                                val thumbFraction =
                                                                        if (drawerScrollState
                                                                                        .maxValue >
                                                                                        0
                                                                        ) {
                                                                                val viewportHeight =
                                                                                        drawerScrollState
                                                                                                .viewportSize
                                                                                                .toFloat()
                                                                                val totalContent =
                                                                                        viewportHeight +
                                                                                                drawerScrollState
                                                                                                        .maxValue
                                                                                                        .toFloat()
                                                                                (viewportHeight /
                                                                                                totalContent)
                                                                                        .coerceIn(
                                                                                                0.1f,
                                                                                                1f
                                                                                        )
                                                                        } else 1f

                                                                Canvas(
                                                                        modifier =
                                                                                Modifier.align(
                                                                                                Alignment
                                                                                                        .CenterEnd
                                                                                        )
                                                                                        .fillMaxHeight()
                                                                                        .width(6.dp)
                                                                                        .padding(
                                                                                                vertical =
                                                                                                        4.dp
                                                                                        )
                                                                ) {
                                                                        drawRoundRect(
                                                                                color =
                                                                                        scrollbarTrackColor,
                                                                                cornerRadius =
                                                                                        GeoCornerRadius(
                                                                                                3.dp.toPx()
                                                                                        ),
                                                                                size =
                                                                                        GeoSize(
                                                                                                size.width,
                                                                                                size.height
                                                                                        )
                                                                        )
                                                                        val thumbHeight =
                                                                                (size.height *
                                                                                                thumbFraction)
                                                                                        .coerceAtLeast(
                                                                                                24.dp.toPx()
                                                                                        )
                                                                        val thumbTravel =
                                                                                size.height -
                                                                                        thumbHeight
                                                                        val thumbY =
                                                                                thumbTravel *
                                                                                        scrollFraction
                                                                        drawRoundRect(
                                                                                color =
                                                                                        scrollbarColor,
                                                                                topLeft =
                                                                                        Offset(
                                                                                                0f,
                                                                                                thumbY
                                                                                        ),
                                                                                size =
                                                                                        GeoSize(
                                                                                                size.width,
                                                                                                thumbHeight
                                                                                        ),
                                                                                cornerRadius =
                                                                                        GeoCornerRadius(
                                                                                                3.dp.toPx()
                                                                                        )
                                                                        )
                                                                }
                                                        }

                                                        Button(
                                                                onClick = {
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants
                                                                                        .KEYBOARD_TAP
                                                                        )
                                                                        showCardDrawer = false
                                                                },
                                                                modifier = Modifier.fillMaxWidth()
                                                        ) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string.button_done
                                                                        )
                                                                )
                                                        }
                                                }
                                        }
                                }

                                if (draggingCard != null && draggingCardSize != null) {
                                        if (canDropOnCanvas) {
                                                Box(
                                                        modifier =
                                                                Modifier.fillMaxSize()
                                                                        .background(
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .primary
                                                                                        .copy(
                                                                                                alpha =
                                                                                                        0.06f
                                                                                        )
                                                                        )
                                                )
                                        }

                                        val previewX =
                                                if (canDropOnCanvas)
                                                        (dropTargetXDp * density * canvasScale)
                                                                .roundToInt()
                                                else
                                                        (dragPointerCanvasPx.x -
                                                                        (draggingCardSize.widthDp *
                                                                                density *
                                                                                canvasScale / 2f))
                                                                .roundToInt()
                                                                .coerceAtLeast(0)
                                        val previewY =
                                                if (canDropOnCanvas)
                                                        (dropTargetYDp * density * canvasScale)
                                                                .roundToInt()
                                                else
                                                        (dragPointerCanvasPx.y -
                                                                        (draggingCardSize.heightDp *
                                                                                density *
                                                                                canvasScale / 2f))
                                                                .roundToInt()
                                                                .coerceAtLeast(0)

                                        Card(
                                                modifier =
                                                        Modifier.offset {
                                                                        IntOffset(
                                                                                previewX,
                                                                                previewY
                                                                        )
                                                                }
                                                                .width(
                                                                        (draggingCardSize.widthDp *
                                                                                        canvasScale)
                                                                                .dp
                                                                )
                                                                .height(
                                                                        (draggingCardSize.heightDp *
                                                                                        canvasScale)
                                                                                .dp
                                                                ),
                                                border =
                                                        BorderStroke(
                                                                width =
                                                                        if (canDropOnCanvas) 2.dp
                                                                        else 1.dp,
                                                                color =
                                                                        if (canDropOnCanvas)
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .primary
                                                                        else
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .outline
                                                        ),
                                                colors =
                                                        CardDefaults.cardColors(
                                                                containerColor =
                                                                        if (canDropOnCanvas)
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .primaryContainer
                                                                                        .copy(
                                                                                                alpha =
                                                                                                        0.35f
                                                                                        )
                                                                        else
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .surfaceVariant
                                                                                        .copy(
                                                                                                alpha =
                                                                                                        0.75f
                                                                                        )
                                                        )
                                        ) {
                                                Box(
                                                        modifier =
                                                                Modifier.fillMaxSize()
                                                                        .padding(12.dp),
                                                        contentAlignment = Alignment.Center
                                                ) {
                                                        Text(
                                                                text =
                                                                        if (canDropOnCanvas)
                                                                                stringResource(
                                                                                        R.string
                                                                                                .dashboard_drop_to_place,
                                                                                        draggingCard
                                                                                                .title
                                                                                )
                                                                        else draggingCard.title,
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodyMedium,
                                                                fontWeight = FontWeight.SemiBold
                                                        )
                                                }
                                        }
                                }

                                pickerCardId?.let { cardId ->
                                        val pickedCard = cards.firstOrNull { it.id == cardId }
                                        if (pickedCard != null) {
                                                val pickedSensor = selectSensor(pickedCard.sensorId, telemetrySensors)
                                                val pickedHistory = telemetryHistory[pickedSensor?.id].orEmpty()
                                                DisplayModePickerSheet(
                                                        cardId = cardId,
                                                        sensor = pickedSensor,
                                                        history = pickedHistory,
                                                        currentMode = pickedCard.displayMode,
                                                        currentTitle = pickedCard.customTitle?.takeIf { it.isNotBlank() }
                                                                ?: pickedCard.title,
                                                        currentShowValueOverlay = pickedCard.showValueOverlay,
                                                        otherSensors = telemetrySensors.filter { it.id != pickedCard.sensorId },
                                                        onDismiss = { pickerCardId = null },
                                                        onPickDisplayMode = { id, mode, secondary ->
                                                                onPickDisplayMode(id, mode, secondary)
                                                                pickerCardId = null
                                                        },
                                                        onSetTitle = onSetCardTitle,
                                                        onSetValueOverlay = onSetValueOverlay
                                                )
                                        }
                                }

                                var showShapePicker by remember { mutableStateOf(false) }
                                if (selectedCardIds.isNotEmpty()) {
                                        val allPinned =
                                                selectedCardIds.all { id -> cards.firstOrNull { it.id == id }?.pinned == true }
                                        DashboardSelectionActionBar(
                                                selectionCount = selectedCardIds.size,
                                                allPinned = allPinned,
                                                onTogglePin = onTogglePinSelection,
                                                onReshape = { showShapePicker = true },
                                                onRemove = onRemoveSelection,
                                                onDone = onClearSelection,
                                                modifier = Modifier.align(Alignment.TopCenter)
                                                        .statusBarsPadding()
                                                        .padding(top = 8.dp)
                                        )
                                }
                                if (showShapePicker) {
                                        ShapePickerSheet(
                                                cornerRadiusDp = cornerRadius,
                                                onDismiss = { showShapePicker = false },
                                                onPick = { idx ->
                                                        onSetGroupShape(selectedCardIds, idx)
                                                        showShapePicker = false
                                                }
                                        )
                                }

                                // M3 Expressive: expandable FAB menu — a ToggleFAB that fans out
                                // the dashboard's quick actions (connect, customize, reset view).
                                var fabMenuExpanded by remember { mutableStateOf(false) }
                                FloatingActionButtonMenu(
                                        expanded = fabMenuExpanded,
                                        button = {
                                                ToggleFloatingActionButton(
                                                        modifier = Modifier.onGloballyPositioned {
                                                                onAddAnchor(it.boundsInRoot().center)
                                                        },
                                                        checked = fabMenuExpanded,
                                                        onCheckedChange = {
                                                                view.performHapticFeedback(
                                                                        HapticFeedbackConstants
                                                                                .KEYBOARD_TAP
                                                                )
                                                                fabMenuExpanded = it
                                                        }
                                                ) {
                                                        Icon(
                                                                if (fabMenuExpanded)
                                                                        Icons.Default.Close
                                                                else Icons.Default.Add,
                                                                contentDescription =
                                                                        stringResource(
                                                                                R.string
                                                                                        .nav_more_label
                                                                        )
                                                        )
                                                }
                                        },
                                        modifier =
                                                Modifier.align(Alignment.BottomEnd)
                                                        .navigationBarsPadding()
                                ) {
                                        FloatingActionButtonMenuItem(
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.CONFIRM
                                                        )
                                                        fabMenuExpanded = false
                                                        onToggleConnection()
                                                },
                                                icon = {
                                                        Icon(
                                                                Icons.Default.PowerSettingsNew,
                                                                contentDescription = null
                                                        )
                                                },
                                                text = {
                                                        Text(
                                                                stringResource(
                                                                        R.string.dashboard_pc_status
                                                                )
                                                        )
                                                }
                                        )
                                        FloatingActionButtonMenuItem(
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.KEYBOARD_TAP
                                                        )
                                                        fabMenuExpanded = false
                                                        showCardDrawer = true
                                                },
                                                icon = {
                                                        Icon(
                                                                Icons.Default.Tune,
                                                                contentDescription = null
                                                        )
                                                },
                                                text = {
                                                        Text(
                                                                stringResource(
                                                                        R.string.cd_customize_cards
                                                                )
                                                        )
                                                }
                                        )
                                        FloatingActionButtonMenuItem(
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.KEYBOARD_TAP
                                                        )
                                                        fabMenuExpanded = false
                                                        canvasScale = 1f
                                                },
                                                icon = {
                                                        Icon(
                                                                Icons.Default.FilterCenterFocus,
                                                                contentDescription = null
                                                        )
                                                },
                                                text = {
                                                        Text(stringResource(R.string.cd_reset_zoom))
                                                }
                                        )
                                }
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
            isConnecting = false,
            telemetrySensors = listOf(
                TelemetrySensor("sensor:cpu", "CPU Usage", "CPU", 25.0, "%"),
                TelemetrySensor("sensor:ram", "RAM Usage", "Memory", 60.0, "%")
            ),
            telemetryHistory = emptyMap(),
            cards = listOf(
                HomeCardState("pc_status", "PC Status", HomeCardType.PC_STATUS, null, 0f, 0f, 200f, 150f, TelemetryDisplayMode.VALUE),
                HomeCardState("sensor:cpu", "CPU", HomeCardType.TELEMETRY, "sensor:cpu", 210f, 0f, 150f, 150f, TelemetryDisplayMode.RING_GAUGE),
                HomeCardState("sensor:ram", "RAM", HomeCardType.TELEMETRY, "sensor:ram", 0f, 160f, 150f, 150f, TelemetryDisplayMode.LINE)
            ),
            enabledCards = setOf("pc_status", "sensor:cpu", "sensor:ram"),
            cornerRadius = 12,
            cardOpacity = 1.0f,
            pcCardShapePreset = 0f,
            telemetryCardShapePreset = 1f,
            draggingCardId = null,
            selectedCardIds = emptySet(),
            onNavigateToConnection = {},
            onResizeCard = { _, _, _ -> },
            onSaveCardLayout = {},
            onToggleConnection = {},
            onWakePc = {},
            onPickDisplayMode = { _, _, _ -> },
            onSetCardTitle = { _, _ -> },
            onSetValueOverlay = { _, _ -> },
            onPlaceCardAt = { _, _, _ -> },
            onBeginCardDrag = {},
            onDragCardBy = { _, _ -> },
            onEndCardDrag = {},
            onSelectCard = {},
            onToggleCardSelection = {},
            onMoveSelection = { _, _ -> },
            onTogglePinSelection = {},
            onRemoveSelection = {},
            onClearSelection = {},
            onSetGroupShape = { _, _ -> },
            onSetCardEnabled = { _, _ -> }
        )
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ConnectionOrbCard(
        isConnected: Boolean,
        isConnecting: Boolean,
        shapePreset: Float,
        cornerRadius: Int,
        onToggle: () -> Unit,
        onNavigateToConnection: () -> Unit = {}
) {
        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                        var currentMorphTarget by remember { mutableFloatStateOf(shapePreset) }

                        LaunchedEffect(isConnecting) {
                                if (isConnecting) {
                                        while (true) {
                                                currentMorphTarget =
                                                        (0 until materialShapesList.size)
                                                                .random()
                                                                .toFloat()
                                                delay(1000)
                                        }
                                } else {
                                        currentMorphTarget = shapePreset
                                }
                        }

                        val animatedShapePreset by
                                animateFloatAsState(
                                        targetValue = currentMorphTarget,
                                        animationSpec =
                                                MaterialTheme.motionScheme.slowSpatialSpec(),
                                        label = "orb_morph"
                                )

                        val orbColor by
                                animateColorAsState(
                                        targetValue =
                                                when {
                                                        isConnected ->
                                                                MaterialTheme.colorScheme.primary
                                                        isConnecting ->
                                                                MaterialTheme.colorScheme.secondary
                                                        else -> MaterialTheme.colorScheme.error
                                                },
                                        animationSpec =
                                                MaterialTheme.motionScheme.defaultEffectsSpec(),
                                        label = "orb_color"
                                )

                        // Only run the infinite glow transition while its value is actually
                        // used (connected/connecting); when disconnected the orb is opaque and
                        // the animation would just burn battery. Conditional composable call is
                        // fine here: the branch is keyed by stable state.
                        val glowAlpha =
                                if (isConnected || isConnecting) {
                                        val infiniteTransition =
                                                rememberInfiniteTransition(label = "glow")
                                        infiniteTransition
                                                .animateFloat(
                                                        initialValue = 0.3f,
                                                        targetValue = 0.8f,
                                                        animationSpec =
                                                                infiniteRepeatable(
                                                                        animation =
                                                                                tween(
                                                                                        1500,
                                                                                        easing =
                                                                                                LinearEasing
                                                                                ),
                                                                        repeatMode =
                                                                                RepeatMode.Reverse
                                                                ),
                                                        label = "glow_alpha"
                                                )
                                                .value
                                } else 1f

                        Box(
                                modifier =
                                        Modifier.size(72.dp)
                                                .clip(cardShape(animatedShapePreset, cornerRadius))
                                                .background(orbColor.copy(alpha = glowAlpha))
                                                .clickable {
                                                        when {
                                                                isConnected -> onToggle()
                                                                isConnecting -> {}
                                                                else -> onNavigateToConnection()
                                                        }
                                                },
                                contentAlignment = Alignment.Center
                        ) {}

                        Text(
                                text =
                                        when {
                                                isConnected ->
                                                        stringResource(
                                                                R.string.dashboard_host_online
                                                        )
                                                isConnecting ->
                                                        stringResource(
                                                                R.string.dashboard_connecting
                                                        )
                                                else ->
                                                        stringResource(
                                                                R.string.dashboard_tap_to_connect
                                                        )
                                        },
                                style = MaterialTheme.typography.labelLarge,
                                fontWeight = FontWeight.Black,
                                color = orbColor
                        )
                }
        }
}

@Composable
private fun WakeOnLanCard(onWake: () -> Unit) {
        val view = LocalView.current
        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                FilledTonalButton(
                        onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                onWake()
                        },
                        modifier = Modifier.padding(16.dp)
                ) {
                        Icon(
                                Icons.Default.PowerSettingsNew,
                                contentDescription = null,
                                modifier = Modifier.size(20.dp)
                        )
                        Spacer(Modifier.width(8.dp))
                        Text(
                                stringResource(R.string.dashboard_wake_pc_button),
                                style = MaterialTheme.typography.labelLarge,
                                fontWeight = FontWeight.Bold
                        )
                }
        }
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
        onOpenPicker: () -> Unit
) {
        val dynamicPadding = calculateAdaptivePadding(shapeIndex)

        Column(
                modifier = Modifier.fillMaxSize().padding(dynamicPadding),
                verticalArrangement = Arrangement.SpaceBetween,
                horizontalAlignment = Alignment.CenterHorizontally
        ) {
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                ) {
                        Text(
                                title,
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = FontWeight.Bold,
                                maxLines = 1,
                                overflow =
                                        androidx.compose.ui.text.style.TextOverflow
                                                .Ellipsis,
                                modifier = Modifier.weight(1f)
                        )
                        // Hidden while selectionActive (spec 4.5) - the action bar is the single
                        // control surface during lift/multi-select.
                        if (!selectionActive) {
                                IconButton(
                                        onClick = onOpenPicker,
                                        modifier = Modifier.size(24.dp)
                                ) {
                                        Icon(
                                                Icons.Default.GridView,
                                                contentDescription =
                                                        stringResource(
                                                                R.string.cd_open_view_picker
                                                        ),
                                                modifier = Modifier.size(16.dp)
                                        )
                                }
                        }
                }

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

