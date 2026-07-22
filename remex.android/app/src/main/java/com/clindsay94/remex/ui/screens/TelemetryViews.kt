package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.telemetry.MetricKind
import kotlin.math.abs

/** Resolves the a11y content description for a view; a plain fun (not stringResource) so it's callable from Modifier.semantics {}. */
private fun cdView(context: android.content.Context, resId: Int, sensor: TelemetrySensor?): String =
    context.getString(resId, formatSensor(sensor).text)

@Composable
private fun CollectingData() {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        RemexLoadingIndicator(modifier = Modifier.size(16.dp))
        Text(
            text = stringResource(R.string.dashboard_collecting_data),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * Swaps between [content] (the chart) and a motion-carrying [CollectingData] placeholder,
 * fading through rather than the bare early-return/if-else pop each view previously did.
 */
@Composable
private fun ChartOrCollecting(hasData: Boolean, modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    val motionScheme = MaterialTheme.motionScheme
    AnimatedContent(
        targetState = hasData,
        transitionSpec = {
            val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
            fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
        },
        modifier = modifier,
        label = "chartOrCollecting"
    ) { ready ->
        if (ready) content() else CollectingData()
    }
}

// ── The 10-view catalog ──────────────────────────────────────────────────────────────

@Composable
fun ValueTrendView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val formatted = formatSensor(sensor)
    val delta = trendDelta(history)
    val sign = when { delta > 0f -> 1; delta < 0f -> -1; else -> 0 }
    Column(
        modifier = modifier.semantics { contentDescription = cdView(context, R.string.cd_view_value, sensor) },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text(formatted.text, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Black)
        if (sign != 0) {
            val color = trendColor(sensor?.kind ?: MetricKind.UNKNOWN, sign)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(if (sign > 0) "▲" else "▼", color = color, style = MaterialTheme.typography.labelSmall)
                Spacer(Modifier.width(4.dp))
                Text("%.1f".format(abs(delta)), color = color, style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

@Composable
fun ValueSparkView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    Column(
        modifier = modifier.semantics { contentDescription = cdView(context, R.string.cd_view_value_spark, sensor) },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Text(formatSensor(sensor).text, style = MaterialTheme.typography.titleLargeEmphasized)
        ChartOrCollecting(hasData = history.size >= 2) {
            val s = rememberAutoscale(history)
            val c = MaterialTheme.colorScheme.primary
            Canvas(Modifier.fillMaxWidth().height(24.dp)) {
                val stepX = size.width / (history.size - 1)
                val p = Path()
                history.forEachIndexed { i, v ->
                    val y = size.height - ((v - s.low) / s.range) * size.height
                    if (i == 0) p.moveTo(0f, y) else p.lineTo(i * stepX, y)
                }
                drawPath(p, c, style = Stroke(width = 2f, cap = StrokeCap.Round))
            }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RingGaugeView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val fraction = gaugeFraction(sensor, history)
    val animatedProgress by animateFloatAsState(
        targetValue = fraction,
        animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
        label = "ring_gauge_progress"
    )
    Box(modifier.semantics { contentDescription = cdView(context, R.string.cd_view_ring_gauge, sensor) }) {
        RemexCircularWavyGauge(
            progress = animatedProgress,
            centerLabel = formatSensor(sensor).text,
            modifier = Modifier.fillMaxWidth()
        )
    }
}

@Composable
fun ArcGaugeView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val track = MaterialTheme.colorScheme.surfaceVariant
    val fill = MaterialTheme.colorScheme.primary
    val fraction = gaugeFraction(sensor, history)
    Box(modifier, contentAlignment = Alignment.Center) {
        Canvas(
            Modifier.fillMaxWidth().aspectRatio(1f)
                .semantics { contentDescription = cdView(context, R.string.cd_view_arc_gauge, sensor) }
        ) {
            val stroke = Stroke(width = 12f, cap = StrokeCap.Round)
            drawArc(track, 135f, 270f, false, style = stroke)
            drawArc(fill, 135f, 270f * fraction, false, style = stroke)
        }
        Text(formatSensor(sensor).text, style = MaterialTheme.typography.titleMediumEmphasized)
    }
}

/** Small corner chip for the pure-chart views (Line/Area/Bar), which otherwise show no number at all. */
@Composable
private fun BoxScope.ValueOverlayChip(sensor: TelemetrySensor?) {
    Surface(
        shape = MaterialTheme.shapes.small,
        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.85f),
        modifier = Modifier.align(Alignment.TopEnd).padding(4.dp)
    ) {
        Text(
            formatSensor(sensor).text,
            style = MaterialTheme.typography.labelSmallEmphasized,
            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
        )
    }
}

@Composable
fun LineChartView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier, showValue: Boolean = false) {
    val context = LocalContext.current
    ChartOrCollecting(hasData = history.size >= 2, modifier = modifier) {
        val s = rememberAutoscale(history)
        val c = MaterialTheme.colorScheme.primary
        Box {
            Canvas(
                Modifier.fillMaxWidth().height(56.dp)
                    .semantics { contentDescription = cdView(context, R.string.cd_view_line, sensor) }
            ) {
                val stepX = size.width / (history.size - 1)
                val p = Path()
                history.forEachIndexed { i, v ->
                    val y = size.height - ((v - s.low) / s.range) * size.height
                    if (i == 0) p.moveTo(0f, y) else p.lineTo(i * stepX, y)
                }
                drawPath(p, c, style = Stroke(width = 4f, cap = StrokeCap.Round))
            }
            if (showValue) ValueOverlayChip(sensor)
        }
    }
}

@Composable
fun AreaChartView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier, showValue: Boolean = false) {
    val context = LocalContext.current
    ChartOrCollecting(hasData = history.size >= 2, modifier = modifier) {
        val s = rememberAutoscale(history)
        val c = MaterialTheme.colorScheme.primary
        Box {
            Canvas(
                Modifier.fillMaxWidth().height(56.dp)
                    .semantics { contentDescription = cdView(context, R.string.cd_view_area, sensor) }
            ) {
                val stepX = size.width / (history.size - 1)
                val p = Path().apply {
                    history.forEachIndexed { i, v ->
                        val y = size.height - ((v - s.low) / s.range) * size.height
                        if (i == 0) moveTo(0f, y) else lineTo(i * stepX, y)
                    }
                    lineTo(size.width, size.height); lineTo(0f, size.height); close()
                }
                drawPath(p, brush = Brush.verticalGradient(listOf(c.copy(alpha = 0.55f), c.copy(alpha = 0f))))
            }
            if (showValue) ValueOverlayChip(sensor)
        }
    }
}

@Composable
fun BarHistogramView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier, showValue: Boolean = false) {
    val context = LocalContext.current
    ChartOrCollecting(hasData = history.isNotEmpty(), modifier = modifier) {
        val s = rememberAutoscale(history)
        val c = MaterialTheme.colorScheme.primary
        Box {
            Canvas(
                Modifier.fillMaxWidth().height(56.dp)
                    .semantics { contentDescription = cdView(context, R.string.cd_view_bar, sensor) }
            ) {
                val stepX = size.width / history.size
                val barW = stepX * 0.7f
                history.forEachIndexed { i, v ->
                    val h = ((v - s.low) / s.range) * size.height
                    drawRoundRect(
                        color = c,
                        topLeft = Offset(i * stepX + (stepX - barW) / 2f, size.height - h),
                        size = Size(barW, h),
                        cornerRadius = CornerRadius(3f, 3f)
                    )
                }
            }
            if (showValue) ValueOverlayChip(sensor)
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun HuePulseTile(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val fraction = gaugeFraction(sensor, history)
    val animatedFraction by animateFloatAsState(
        targetValue = fraction,
        animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
        label = "hue_pulse_fraction"
    )
    val cool = MaterialTheme.colorScheme.primary
    val warm = MaterialTheme.colorScheme.error
    val tint = lerp(cool, warm, animatedFraction)
    Box(
        modifier = modifier.fillMaxSize()
            .clip(MaterialTheme.shapes.medium)
            .background(tint.copy(alpha = 0.28f))
            .semantics { contentDescription = cdView(context, R.string.cd_view_hue_pulse, sensor) },
        contentAlignment = Alignment.Center
    ) {
        Text(formatSensor(sensor).text, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Black, color = tint)
    }
}

@Composable
fun LedMeterView(sensor: TelemetrySensor?, history: List<Float>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val fraction = gaugeFraction(sensor, history)
    val lit = MaterialTheme.colorScheme.primary
    val hot = MaterialTheme.colorScheme.error
    val unlit = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.35f)
    Column(
        modifier = modifier.semantics { contentDescription = cdView(context, R.string.cd_view_led_meter, sensor) },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Canvas(Modifier.fillMaxWidth().height(56.dp)) {
            val segmentCount = 12
            val segmentHeight = size.height / segmentCount
            val gap = segmentHeight * 0.15f
            val litCount = (fraction * segmentCount).toInt().coerceIn(0, segmentCount)
            val hotThreshold = (segmentCount * 0.8f).toInt()
            for (fromBottom in 0 until segmentCount) {
                val isLit = fromBottom < litCount
                val color = if (!isLit) unlit else if (fromBottom >= hotThreshold) hot else lit
                val top = size.height - (fromBottom + 1) * segmentHeight + gap / 2f
                drawRoundRect(
                    color = color,
                    topLeft = Offset(0f, top),
                    size = Size(size.width, segmentHeight - gap),
                    cornerRadius = CornerRadius(3f, 3f)
                )
            }
        }
        Text(formatSensor(sensor).text, style = MaterialTheme.typography.labelLargeEmphasized)
    }
}

@Composable
fun DualMetricOverlay(
    primary: TelemetrySensor?,
    primaryHistory: List<Float>,
    secondary: TelemetrySensor?,
    secondaryHistory: List<Float>,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    ChartOrCollecting(hasData = primaryHistory.size >= 2, modifier = modifier) {
    val sPrimary = rememberAutoscale(primaryHistory)
    val primaryColor = MaterialTheme.colorScheme.primary
    val secondaryColor = MaterialTheme.colorScheme.tertiary
    Box(Modifier.semantics { contentDescription = cdView(context, R.string.cd_view_dual_metric, primary) }) {
        Canvas(Modifier.fillMaxWidth().height(56.dp)) {
            val stepX = size.width / (primaryHistory.size - 1)
            val areaPath = Path().apply {
                primaryHistory.forEachIndexed { i, v ->
                    val y = size.height - ((v - sPrimary.low) / sPrimary.range) * size.height
                    if (i == 0) moveTo(0f, y) else lineTo(i * stepX, y)
                }
                lineTo(size.width, size.height); lineTo(0f, size.height); close()
            }
            drawPath(areaPath, brush = Brush.verticalGradient(listOf(primaryColor.copy(alpha = 0.45f), primaryColor.copy(alpha = 0f))))

            if (secondaryHistory.size >= 2) {
                val sSecondary = rememberAutoscale(secondaryHistory)
                val stepX2 = size.width / (secondaryHistory.size - 1)
                val linePath = Path()
                secondaryHistory.forEachIndexed { i, v ->
                    val y = size.height - ((v - sSecondary.low) / sSecondary.range) * size.height
                    if (i == 0) linePath.moveTo(0f, y) else linePath.lineTo(i * stepX2, y)
                }
                drawPath(linePath, secondaryColor, style = Stroke(width = 3f, cap = StrokeCap.Round))
            }
        }
        Surface(
            shape = MaterialTheme.shapes.small,
            color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.85f),
            modifier = Modifier.align(Alignment.TopEnd).padding(4.dp)
        ) {
            Column(Modifier.padding(4.dp)) {
                Text(formatSensor(primary).text, style = MaterialTheme.typography.labelSmallEmphasized, color = primaryColor)
                if (secondary != null) {
                    Text(formatSensor(secondary).text, style = MaterialTheme.typography.labelSmallEmphasized, color = secondaryColor)
                }
            }
        }
    }
    }
}

// ── Dispatcher - shared by the full card and the picker thumbnails ──────────────────

@Composable
fun TelemetryViewDispatch(
    mode: TelemetryDisplayMode,
    sensor: TelemetrySensor?,
    history: List<Float>,
    secondarySensor: TelemetrySensor?,
    secondaryHistory: List<Float>,
    modifier: Modifier = Modifier,
    showValueOverlay: Boolean = false
) {
    val effective = if (mode == TelemetryDisplayMode.AUTO) bestDisplayModeFor(sensor) else mode
    when (effective) {
        TelemetryDisplayMode.VALUE -> ValueTrendView(sensor, history, modifier)
        TelemetryDisplayMode.VALUE_SPARK -> ValueSparkView(sensor, history, modifier)
        TelemetryDisplayMode.RING_GAUGE -> RingGaugeView(sensor, history, modifier)
        TelemetryDisplayMode.ARC_GAUGE -> ArcGaugeView(sensor, history, modifier)
        TelemetryDisplayMode.LINE -> LineChartView(sensor, history, modifier, showValueOverlay)
        TelemetryDisplayMode.AREA -> AreaChartView(sensor, history, modifier, showValueOverlay)
        TelemetryDisplayMode.BAR -> BarHistogramView(sensor, history, modifier, showValueOverlay)
        TelemetryDisplayMode.HUE_PULSE -> HuePulseTile(sensor, history, modifier)
        TelemetryDisplayMode.LED_METER -> LedMeterView(sensor, history, modifier)
        TelemetryDisplayMode.DUAL_METRIC ->
            if (secondarySensor != null) DualMetricOverlay(sensor, history, secondarySensor, secondaryHistory, modifier)
            else ValueSparkView(sensor, history, modifier)
        TelemetryDisplayMode.AUTO -> {} // unreachable - `effective` is never AUTO
    }
}

// ── View picker sheet ─────────────────────────────────────────────────────────────

private data class ViewCatalogEntry(val mode: TelemetryDisplayMode, val labelRes: Int)

private val VIEW_CATALOG = listOf(
    ViewCatalogEntry(TelemetryDisplayMode.VALUE, R.string.dashboard_view_big_value),
    ViewCatalogEntry(TelemetryDisplayMode.VALUE_SPARK, R.string.dashboard_view_value_spark),
    ViewCatalogEntry(TelemetryDisplayMode.RING_GAUGE, R.string.dashboard_view_ring_gauge),
    ViewCatalogEntry(TelemetryDisplayMode.ARC_GAUGE, R.string.dashboard_view_arc_gauge),
    ViewCatalogEntry(TelemetryDisplayMode.LINE, R.string.dashboard_view_line),
    ViewCatalogEntry(TelemetryDisplayMode.AREA, R.string.dashboard_view_area),
    ViewCatalogEntry(TelemetryDisplayMode.BAR, R.string.dashboard_view_bar),
    ViewCatalogEntry(TelemetryDisplayMode.HUE_PULSE, R.string.dashboard_view_hue_pulse),
    ViewCatalogEntry(TelemetryDisplayMode.LED_METER, R.string.dashboard_view_led_meter),
    ViewCatalogEntry(TelemetryDisplayMode.DUAL_METRIC, R.string.dashboard_view_dual_metric),
)

@Composable
internal fun ViewPickerCell(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    preview: @Composable () -> Unit
) {
    val borderColor by animateColorAsState(
        targetValue = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "viewPickerBorderColor"
    )
    val borderWidth by animateDpAsState(
        targetValue = if (selected) 2.dp else 1.dp,
        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
        label = "viewPickerBorderWidth"
    )
    Column(
        modifier = modifier
            .clip(MaterialTheme.shapes.medium)
            .border(borderWidth, borderColor, MaterialTheme.shapes.medium)
            .selectable(selected = selected, role = Role.RadioButton, onClick = onClick)
            .padding(8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Box(Modifier.size(80.dp), contentAlignment = Alignment.Center) { preview() }
        Text(label, style = MaterialTheme.typography.labelSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DisplayModePickerSheet(
    cardId: String,
    sensor: TelemetrySensor?,
    history: List<Float>,
    currentMode: TelemetryDisplayMode,
    currentTitle: String,
    currentShowValueOverlay: Boolean,
    otherSensors: List<TelemetrySensor>,
    onDismiss: () -> Unit,
    onPickDisplayMode: (String, TelemetryDisplayMode, String?) -> Unit,
    onSetTitle: (String, String?) -> Unit,
    onSetValueOverlay: (String, Boolean) -> Unit
) {
    val view = LocalView.current
    val sheetState = rememberModalBottomSheetState()
    var awaitingSecondary by remember { mutableStateOf(false) }
    var titleDraft by remember(cardId) { mutableStateOf(currentTitle) }
    val focusManager = LocalFocusManager.current
    val motionScheme = MaterialTheme.motionScheme

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        if (!awaitingSecondary) {
            OutlinedTextField(
                value = titleDraft,
                onValueChange = { titleDraft = it },
                label = { Text(stringResource(R.string.dashboard_custom_title_label)) },
                placeholder = { Text(stringResource(R.string.dashboard_custom_title_hint)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                keyboardActions = KeyboardActions(onDone = {
                    onSetTitle(cardId, titleDraft)
                    focusManager.clearFocus()
                }),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)
            )
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)
                    .toggleable(
                        value = currentShowValueOverlay,
                        role = Role.Switch,
                        onValueChange = { enabled ->
                            view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                            onSetValueOverlay(cardId, enabled)
                        }
                    ),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(stringResource(R.string.dashboard_show_value_overlay), style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = currentShowValueOverlay,
                    onCheckedChange = null
                )
            }
        }
        AnimatedContent(
            targetState = awaitingSecondary,
            transitionSpec = {
                val spatialSpec = motionScheme.defaultSpatialSpec<IntOffset>()
                val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                if (targetState) {
                    (slideInHorizontally(spatialSpec) { it } + fadeIn(effectsSpec))
                        .togetherWith(slideOutHorizontally(spatialSpec) { -it } + fadeOut(effectsSpec))
                } else {
                    (slideInHorizontally(spatialSpec) { -it } + fadeIn(effectsSpec))
                        .togetherWith(slideOutHorizontally(spatialSpec) { it } + fadeOut(effectsSpec))
                }
            },
            label = "secondarySensorPane"
        ) { showSecondary ->
            if (showSecondary) {
                Column {
                    Text(
                        stringResource(R.string.dashboard_dual_metric_pick_secondary),
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)
                    )
                    LazyColumn(modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp)) {
                        items(otherSensors, key = { it.id }) { candidate ->
                            Text(
                                candidate.name,
                                style = MaterialTheme.typography.bodyLarge,
                                modifier = Modifier.fillMaxWidth()
                                    .animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec())
                                    .clickable {
                                        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                        onPickDisplayMode(cardId, TelemetryDisplayMode.DUAL_METRIC, candidate.id)
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp)
                            )
                        }
                    }
                }
            } else {
                Column {
                    Text(
                        stringResource(R.string.dashboard_view_picker_title),
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)
                    )
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(96.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                        modifier = Modifier.fillMaxWidth().padding(16.dp)
                    ) {
                        item {
                            ViewPickerCell(
                                label = stringResource(R.string.dashboard_view_auto),
                                selected = currentMode == TelemetryDisplayMode.AUTO,
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                    onPickDisplayMode(cardId, TelemetryDisplayMode.AUTO, null)
                                }
                            ) {
                                TelemetryViewDispatch(TelemetryDisplayMode.AUTO, sensor, history, null, emptyList(), Modifier.fillMaxSize())
                            }
                        }
                        items(VIEW_CATALOG, key = { it.mode }) { entry ->
                            ViewPickerCell(
                                label = stringResource(entry.labelRes),
                                selected = currentMode == entry.mode,
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                    if (entry.mode == TelemetryDisplayMode.DUAL_METRIC) {
                                        awaitingSecondary = true
                                    } else {
                                        onPickDisplayMode(cardId, entry.mode, null)
                                    }
                                },
                                modifier = Modifier.animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec())
                            ) {
                                TelemetryViewDispatch(entry.mode, sensor, history, null, emptyList(), Modifier.fillMaxSize())
                            }
                        }
                    }
                }
            }
        }
    }
}
