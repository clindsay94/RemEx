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
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.FilterCenterFocus
import androidx.compose.material.icons.filled.OpenInFull
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexScreenHeader
import com.clindsay94.remex.ui.theme.cardShape
import com.clindsay94.remex.ui.theme.materialShapesList
import kotlin.math.roundToInt
import kotlinx.coroutines.delay

// Wrapper that isolates AnimatedVisibility from outer ColumnScope receiver.
// When the transformable Box lives inside a Column, Kotlin's overload resolution
// binds to ColumnScope.AnimatedVisibility and then fails because @LayoutScopeMarker
// blocks the outer ColumnScope implicit receiver. Calling AnimatedVisibility from
// a plain composable body resolves to the top-level overload cleanly.
@Composable
private fun PlainAnimatedVisibility(
        visible: Boolean,
        modifier: Modifier = Modifier,
        content: @Composable AnimatedVisibilityScope.() -> Unit
) {
        AnimatedVisibility(visible = visible, modifier = modifier, content = content)
}

private data class AvailableCardItem(val id: String, val title: String, val subtitle: String)

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
        val isConnected by viewModel.isConnected.collectAsState()
        val isConnecting by viewModel.isConnecting.collectAsState()
        val telemetrySensors by viewModel.telemetrySensors.collectAsState()
        val telemetryHistory by viewModel.telemetryHistory.collectAsState()
        val cards by viewModel.homeCards.collectAsState()
        val enabledCards by viewModel.enabledCardIds.collectAsState()
        val cornerRadius by viewModel.cardCornerRadius.collectAsState()
        val cardOpacity by viewModel.cardOpacity.collectAsState()
        val pcCardShapePreset by viewModel.pcCardShapePreset.collectAsState()
        val telemetryCardShapePreset by viewModel.telemetryCardShapePreset.collectAsState()

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
                onNavigateToConnection = onNavigateToConnection,
                onMoveCard = { cardId, dx, dy -> viewModel.moveCard(cardId, dx, dy) },
                onResizeCard = { cardId, dw, dh -> viewModel.resizeCard(cardId, dw, dh) },
                onSaveCardLayout = { viewModel.saveCardLayout() },
                onToggleConnection = { viewModel.toggleConnection() },
                onWakePc = { viewModel.wakePc() },
                onCycleTelemetryDisplayMode = { cardId ->
                        viewModel.cycleTelemetryDisplayMode(cardId)
                },
                onPlaceCardAt = { cardId, x, y -> viewModel.placeCardAt(cardId, x, y) },
                onSetCardEnabled = { cardId, enabled -> viewModel.setCardEnabled(cardId, enabled) }
        )
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
        onNavigateToConnection: () -> Unit,
        onMoveCard: (String, Float, Float) -> Unit,
        onResizeCard: (String, Float, Float) -> Unit,
        onSaveCardLayout: () -> Unit,
        onToggleConnection: () -> Unit,
        onWakePc: () -> Unit,
        onCycleTelemetryDisplayMode: (String) -> Unit,
        onPlaceCardAt: (String, Float, Float) -> Unit,
        onSetCardEnabled: (String, Boolean) -> Unit
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
                                                                }
                                                )
                                        )
                                }
                        }
                                .distinctBy { it.id }
                }

        val density = androidx.compose.ui.platform.LocalDensity.current.density
        val sensorMap = remember(telemetrySensors) { telemetrySensors.associateBy { it.id } }

        var showCardDrawer by remember { mutableStateOf(false) }
        var canvasTopLeftPx by remember { mutableStateOf(Offset.Zero) }
        var drawerLeftPx by remember { mutableFloatStateOf(Float.MAX_VALUE) }
        var draggingCardId by remember { mutableStateOf<String?>(null) }
        var draggingPointerPx by remember { mutableStateOf(Offset.Zero) }

        // Pinch-to-zoom: use a State object so pointerInput lambdas can read the
        // current value without needing it as a restart key (review fix).
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
        // Drop target positions must account for the canvas scale
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

        // Base canvas dimensions — independent of scale.
        // graphicsLayer handles the visual zoom without relayout.
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

        // Surface establishes LocalContentColor from the theme so that header title text
        // and other non-card content inherits the correct on-surface color.
        Surface(modifier = Modifier.fillMaxSize()) {
        Column(modifier = Modifier.fillMaxSize()) {
                RemexScreenHeader(
                        title = stringResource(R.string.screen_dashboard_title),
                        actions = {
                                if (canvasScale != 1f) {
                                        IconButton(
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.KEYBOARD_TAP
                                                        )
                                                        canvasScale = 1f
                                                }
                                        ) {
                                                Icon(
                                                        Icons.Default.FilterCenterFocus,
                                                        contentDescription =
                                                                stringResource(
                                                                        R.string.cd_reset_zoom
                                                                )
                                                )
                                        }
                                }
                                IconButton(
                                        onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                )
                                                showCardDrawer = !showCardDrawer
                                        }
                                ) {
                                        Icon(
                                                Icons.Default.Tune,
                                                contentDescription =
                                                        stringResource(R.string.cd_customize_cards)
                                        )
                                }
                        }
                )
                Box(modifier = Modifier.fillMaxSize().transformable(state = transformableState)) {
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
                                                                MaterialTheme.colorScheme.background
                                                        )
                                                        // Use graphicsLayer for zoom to avoid
                                                        // expensive relayout on
                                                        // every frame during pinch gestures (review
                                                        // fix).
                                                        .graphicsLayer {
                                                                scaleX = canvasScaleState.floatValue
                                                                scaleY = canvasScaleState.floatValue
                                                                transformOrigin =
                                                                        androidx.compose.ui.graphics
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
                                                        when {
                                                                card.id == "pc_status" ->
                                                                        pcCardShapePreset
                                                                card.id.startsWith("sensor:") ->
                                                                        telemetryCardShapePreset
                                                                else -> 0f
                                                        }

                                                Card(
                                                        modifier =
                                                                Modifier.offset {
                                                                        IntOffset(xPx, yPx)
                                                                }
                                                                        .width(card.widthDp.dp)
                                                                        .height(card.heightDp.dp)
                                                                        // pointerInput uses stable
                                                                        // card.id key — reads
                                                                        // canvasScaleState.floatValue directly (review
                                                                        // fix).
                                                                        .pointerInput(card.id) {
                                                                                detectDragGestures(
                                                                                        onDrag = {
                                                                                                change,
                                                                                                dragAmount
                                                                                                ->
                                                                                                change.consume()
                                                                                                val currentScale =
                                                                                                        canvasScaleState
                                                                                                                .floatValue
                                                                                                onMoveCard(
                                                                                                        card.id,
                                                                                                        dragAmount
                                                                                                                .x /
                                                                                                                (density *
                                                                                                                        currentScale),
                                                                                                        dragAmount
                                                                                                                .y /
                                                                                                                (density *
                                                                                                                        currentScale)
                                                                                                )
                                                                                        },
                                                                                        onDragEnd = {
                                                                                                onSaveCardLayout()
                                                                                        }
                                                                                )
                                                                        },
                                                        shape =
                                                                cardShape(
                                                                        cardShapePreset,
                                                                        cornerRadius
                                                                ),
                                                        colors =
                                                                CardDefaults.cardColors(
                                                                        containerColor =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .primaryContainer
                                                                                        .copy(
                                                                                                alpha =
                                                                                                        cardOpacity
                                                                                        ),
                                                                        contentColor =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onPrimaryContainer
                                                                )
                                                ) {
                                                        Box(
                                                                modifier =
                                                                        Modifier.fillMaxSize()
                                                                                .padding(4.dp)
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
                                                                                        sensorMap[
                                                                                                card.sensorId]
                                                                                val history =
                                                                                        telemetryHistory[
                                                                                                        card.sensorId]
                                                                                                .orEmpty()
                                                                                TelemetryCardContent(
                                                                                        title =
                                                                                                card.title,
                                                                                        sensor =
                                                                                                sensor,
                                                                                        history =
                                                                                                history,
                                                                                        mode =
                                                                                                card.displayMode,
                                                                                        onCycleDisplayMode = {
                                                                                                onCycleTelemetryDisplayMode(
                                                                                                        card.id
                                                                                                )
                                                                                        },
                                                                                        isExpressiveShape =
                                                                                                cardShapePreset >
                                                                                                        0
                                                                                )
                                                                        }
                                                                }

                                                                // Resize handle — bottom-right
                                                                // corner
                                                                Surface(
                                                                        shape = CircleShape,
                                                                        // M3: use solid primary
                                                                        // token, tonal elevation
                                                                        // for depth
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .primary,
                                                                        tonalElevation = 2.dp,
                                                                        modifier =
                                                                                Modifier.align(
                                                                                                Alignment
                                                                                                        .BottomEnd
                                                                                        )
                                                                                        .padding(
                                                                                                12.dp
                                                                                        )
                                                                                        .size(24.dp)
                                                                                        .pointerInput(
                                                                                                "resize_${card.id}"
                                                                                        ) {
                                                                                                detectDragGestures(
                                                                                                        onDrag = {
                                                                                                                change,
                                                                                                                dragAmount
                                                                                                                ->
                                                                                                                change.consume()
                                                                                                                val currentScale =
                                                                                                                        canvasScaleState
                                                                                                                                .floatValue
                                                                                                                onResizeCard(
                                                                                                                        card.id,
                                                                                                                        dragAmount
                                                                                                                                .x /
                                                                                                                                (density *
                                                                                                                                        currentScale),
                                                                                                                        dragAmount
                                                                                                                                .y /
                                                                                                                                (density *
                                                                                                                                        currentScale)
                                                                                                                )
                                                                                                        },
                                                                                                        onDragEnd = {
                                                                                                                onSaveCardLayout()
                                                                                                        }
                                                                                                )
                                                                                        }
                                                                ) {
                                                                        Icon(
                                                                                Icons.Default
                                                                                        .OpenInFull,
                                                                                contentDescription =
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .cd_resize_cards
                                                                                        ),
                                                                                tint =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .onPrimary,
                                                                                modifier =
                                                                                        Modifier.size(
                                                                                                        12.dp
                                                                                                )
                                                                                                .padding(
                                                                                                        2.dp
                                                                                                )
                                                                        )
                                                                }
                                                        }
                                                }
                                        }
                                }
                        }

                        // M3: ModalBottomSheet replaces side-panel drawer for standard M3 sheet UX
                        if (showCardDrawer) {
                                ModalBottomSheet(
                                        onDismissRequest = { showCardDrawer = false },
                                        sheetState =
                                                rememberModalBottomSheetState(
                                                        skipPartiallyExpanded = true
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
                                                        style = MaterialTheme.typography.titleLarge,
                                                        fontWeight = FontWeight.Bold
                                                )
                                                Text(
                                                        text =
                                                                stringResource(
                                                                        R.string
                                                                                .dashboard_card_drawer_hint
                                                                ),
                                                        style = MaterialTheme.typography.bodySmall,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant,
                                                        modifier =
                                                                Modifier.padding(
                                                                        top = 4.dp,
                                                                        bottom = 12.dp
                                                                )
                                                )

                                                // Scrollable list with always-visible scrollbar
                                                val drawerScrollState = rememberScrollState()
                                                val scrollbarColor =
                                                        MaterialTheme.colorScheme.onSurfaceVariant
                                                                .copy(alpha = 0.4f)
                                                val scrollbarTrackColor =
                                                        MaterialTheme.colorScheme.surfaceVariant
                                                                .copy(alpha = 0.3f)

                                                Box(modifier = Modifier.heightIn(max = 400.dp)) {
                                                        Column(
                                                                modifier =
                                                                        Modifier.fillMaxSize()
                                                                                .padding(
                                                                                        end = 12.dp
                                                                                ) // room for
                                                                                // scrollbar
                                                                                .verticalScroll(
                                                                                        drawerScrollState
                                                                                ),
                                                                verticalArrangement =
                                                                        Arrangement.spacedBy(8.dp)
                                                        ) {
                                                                availableCards.forEach {
                                                                        availableCard ->
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

                                                        // Always-visible scrollbar track + thumb
                                                        val scrollFraction =
                                                                if (drawerScrollState.maxValue > 0
                                                                ) {
                                                                        drawerScrollState.value
                                                                                .toFloat() /
                                                                                drawerScrollState
                                                                                        .maxValue
                                                                                        .toFloat()
                                                                } else 0f
                                                        val thumbFraction =
                                                                if (drawerScrollState.maxValue > 0
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
                                                                                .coerceIn(0.1f, 1f)
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
                                                                // Track
                                                                drawRoundRect(
                                                                        color = scrollbarTrackColor,
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
                                                                // Thumb
                                                                val thumbHeight =
                                                                        (size.height *
                                                                                        thumbFraction)
                                                                                .coerceAtLeast(
                                                                                        24.dp.toPx()
                                                                                )
                                                                val thumbTravel =
                                                                        size.height - thumbHeight
                                                                val thumbY =
                                                                        thumbTravel * scrollFraction
                                                                drawRoundRect(
                                                                        color = scrollbarColor,
                                                                        topLeft =
                                                                                Offset(0f, thumbY),
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
                                                ) { Text(stringResource(R.string.button_done)) }
                                        }
                                }
                        }

                        // Drag-from-drawer ghost preview
                        if (draggingCard != null && draggingCardSize != null) {
                                if (canDropOnCanvas) {
                                        Box(
                                                modifier =
                                                        Modifier.fillMaxSize()
                                                                .background(
                                                                        MaterialTheme.colorScheme
                                                                                .primary.copy(
                                                                                alpha = 0.06f
                                                                        )
                                                                )
                                        )
                                }

                                val previewX =
                                        if (canDropOnCanvas)
                                                (dropTargetXDp * density * canvasScale).roundToInt()
                                        else
                                                (dragPointerCanvasPx.x -
                                                                (draggingCardSize.widthDp *
                                                                        density *
                                                                        canvasScale / 2f))
                                                        .roundToInt()
                                                        .coerceAtLeast(0)
                                val previewY =
                                        if (canDropOnCanvas)
                                                (dropTargetYDp * density * canvasScale).roundToInt()
                                        else
                                                (dragPointerCanvasPx.y -
                                                                (draggingCardSize.heightDp *
                                                                        density *
                                                                        canvasScale / 2f))
                                                        .roundToInt()
                                                        .coerceAtLeast(0)

                                Card(
                                        modifier =
                                                Modifier.offset { IntOffset(previewX, previewY) }
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
                                                        width = if (canDropOnCanvas) 2.dp else 1.dp,
                                                        color =
                                                                if (canDropOnCanvas)
                                                                        MaterialTheme.colorScheme
                                                                                .primary
                                                                else
                                                                        MaterialTheme.colorScheme
                                                                                .outline
                                                ),
                                        colors =
                                                CardDefaults.cardColors(
                                                        containerColor =
                                                                if (canDropOnCanvas)
                                                                        MaterialTheme.colorScheme
                                                                                .primaryContainer
                                                                                .copy(alpha = 0.35f)
                                                                else
                                                                        MaterialTheme.colorScheme
                                                                                .surfaceVariant
                                                                                .copy(alpha = 0.75f)
                                                )
                                ) {
                                        Box(
                                                modifier = Modifier.fillMaxSize().padding(12.dp),
                                                contentAlignment = Alignment.Center
                                        ) {
                                                Text(
                                                        text =
                                                                if (canDropOnCanvas)
                                                                        stringResource(
                                                                                R.string
                                                                                        .dashboard_drop_to_place,
                                                                                draggingCard.title
                                                                        )
                                                                else draggingCard.title,
                                                        style = MaterialTheme.typography.bodyMedium,
                                                        fontWeight = FontWeight.SemiBold
                                                )
                                        }
                                }
                        }
                }
        }
        } // Surface
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

                        val infiniteTransition = rememberInfiniteTransition(label = "glow")
                        val glowAlpha by
                                infiniteTransition.animateFloat(
                                        initialValue = 0.3f,
                                        targetValue = 0.8f,
                                        animationSpec =
                                                infiniteRepeatable(
                                                        animation =
                                                                tween(1500, easing = LinearEasing),
                                                        repeatMode = RepeatMode.Reverse
                                                ),
                                        label = "glow_alpha"
                                )

                        Box(
                                modifier =
                                        Modifier.size(72.dp)
                                                .clip(cardShape(animatedShapePreset, cornerRadius))
                                                .background(
                                                        orbColor.copy(
                                                                alpha =
                                                                        if (isConnected ||
                                                                                        isConnecting
                                                                        )
                                                                                glowAlpha
                                                                        else 1f
                                                        )
                                                )
                                                .clickable {
                                                        when {
                                                                // Already connected — toggle
                                                                // (reconnect logic in
                                                                // RemexClientManager)
                                                                isConnected -> onToggle()
                                                                // Already trying — wait, don't
                                                                // double-trigger
                                                                isConnecting -> {
                                                                        /* do nothing */
                                                                }
                                                                // Offline — send user to the
                                                                // connection screen
                                                                else -> onNavigateToConnection()
                                                        }
                                                },
                                contentAlignment = Alignment.Center
                        ) { /* orb interior intentionally empty */}

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
                // M3: FilledTonalButton is the semantic match for secondary-container colors
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
        onCycleDisplayMode: () -> Unit,
        isExpressiveShape: Boolean = false
) {
        val view = LocalView.current
        BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
                // Give extra padding for organic/expressive shapes so content doesn't clip into the
                // shape
                // edges
                val paddingFactor = if (isExpressiveShape) 0.22f else 0.12f
                val dynamicPadding =
                        (minOf(maxWidth, maxHeight) * paddingFactor).coerceAtLeast(16.dp)

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
                                IconButton(
                                        onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.KEYBOARD_TAP
                                                )
                                                onCycleDisplayMode()
                                        },
                                        modifier = Modifier.size(24.dp)
                                ) {
                                        Icon(
                                                Icons.Default.Tune,
                                                contentDescription =
                                                        stringResource(
                                                                R.string.cd_change_display_mode
                                                        ),
                                                modifier = Modifier.size(16.dp)
                                        )
                                }
                        }

                        val valueText =
                                if (sensor == null) "--"
                                else "${"%.1f".format(sensor.value)}${sensor.unit}"

                        when (mode) {
                                TelemetryDisplayMode.VALUE -> {
                                        Text(
                                                valueText,
                                                style = MaterialTheme.typography.headlineSmall,
                                                fontWeight = FontWeight.Black
                                        )
                                }
                                TelemetryDisplayMode.GAUGE -> {
                                        val percent =
                                                (sensor?.value ?: 0.0)
                                                        .toFloat()
                                                        .coerceIn(0f, 100f) / 100f
                                        val animatedProgress by
                                                animateFloatAsState(
                                                        targetValue = percent,
                                                        animationSpec =
                                                                MaterialTheme.motionScheme
                                                                        .fastSpatialSpec(),
                                                        label = "gauge_bounce"
                                                )
                                        Box(
                                                contentAlignment = Alignment.Center,
                                                modifier = Modifier.fillMaxWidth()
                                        ) {
                                                CircularProgressIndicator(
                                                        progress = { animatedProgress },
                                                        modifier = Modifier.size(64.dp),
                                                        strokeWidth = 6.dp,
                                                        strokeCap = StrokeCap.Round
                                                )
                                                Text(
                                                        "${(percent * 100).roundToInt()}%",
                                                        style =
                                                                MaterialTheme.typography
                                                                        .labelMedium,
                                                        fontWeight = FontWeight.Bold
                                                )
                                        }
                                }
                                TelemetryDisplayMode.LINE -> {
                                        Box(
                                                modifier = Modifier.weight(1f).fillMaxWidth(),
                                                contentAlignment = Alignment.Center
                                        ) { Sparkline(history = history) }
                                        Text(
                                                valueText,
                                                style = MaterialTheme.typography.labelLarge,
                                                fontWeight = FontWeight.Bold
                                        )
                                }
                                TelemetryDisplayMode.BAR, TelemetryDisplayMode.AREA -> {
                                        Box(
                                                modifier = Modifier.weight(1f).fillMaxWidth(),
                                                contentAlignment = Alignment.Center
                                        ) { Sparkline(history = history) }
                                        Text(
                                                valueText,
                                                style = MaterialTheme.typography.labelLarge,
                                                fontWeight = FontWeight.Bold
                                        )
                                }
                                TelemetryDisplayMode.CIRCLE_GAUGE -> {
                                        val percent =
                                                (sensor?.value ?: 0.0)
                                                        .toFloat()
                                                        .coerceIn(0f, 100f) / 100f
                                        val animatedProgress by
                                                animateFloatAsState(
                                                        targetValue = percent,
                                                        animationSpec =
                                                                MaterialTheme.motionScheme
                                                                        .fastSpatialSpec(),
                                                        label = "circle_gauge_bounce"
                                                )
                                        Box(
                                                contentAlignment = Alignment.Center,
                                                modifier = Modifier.fillMaxWidth()
                                        ) {
                                                CircularProgressIndicator(
                                                        progress = { animatedProgress },
                                                        modifier = Modifier.size(64.dp),
                                                        strokeWidth = 6.dp,
                                                        strokeCap = StrokeCap.Round
                                                )
                                                Text(
                                                        "${(percent * 100).roundToInt()}%",
                                                        style =
                                                                MaterialTheme.typography
                                                                        .labelMedium,
                                                        fontWeight = FontWeight.Bold
                                                )
                                        }
                                }
                        }
                }
        }
}

@Composable
private fun Sparkline(history: List<Float>) {
        if (history.size < 2) {
                Text(
                        text = stringResource(R.string.dashboard_collecting_data),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                return
        }
        val high = history.maxOrNull() ?: 1f
        val low = history.minOrNull() ?: 0f
        val range = (high - low).takeIf { it > 0f } ?: 1f
        val lineColor = MaterialTheme.colorScheme.primary
        Canvas(modifier = Modifier.fillMaxWidth().height(56.dp)) {
                val stepX = size.width / (history.size - 1).coerceAtLeast(1)
                val path = Path()
                history.forEachIndexed { index, value ->
                        val x = index * stepX
                        val normalized = (value - low) / range
                        val y = size.height - (normalized * size.height)
                        if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
                }
                drawPath(
                        path = path,
                        color = lineColor,
                        style = Stroke(width = 4f, cap = StrokeCap.Round)
                )
        }
}
