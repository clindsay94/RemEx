package com.clindsay94.remex.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlin.math.roundToInt

private data class AvailableCardItem(
    val id: String,
    val title: String,
    val subtitle: String
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    viewModel: DashboardViewModel = viewModel(),
    onMenuClick: () -> Unit = {}
) {
    val isConnected by viewModel.isConnected.collectAsState()
    val telemetrySensors by viewModel.telemetrySensors.collectAsState()
    val telemetryHistory by viewModel.telemetryHistory.collectAsState()
    val cards by viewModel.homeCards.collectAsState()
    val enabledCards by viewModel.enabledCardIds.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val cardOpacity by viewModel.cardOpacity.collectAsState()

    val availableCards = remember(telemetrySensors) {
        buildList {
            add(AvailableCardItem("pc_status", "PC Status", "Wake, connection state"))
            telemetrySensors.forEach { sensor ->
                add(
                    AvailableCardItem(
                        id = sensor.id,
                        title = sensor.name,
                        subtitle = sensor.category.ifBlank { "Telemetry" }
                    )
                )
            }
        }.distinctBy { it.id }
    }

    val density = androidx.compose.ui.platform.LocalDensity.current.density
    val sensorMap = remember(telemetrySensors) { telemetrySensors.associateBy { it.id } }

    var showCardDrawer by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("RemEx Home Base", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = onMenuClick) {
                        Icon(Icons.Default.Menu, contentDescription = "Menu")
                    }
                },
                actions = {
                    IconButton(onClick = { showCardDrawer = !showCardDrawer }) {
                        Icon(Icons.Default.Tune, contentDescription = "Customize cards")
                    }
                }
            )
        }
    ) { paddingValues ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues)
                .background(MaterialTheme.colorScheme.background)
        ) {
            cards.filter { enabledCards.contains(it.id) }.forEach { card ->
                val xPx = (card.xDp * density).roundToInt()
                val yPx = (card.yDp * density).roundToInt()

                Card(
                    modifier = Modifier
                        .offset { IntOffset(xPx, yPx) }
                        .width(card.widthDp.dp)
                        .height(card.heightDp.dp)
                        .pointerInput(card.id) {
                            detectDragGestures(
                                onDrag = { change, dragAmount ->
                                    change.consume()
                                    viewModel.moveCard(
                                        cardId = card.id,
                                        deltaXDp = dragAmount.x / density,
                                        deltaYDp = dragAmount.y / density
                                    )
                                },
                                onDragEnd = { viewModel.saveCardLayout() }
                            )
                        },
                    shape = RoundedCornerShape(cornerRadius.dp),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = cardOpacity)
                    )
                ) {
                    Box(modifier = Modifier.fillMaxSize()) {
                        when (card.type) {
                            HomeCardType.PC_STATUS -> {
                                PcStatusCardContent(
                                    isConnected = isConnected,
                                    onWakeClicked = { viewModel.wakePc() }
                                )
                            }

                            HomeCardType.TELEMETRY -> {
                                val sensor = sensorMap[card.sensorId]
                                val history = telemetryHistory[card.sensorId].orEmpty()
                                TelemetryCardContent(
                                    title = card.title,
                                    sensor = sensor,
                                    history = history,
                                    mode = card.displayMode,
                                    onCycleDisplayMode = { viewModel.cycleTelemetryDisplayMode(card.id) }
                                )
                            }
                        }

                        Box(
                            modifier = Modifier
                                .align(Alignment.BottomEnd)
                                .padding(6.dp)
                                .size(18.dp)
                                .clip(CircleShape)
                                .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.7f))
                                .pointerInput("resize_${card.id}") {
                                    detectDragGestures(
                                        onDrag = { change, dragAmount ->
                                            change.consume()
                                            viewModel.resizeCard(
                                                cardId = card.id,
                                                deltaWidthDp = dragAmount.x / density,
                                                deltaHeightDp = dragAmount.y / density
                                            )
                                        },
                                        onDragEnd = { viewModel.saveCardLayout() }
                                    )
                                }
                        )
                    }
                }
            }

            AnimatedVisibility(
                visible = showCardDrawer,
                modifier = Modifier.align(Alignment.CenterEnd)
            ) {
                Surface(
                    tonalElevation = 10.dp,
                    shadowElevation = 8.dp,
                    modifier = Modifier
                        .fillMaxHeight()
                        .width(300.dp)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(12.dp)
                    ) {
                        Text(
                            text = "Card Drawer",
                            style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.Bold
                        )
                        Text(
                            text = "Check to place card, uncheck to send it back to drawer.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 4.dp, bottom = 12.dp)
                        )

                        Column(
                            modifier = Modifier
                                .weight(1f)
                                .verticalScroll(rememberScrollState()),
                            verticalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            availableCards.forEach { availableCard ->
                                Card(
                                    modifier = Modifier.fillMaxWidth(),
                                    onClick = {
                                        val checked = !enabledCards.contains(availableCard.id)
                                        viewModel.setCardEnabled(availableCard.id, checked)
                                    }
                                ) {
                                    Row(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(10.dp),
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(10.dp)
                                    ) {
                                        val checked = enabledCards.contains(availableCard.id)
                                        Icon(
                                            imageVector = if (checked) Icons.Default.CheckBox else Icons.Default.CheckBoxOutlineBlank,
                                            contentDescription = null
                                        )
                                        Column(modifier = Modifier.weight(1f)) {
                                            Text(availableCard.title, fontWeight = FontWeight.SemiBold)
                                            Text(
                                                availableCard.subtitle,
                                                style = MaterialTheme.typography.bodySmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }
                                    }
                                }
                            }
                        }

                        Button(
                            onClick = { showCardDrawer = false },
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text("Done")
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun PcStatusCardContent(isConnected: Boolean, onWakeClicked: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(12.dp),
        verticalArrangement = Arrangement.SpaceBetween
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text("PC Status", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
            Text(
                text = if (isConnected) "Online" else "Offline",
                style = MaterialTheme.typography.headlineSmall,
                color = if (isConnected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error
            )
            Text(
                text = "Drag to move. Use the bottom-right handle to resize.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        if (!isConnected) {
            Button(onClick = onWakeClicked, modifier = Modifier.fillMaxWidth()) {
                Text("Wake on LAN")
            }
        }
    }
}

@Composable
private fun TelemetryCardContent(
    title: String,
    sensor: TelemetrySensor?,
    history: List<Float>,
    mode: TelemetryDisplayMode,
    onCycleDisplayMode: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(12.dp),
        verticalArrangement = Arrangement.SpaceBetween
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
            IconButton(onClick = onCycleDisplayMode) {
                Icon(Icons.Default.Tune, contentDescription = "Change display mode")
            }
        }

        val valueText = if (sensor == null) "--" else "${"%.1f".format(sensor.value)}${sensor.unit}"

        when (mode) {
            TelemetryDisplayMode.VALUE -> {
                Text(
                    valueText,
                    style = MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.Bold
                )
            }

            TelemetryDisplayMode.GAUGE -> {
                val percent = (sensor?.value ?: 0.0).toFloat().coerceIn(0f, 100f) / 100f
                Box(contentAlignment = Alignment.Center, modifier = Modifier.fillMaxWidth()) {
                    CircularProgressIndicator(progress = { percent }, modifier = Modifier.size(72.dp))
                    Text(valueText, style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.Bold)
                }
            }

            TelemetryDisplayMode.LINE -> {
                Sparkline(history = history)
                Text(
                    valueText,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.SemiBold
                )
            }
        }
    }
}

@Composable
private fun Sparkline(history: List<Float>) {
    if (history.size < 2) {
        Text(
            text = "Collecting data...",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        return
    }

    val high = history.maxOrNull() ?: 1f
    val low = history.minOrNull() ?: 0f
    val range = (high - low).takeIf { it > 0f } ?: 1f

    Canvas(
        modifier = Modifier
            .fillMaxWidth()
            .height(56.dp)
    ) {
        val stepX = size.width / (history.size - 1).coerceAtLeast(1)
        val path = Path()
        history.forEachIndexed { index, value ->
            val x = index * stepX
            val normalized = (value - low) / range
            val y = size.height - (normalized * size.height)
            if (index == 0) {
                path.moveTo(x, y)
            } else {
                path.lineTo(x, y)
            }
        }

        drawPath(
            path = path,
            color = androidx.compose.ui.graphics.Color.Cyan,
            style = androidx.compose.ui.graphics.drawscope.Stroke(width = 4f, cap = StrokeCap.Round)
        )
    }
}
