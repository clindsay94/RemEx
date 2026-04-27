package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.*
import androidx.compose.animation.core.*
import androidx.compose.foundation.*
import androidx.compose.foundation.gestures.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.*
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.theme.cardShape
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sqrt

data class DashboardUiState(
    val isConnected: Boolean = false,
    val isConnecting: Boolean = false,
    val telemetrySensors: List<TelemetrySensor> = emptyList(),
    val telemetryHistory: Map<String, List<Float>> = emptyMap(),
    val cards: List<HomeCardState> = emptyList(),
    val enabledCards: Set<String> = emptySet(),
    val cornerRadius: Int = 8,
    val cardOpacity: Float = 1.0f,
    val pcCardShapePreset: Float = 0f,
    val telemetryCardShapePreset: Float = 0f
)

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

    val uiState = DashboardUiState(
        isConnected = isConnected,
        isConnecting = isConnecting,
        telemetrySensors = telemetrySensors,
        telemetryHistory = telemetryHistory,
        cards = cards,
        enabledCards = enabledCards,
        cornerRadius = cornerRadius,
        cardOpacity = cardOpacity,
        pcCardShapePreset = pcCardShapePreset,
        telemetryCardShapePreset = telemetryCardShapePreset
    )

    DashboardScreenContent(
            uiState = uiState,
            onNavigateToConnection = onNavigateToConnection,
            onMoveCard = { cardId, dx, dy -> viewModel.moveCard(cardId, dx, dy) },
            onResizeCard = { cardId, dw, dh -> viewModel.resizeCard(cardId, dw, dh) },
            onSaveCardLayout = { viewModel.saveCardLayout() },
            onToggleConnection = { viewModel.toggleConnection() },
            onWakePc = { viewModel.wakePc() },
            onCycleTelemetryDisplayMode = { cardId -> viewModel.cycleTelemetryDisplayMode(cardId) },
            onPlaceCardAt = { cardId, x, y -> viewModel.placeCardAt(cardId, x, y) },
            onSetCardEnabled = { cardId, enabled -> viewModel.setCardEnabled(cardId, enabled) }
    )
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun DashboardScreenContent(
        uiState: DashboardUiState,
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
            remember(uiState.telemetrySensors, pcStatusTitle, wakePcTitle, telemetryFallback) {
                buildList {
                    add(DashboardAvailableCardItem("pc_status", pcStatusTitle, pcStatusSubtitle))
                    add(DashboardAvailableCardItem("wake_pc", wakePcTitle, wakePcSubtitle))
                    uiState.telemetrySensors.forEach { sensor ->
                        add(
                                DashboardAvailableCardItem(
                                        id = sensor.id,
                                        title = sensor.name,
                                        subtitle = sensor.category.ifBlank { telemetryFallback }
                                )
                        )
                    }
                }
                        .distinctBy { it.id }
            }

    val density = LocalDensity.current.density
    val sensorMap = remember(uiState.telemetrySensors) { uiState.telemetrySensors.associateBy { it.id } }

    var showCardDrawer by remember { mutableStateOf(false) }
    var canvasTopLeftPx by remember { mutableStateOf(Offset.Zero) }
    var drawerLeftPx by remember { mutableFloatStateOf(Float.MAX_VALUE) }
    var draggingCardId by remember { mutableStateOf<String?>(null) }
    var draggingPointerPx by remember { mutableStateOf(Offset.Zero) }

    // Pinch-to-zoom
    val canvasScaleState = remember { mutableFloatStateOf(1f) }
    var canvasScale by canvasScaleState
    @Suppress("DEPRECATION")
    val transformableState = rememberTransformableState { zoomChange, _, _ ->
        canvasScale = (canvasScale * zoomChange).coerceIn(0.4f, 3f)
    }

    val draggingCard = availableCards.firstOrNull { it.id == draggingCardId }
    val draggingCardSize =
            remember(draggingCard?.id, uiState.cards) {
                draggingCard?.let { item ->
                    uiState.cards.firstOrNull { it.id == item.id }?.let { card ->
                        CardSizeDp(card.widthDp.toInt(), card.heightDp.toInt())
                    }
                            ?: defaultCardSizeFor(item.id)
                }
            }

    val hScrollState = rememberScrollState()
    val vScrollState = rememberScrollState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.screen_dashboard_title)) },
                actions = {
                    if (canvasScale != 1f) {
                        IconButton(
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                    canvasScale = 1f
                                }
                        ) {
                            Icon(
                                    Icons.Default.FilterCenterFocus,
                                    contentDescription = stringResource(R.string.cd_reset_zoom)
                            )
                        }
                    }
                    IconButton(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                showCardDrawer = !showCardDrawer
                            }
                    ) {
                        Icon(
                                Icons.Default.Tune,
                                contentDescription = stringResource(R.string.cd_customize_cards)
                        )
                    }
                }
            )
        }
    ) { innerPadding ->
        Box(modifier = Modifier.fillMaxSize().padding(innerPadding).transformable(state = transformableState)) {
            Box(
                    modifier =
                            Modifier.fillMaxSize()
                                    .horizontalScroll(hScrollState)
                                    .verticalScroll(vScrollState)
                                    .onGloballyPositioned { canvasTopLeftPx = it.positionInRoot() }
            ) {
                Box(
                        modifier =
                                Modifier.size(2000.dp, 2000.dp).graphicsLayer {
                                    scaleX = canvasScale
                                    scaleY = canvasScale
                                    transformOrigin = androidx.compose.ui.graphics.TransformOrigin(0f, 0f)
                                }
                ) {
                    val visibleCards = uiState.cards.filter { uiState.enabledCards.contains(it.id) }
                    visibleCards.forEach { card ->
                        val xPx = (card.xDp * density).roundToInt()
                        val yPx = (card.yDp * density).roundToInt()

                        val cardShapePreset =
                                if (card.type == HomeCardType.PC_STATUS) uiState.pcCardShapePreset
                                else uiState.telemetryCardShapePreset

                        val adaptivePadding = calculateAdaptivePadding(cardShapePreset)

                        Card(
                                modifier =
                                        Modifier.offset { IntOffset(xPx, yPx) }
                                                .width(card.widthDp.dp)
                                                .height(card.heightDp.dp)
                                                .pointerInput(card.id) {
                                                    detectDragGestures(
                                                            onDrag = { change, dragAmount ->
                                                                change.consume()
                                                                val currentScale =
                                                                        canvasScaleState.floatValue
                                                                onMoveCard(
                                                                        card.id,
                                                                        dragAmount.x /
                                                                                (density *
                                                                                        currentScale),
                                                                        dragAmount.y /
                                                                                (density *
                                                                                        currentScale)
                                                                )
                                                            },
                                                            onDragEnd = { onSaveCardLayout() }
                                                    )
                                                },
                                shape = cardShape(cardShapePreset, uiState.cornerRadius),
                                colors =
                                        CardDefaults.cardColors(
                                                containerColor =
                                                        MaterialTheme.colorScheme.surfaceContainer
                                                                .copy(alpha = uiState.cardOpacity)
                                        )
                        ) {
                            Box(modifier = Modifier.fillMaxSize().padding(adaptivePadding)) {
                                when (card.type) {
                                    HomeCardType.PC_STATUS -> {
                                        ConnectionOrbCard(
                                                isConnected = uiState.isConnected,
                                                isConnecting = uiState.isConnecting,
                                                shapePreset = uiState.pcCardShapePreset,
                                                cornerRadius = uiState.cornerRadius,
                                                onToggle = {
                                                    view.performHapticFeedback(
                                                            HapticFeedbackConstants.CONFIRM
                                                    )
                                                    onToggleConnection()
                                                },
                                                onNavigateToConnection = onNavigateToConnection
                                        )
                                    }
                                    HomeCardType.WAKE_ON_LAN -> {
                                        WakeOnLanCard(onWake = { onWakePc() })
                                    }
                                    HomeCardType.TELEMETRY -> {
                                        val sensorId = card.sensorId ?: ""
                                        val sensor = sensorMap[sensorId]
                                        val history = uiState.telemetryHistory[sensorId].orEmpty()
                                        TelemetryCardContent(
                                                title = card.title,
                                                sensor = sensor,
                                                history = history,
                                                mode = card.displayMode,
                                                onCycleDisplayMode = {
                                                    onCycleTelemetryDisplayMode(card.id)
                                                },
                                                isExpressiveShape = cardShapePreset > 0,
                                                outerPadding = adaptivePadding
                                        )
                                    }
                                }

                                IconButton(
                                        onClick = {
                                            view.performHapticFeedback(
                                                    HapticFeedbackConstants.KEYBOARD_TAP
                                            )
                                            onSetCardEnabled(card.id, false)
                                            onSaveCardLayout()
                                        },
                                        modifier =
                                                Modifier.align(Alignment.TopStart)
                                                        .size(24.dp)
                                                        .padding(2.dp)
                                ) {
                                    Icon(
                                            Icons.Default.Close,
                                            contentDescription = stringResource(R.string.button_dismiss),
                                            modifier = Modifier.size(16.dp),
                                            tint = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }

                                Box(
                                        modifier =
                                                Modifier.align(Alignment.BottomEnd)
                                                        .size(24.dp)
                                                        .pointerInput(card.id) {
                                                            detectDragGestures(
                                                                    onDrag = { change, dragAmount ->
                                                                        change.consume()
                                                                        val currentScale =
                                                                                canvasScaleState
                                                                                        .floatValue
                                                                        onResizeCard(
                                                                                card.id,
                                                                                dragAmount.x /
                                                                                        (density *
                                                                                                currentScale),
                                                                                dragAmount.y /
                                                                                        (density *
                                                                                                currentScale)
                                                                        )
                                                                    },
                                                                    onDragEnd = { onSaveCardLayout() }
                                                            )
                                                        }
                                ) {
                                    Icon(
                                            Icons.AutoMirrored.Filled.ArrowForward,
                                            contentDescription = stringResource(R.string.cd_expand_icon),
                                            modifier =
                                                    Modifier.fillMaxSize()
                                                            .graphicsLayer { rotationZ = 45f },
                                            tint = MaterialTheme.colorScheme.primary.copy(alpha = 0.7f)
                                    )
                                }
                            }
                        }
                    }
                }
            }

            AnimatedVisibility(
                    visible = showCardDrawer,
                    enter = slideInHorizontally(initialOffsetX = { it }),
                    exit = slideOutHorizontally(targetOffsetX = { it }),
                    modifier = Modifier.align(Alignment.CenterEnd).fillMaxHeight().width(300.dp)
            ) {
                Surface(
                        tonalElevation = 8.dp,
                        modifier = Modifier.fillMaxSize().onGloballyPositioned { drawerLeftPx = it.positionInRoot().x },
                        color = MaterialTheme.colorScheme.surfaceContainerHigh
                ) {
                    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
                        Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                    stringResource(R.string.dashboard_card_drawer_title),
                                    style = MaterialTheme.typography.titleLarge,
                                    fontWeight = FontWeight.Bold
                            )
                            IconButton(
                                    onClick = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.KEYBOARD_TAP
                                        )
                                        showCardDrawer = false
                                    }
                            ) {
                                Icon(Icons.Default.Close, contentDescription = stringResource(R.string.button_dismiss))
                            }
                        }

                        Text(
                                stringResource(R.string.dashboard_card_drawer_hint),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(vertical = 8.dp)
                        )

                        LazyColumn(
                                modifier = Modifier.weight(1f),
                                verticalArrangement = Arrangement.spacedBy(10.dp)
                        ) {
                            items(availableCards, key = { it.id }) { availableCard ->
                                var itemTopLeftPx by remember { mutableStateOf(Offset.Zero) }
                                Card(
                                        modifier =
                                                Modifier.fillMaxWidth()
                                                        .onGloballyPositioned {
                                                            itemTopLeftPx = it.positionInRoot()
                                                        }
                                                        .pointerInput(
                                                                availableCard.id,
                                                                density,
                                                                drawerLeftPx,
                                                                canvasTopLeftPx,
                                                                uiState.cards
                                                        ) {
                                                            detectDragGesturesAfterLongPress(
                                                                    onDragStart = { startOffset ->
                                                                        view.performHapticFeedback(
                                                                                HapticFeedbackConstants.LONG_PRESS
                                                                        )
                                                                        draggingCardId =
                                                                                availableCard.id
                                                                        draggingPointerPx =
                                                                                itemTopLeftPx +
                                                                                        startOffset
                                                                    },
                                                                    onDrag = { change, dragAmount ->
                                                                        change.consume()
                                                                        draggingPointerPx +=
                                                                                dragAmount
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
                                                                                    uiState.cards
                                                                                            .firstOrNull {
                                                                                                it.id ==
                                                                                                        draggingId
                                                                                            }
                                                                                            ?.let {
                                                                                                CardSizeDp(
                                                                                                        it.widthDp.toInt(),
                                                                                                        it.heightDp.toInt()
                                                                                                )
                                                                                            }
                                                                                            ?: defaultCardSizeFor(
                                                                                                    draggingId
                                                                                            )

                                                                            val dropCenterX =
                                                                                    draggingPointerPx
                                                                                            .x -
                                                                                            canvasTopLeftPx
                                                                                                    .x
                                                                            val dropCenterY =
                                                                                    draggingPointerPx
                                                                                            .y -
                                                                                            canvasTopLeftPx
                                                                                                    .y

                                                                            val dropX =
                                                                                    (dropCenterX /
                                                                                            (density *
                                                                                                    canvasScale)) -
                                                                                            (dragSize
                                                                                                    .width /
                                                                                                    2f)
                                                                            val dropY =
                                                                                    (dropCenterY /
                                                                                            (density *
                                                                                                    canvasScale)) -
                                                                                            (dragSize
                                                                                                    .height /
                                                                                                    2f)

                                                                            onSetCardEnabled(
                                                                                    draggingId,
                                                                                    true
                                                                            )
                                                                            onPlaceCardAt(
                                                                                    draggingId,
                                                                                    dropX,
                                                                                    dropY
                                                                            )
                                                                            onSaveCardLayout()
                                                                            view.performHapticFeedback(
                                                                                    HapticFeedbackConstants.CONFIRM
                                                                            )
                                                                        }
                                                                        draggingCardId = null
                                                                    },
                                                                    onDragCancel = {
                                                                        draggingCardId = null
                                                                    }
                                                            )
                                                        },
                                        colors =
                                                CardDefaults.cardColors(
                                                        containerColor =
                                                                MaterialTheme.colorScheme
                                                                        .surfaceContainerLow
                                                ),
                                        onClick = {
                                            view.performHapticFeedback(
                                                    HapticFeedbackConstants.KEYBOARD_TAP
                                            )
                                            val checked =
                                                    !uiState.enabledCards.contains(availableCard.id)
                                            onSetCardEnabled(availableCard.id, checked)
                                        }
                                ) {
                                    Row(
                                            modifier = Modifier.fillMaxWidth().padding(10.dp),
                                            verticalAlignment = Alignment.CenterVertically,
                                            horizontalArrangement = Arrangement.spacedBy(10.dp)
                                    ) {
                                        val checked = uiState.enabledCards.contains(availableCard.id)
                                        Icon(
                                                imageVector =
                                                        if (checked) Icons.Default.CheckBox
                                                        else Icons.Default.CheckBoxOutlineBlank,
                                                contentDescription = stringResource(R.string.cd_card_enabled)
                                        )
                                        Column(modifier = Modifier.weight(1f)) {
                                            Text(
                                                    availableCard.title,
                                                    fontWeight = FontWeight.SemiBold
                                            )
                                            Text(
                                                    availableCard.subtitle,
                                                    style = MaterialTheme.typography.bodySmall,
                                                    color =
                                                            MaterialTheme.colorScheme
                                                                    .onSurfaceVariant
                                            )
                                        }
                                        Icon(Icons.Default.DragHandle, contentDescription = null)
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Dragging ghost overlay
            if (draggingCardId != null && draggingCard != null) {
                val ghostSize = draggingCardSize ?: defaultCardSizeFor(draggingCard.id)
                Box(
                        modifier =
                                Modifier.offset {
                                            IntOffset(
                                                    draggingPointerPx.x.roundToInt(),
                                                    draggingPointerPx.y.roundToInt()
                                            )
                                        }
                                        .graphicsLayer {
                                            translationX = -(ghostSize.width * density) / 2f
                                            translationY = -(ghostSize.height * density) / 2f
                                            alpha = 0.7f
                                            scaleX = canvasScale
                                            scaleY = canvasScale
                                        }
                                        .width(ghostSize.width.dp)
                                        .height(ghostSize.height.dp)
                                        .background(
                                                MaterialTheme.colorScheme.primary.copy(alpha = 0.4f),
                                                cardShape(0f, uiState.cornerRadius)
                                        )
                                        .border(
                                                2.dp,
                                                MaterialTheme.colorScheme.primary,
                                                cardShape(0f, uiState.cornerRadius)
                                        ),
                        contentAlignment = Alignment.Center
                ) {
                    Text(
                            stringResource(R.string.dashboard_drop_to_place, draggingCard.title),
                            style = MaterialTheme.typography.labelSmall,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.primary,
                            textAlign = TextAlign.Center
                    )
                }
            }
        }
    }
}

private data class DashboardAvailableCardItem(val id: String, val title: String, val subtitle: String)

private data class CardSizeDp(val width: Int, val height: Int)

private fun defaultCardSizeFor(id: String): CardSizeDp {
    return when {
        id == "pc_status" -> CardSizeDp(160, 160)
        id == "wake_pc" -> CardSizeDp(160, 80)
        else -> CardSizeDp(160, 100) // Telemetry cards
    }
}

@Composable
private fun ConnectionOrbCard(
        isConnected: Boolean,
        isConnecting: Boolean,
        shapePreset: Float,
        cornerRadius: Int,
        onToggle: () -> Unit,
        onNavigateToConnection: () -> Unit
) {
    val infiniteTransition = rememberInfiniteTransition(label = "orb")
    val pulseAlpha by
            infiniteTransition.animateFloat(
                    initialValue = 0.3f,
                    targetValue = 0.8f,
                    animationSpec =
                            infiniteRepeatable(
                                    animation = tween(1500, easing = LinearEasing),
                                    repeatMode = RepeatMode.Reverse
                            ),
                    label = "pulse"
            )

    val orbColor by
            animateColorAsState(
                    targetValue =
                            when {
                                isConnected -> MaterialTheme.colorScheme.primary
                                isConnecting -> MaterialTheme.colorScheme.secondary
                                else -> MaterialTheme.colorScheme.error
                            },
                    label = "color"
            )

    Column(
            modifier = Modifier.fillMaxSize().padding(8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
    ) {
        Box(
                modifier =
                        Modifier.size(70.dp)
                                .drawBehind {
                                    if (isConnecting) {
                                        drawCircle(
                                                color = orbColor,
                                                radius = size.minDimension / 2f + 10f,
                                                alpha = pulseAlpha * 0.4f
                                        )
                                    }
                                }
                                .background(orbColor, CircleShape)
                                .clickable { onToggle() },
                contentAlignment = Alignment.Center
        ) {
            Icon(
                    imageVector = if (isConnected) Icons.Default.DesktopWindows else Icons.Default.DesktopAccessDisabled,
                    contentDescription = null,
                    tint = Color.White,
                    modifier = Modifier.size(32.dp)
            )
        }

        Spacer(modifier = Modifier.height(12.dp))

        Text(
                text =
                        if (isConnected) stringResource(R.string.dashboard_host_online)
                        else if (isConnecting) stringResource(R.string.dashboard_connecting)
                        else stringResource(R.string.dashboard_tap_to_connect),
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.ExtraBold,
                color = orbColor
        )

        if (!isConnected && !isConnecting) {
            TextButton(onClick = onNavigateToConnection) {
                Text(stringResource(R.string.button_setup_connection), style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

@Composable
private fun WakeOnLanCard(onWake: () -> Unit) {
    val view = LocalView.current
    Column(
            modifier = Modifier.fillMaxSize().padding(8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
    ) {
        Button(
                onClick = {
                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                    onWake()
                },
                modifier = Modifier.fillMaxWidth()
        ) {
            Icon(Icons.Default.PowerSettingsNew, contentDescription = null)
            Spacer(modifier = Modifier.width(8.dp))
            Text(stringResource(R.string.dashboard_wake_pc_button))
        }
        Text(
                stringResource(R.string.dashboard_wake_pc_subtitle),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun TelemetryCardContent(
        title: String,
        sensor: TelemetrySensor?,
        history: List<Float>,
        mode: TelemetryDisplayMode,
        onCycleDisplayMode: () -> Unit,
        isExpressiveShape: Boolean,
        outerPadding: androidx.compose.ui.unit.Dp = 0.dp
) {
    val view = LocalView.current
    Column(
            modifier =
                    Modifier.fillMaxSize().clickable {
                        view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                        onCycleDisplayMode()
                    }
    ) {
        // P7: Adaptive internal title padding.
        // The dismiss button is at TopStart. We need room for it (approx 24dp from left).
        // If outerPadding is already large (e.g. 16dp), we need less extra start padding here.
        val titleStartPadding = maxOf(0.dp, 24.dp - outerPadding)

        Row(
                modifier = Modifier.fillMaxWidth().padding(start = titleStartPadding, end = 4.dp, top = 2.dp, bottom = 2.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                    text = title.uppercase(),
                    style = MaterialTheme.typography.labelSmall,
                    fontWeight = FontWeight.ExtraBold,
                    color = MaterialTheme.colorScheme.primary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f)
            )
            Icon(
                    imageVector = Icons.Default.Timeline,
                    contentDescription = stringResource(R.string.cd_change_display_mode),
                    modifier = Modifier.size(10.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
            )
        }

        Box(modifier = Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            if (sensor == null) {
                Text(
                        stringResource(R.string.dashboard_collecting_data),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            } else {
                when (mode) {
                    TelemetryDisplayMode.VALUE -> {
                        Text(
                                text = formatSensorValue(sensor.value, sensor.unit),
                                style =
                                        if (isExpressiveShape) MaterialTheme.typography.headlineSmall
                                        else MaterialTheme.typography.headlineMedium,
                                fontWeight = FontWeight.Black,
                                color = MaterialTheme.colorScheme.onSurface
                        )
                    }
                    TelemetryDisplayMode.GAUGE -> {
                        Sparkline(
                                data = history,
                                modifier = Modifier.fillMaxSize().padding(8.dp),
                                color = MaterialTheme.colorScheme.primary,
                                isArea = false
                        )
                    }
                    TelemetryDisplayMode.LINE -> {
                        Sparkline(
                                data = history,
                                modifier = Modifier.fillMaxSize().padding(8.dp),
                                color = MaterialTheme.colorScheme.primary,
                                isArea = false
                        )
                    }
                    TelemetryDisplayMode.BAR -> {
                        BarChart(
                                data = history,
                                modifier = Modifier.fillMaxSize().padding(8.dp),
                                color = MaterialTheme.colorScheme.primary
                        )
                    }
                    TelemetryDisplayMode.AREA -> {
                        Sparkline(
                                data = history,
                                modifier = Modifier.fillMaxSize().padding(8.dp),
                                color = MaterialTheme.colorScheme.primary,
                                isArea = true
                        )
                    }
                    TelemetryDisplayMode.CIRCLE_GAUGE -> {
                        CircleGauge(
                                value = sensor.value.toFloat(),
                                modifier = Modifier.size(80.dp),
                                color = MaterialTheme.colorScheme.primary
                        )
                    }
                }
            }
        }
    }
}

private fun formatSensorValue(value: Double, unit: String): String {
    if (value.isNaN()) return "--"
    
    val absoluteValue = abs(value)
    val formatted = when {
        absoluteValue >= 1_000_000_000_000.0 -> "%.1fT".format(value / 1_000_000_000_000.0)
        absoluteValue >= 1_000_000_000.0 -> "%.1fG".format(value / 1_000_000_000.0)
        absoluteValue >= 1_000_000.0 -> "%.1fM".format(value / 1_000_000.0)
        absoluteValue >= 10_000.0 -> "%.1fK".format(value / 1000.0)
        absoluteValue >= 100.0 -> value.roundToInt().toString()
        absoluteValue >= 1.0 -> "%.1f".format(value)
        else -> "%.2f".format(value)
    }
    
    return if (unit.isNotBlank()) "$formatted$unit" else formatted
}

@Composable
fun CircleGauge(value: Float, modifier: Modifier = Modifier, color: Color = Color.Cyan) {
    val animatedValue by animateFloatAsState(targetValue = value, label = "gauge")
    val strokeWidth = 10f
    
    Box(modifier = modifier, contentAlignment = Alignment.Center) {
        Canvas(modifier = Modifier.fillMaxSize()) {
            // Track
            drawArc(
                color = color.copy(alpha = 0.1f),
                startAngle = 135f,
                sweepAngle = 270f,
                useCenter = false,
                style = Stroke(width = strokeWidth, cap = StrokeCap.Round)
            )
            // Progress
            drawArc(
                color = color,
                startAngle = 135f,
                sweepAngle = (animatedValue.coerceIn(0f, 100f) / 100f) * 270f,
                useCenter = false,
                style = Stroke(width = strokeWidth, cap = StrokeCap.Round)
            )
        }
        Text(
            text = "${value.roundToInt()}%",
            style = MaterialTheme.typography.labelSmall,
            fontWeight = FontWeight.Bold,
            color = color
        )
    }
}

@Composable
fun BarChart(data: List<Float>, modifier: Modifier = Modifier, color: Color = Color.Cyan) {
    Canvas(modifier = modifier) {
        if (data.isEmpty()) return@Canvas
        val max = data.maxOrNull()?.coerceAtLeast(1f) ?: 1f
        val barWidth = size.width / (data.size.coerceAtLeast(1) * 1.5f)
        val spacing = barWidth / 2f
        
        data.forEachIndexed { index, value ->
            val barHeight = (value / max) * size.height
            val x = index * (barWidth + spacing)
            drawRect(
                color = color.copy(alpha = 0.6f + (index.toFloat() / data.size) * 0.4f),
                topLeft = Offset(x, size.height - barHeight),
                size = androidx.compose.ui.geometry.Size(barWidth, barHeight)
            )
        }
    }
}

@Composable
fun Sparkline(data: List<Float>, modifier: Modifier = Modifier, color: Color = Color.Cyan, isArea: Boolean = false) {
    val lineColor by animateColorAsState(targetValue = color, label = "sparkline")
    Canvas(modifier = modifier) {
        if (data.size < 2) return@Canvas
        val path = Path()
        val areaPath = Path()
        
        val max = data.maxOrNull() ?: 100f
        val min = data.minOrNull() ?: 0f
        val range = (max - min).coerceAtLeast(1f)

        data.forEachIndexed { index, value ->
            val x = index * (size.width / (data.size - 1))
            val normalized = (value - min) / range
            val y = size.height - (normalized * size.height)
            
            if (index == 0) {
                path.moveTo(x, y)
                if (isArea) areaPath.moveTo(x, size.height)
                if (isArea) areaPath.lineTo(x, y)
            } else {
                path.lineTo(x, y)
                if (isArea) areaPath.lineTo(x, y)
            }
            
            if (isArea && index == data.size - 1) {
                areaPath.lineTo(x, size.height)
                areaPath.close()
            }
        }
        
        if (isArea) {
            drawPath(
                path = areaPath,
                brush = androidx.compose.ui.graphics.Brush.verticalGradient(
                    colors = listOf(lineColor.copy(alpha = 0.4f), Color.Transparent)
                )
            )
        }
        
        drawPath(path = path, color = lineColor, style = Stroke(width = 4f, cap = StrokeCap.Round))
    }
}
