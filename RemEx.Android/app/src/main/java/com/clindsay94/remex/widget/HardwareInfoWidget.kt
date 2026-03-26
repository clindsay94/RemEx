package com.clindsay94.remex.widget

import android.content.Context
import android.util.Log
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.LocalSize
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.currentState
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import org.json.JSONObject

val SELECTED_SENSORS_KEY = stringPreferencesKey("selected_sensors")

private val SMALL = DpSize(110.dp, 40.dp)
private val MEDIUM = DpSize(180.dp, 100.dp)
private val LARGE = DpSize(250.dp, 160.dp)
private val XLARGE = DpSize(320.dp, 240.dp)

data class WidgetSensorData(
    val id: String,
    val name: String,
    val category: String,
    val value: Double,
    val unit: String
)

class HardwareInfoWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Responsive(setOf(SMALL, MEDIUM, LARGE, XLARGE))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val telemetryJson = WidgetDataCache.getTelemetryJson(context)
        val allSensors = parseTelemetry(telemetryJson)

        provideContent {
            GlanceTheme {
                HardwareInfoContent(allSensors)
            }
        }
    }

    private fun parseTelemetry(json: String?): List<WidgetSensorData> {
        if (json.isNullOrBlank()) return emptyList()
        return try {
            val root = JSONObject(json)
            val sensors = root.optJSONArray("sensors") ?: return emptyList()
            List(sensors.length()) { i ->
                val obj = sensors.getJSONObject(i)
                WidgetSensorData(
                    id = obj.optString("id", "sensor_$i"),
                    name = obj.optString("name", "Unknown"),
                    category = obj.optString("category", ""),
                    value = obj.optDouble("value", 0.0),
                    unit = obj.optString("unit", "")
                )
            }
        } catch (e: Exception) {
            Log.w("HardwareInfoWidget", "Failed to parse telemetry", e)
            emptyList()
        }
    }
}

@Composable
private fun HardwareInfoContent(allSensors: List<WidgetSensorData>) {
    val prefs = currentState<Preferences>()
    val selectedStr = prefs[SELECTED_SENSORS_KEY] ?: ""
    val selectedIds = selectedStr.split(",").filter { it.isNotBlank() }.toSet()
    val size = LocalSize.current

    if (selectedIds.isEmpty()) {
        Box(
            modifier = GlanceModifier.fillMaxSize()
                .background(GlanceTheme.colors.surface)
                .cornerRadius(16.dp)
                .padding(8.dp),
            contentAlignment = Alignment.Center
        ) {
            Text(
                "Tap to configure",
                style = TextStyle(
                    color = GlanceTheme.colors.onSurfaceVariant,
                    fontSize = 12.sp
                )
            )
        }
        return
    }

    val sensors = allSensors.filter { it.id in selectedIds }

    val showTitle = size.height >= 100.dp
    val showCategory = size.width >= 250.dp
    val useCards = size.height >= 80.dp
    val availableWidth = size.width - 8.dp

    // Dynamic column calculation based on available width
    val cardMinWidth = if (useCards) 88.dp else 70.dp
    val columns = (availableWidth / cardMinWidth).toInt().coerceAtLeast(1)

    val titleHeight = if (showTitle) 20.dp else 0.dp
    val contentHeight = size.height - 8.dp - titleHeight
    val cardMinHeight = if (useCards) 48.dp else 28.dp
    val maxRows = (contentHeight / (cardMinHeight + 2.dp)).toInt().coerceAtLeast(1)
    val maxItems = (columns * maxRows).coerceAtMost(sensors.size)
    val visibleSensors = sensors.take(maxItems)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .padding(4.dp)
    ) {
        if (showTitle) {
            Text(
                "Hardware Info",
                style = TextStyle(
                    color = GlanceTheme.colors.onSurface,
                    fontWeight = FontWeight.Bold,
                    fontSize = 13.sp
                ),
                modifier = GlanceModifier.padding(bottom = 2.dp, start = 2.dp)
            )
        }

        if (visibleSensors.isEmpty() && selectedIds.isNotEmpty()) {
            Box(
                modifier = GlanceModifier.fillMaxSize(),
                contentAlignment = Alignment.Center
            ) {
                Text(
                    "Waiting for data\u2026",
                    style = TextStyle(
                        color = GlanceTheme.colors.onSurfaceVariant,
                        fontSize = 12.sp
                    )
                )
            }
        } else if (useCards) {
            val cardWidth = availableWidth / columns
            val rows = visibleSensors.chunked(columns)
            rows.forEach { rowItems ->
                Row(
                    modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp)
                ) {
                    rowItems.forEach { sensor ->
                        Box(
                            modifier = GlanceModifier
                                .width(cardWidth - 2.dp)
                                .background(GlanceTheme.colors.secondaryContainer)
                                .cornerRadius(10.dp)
                                .padding(6.dp)
                        ) {
                            Column {
                                Text(
                                    sensor.name,
                                    style = TextStyle(
                                        color = GlanceTheme.colors.onSecondaryContainer,
                                        fontSize = 10.sp,
                                        fontWeight = FontWeight.Medium
                                    ),
                                    maxLines = 1
                                )
                                if (showCategory && sensor.category.isNotBlank()) {
                                    Text(
                                        sensor.category,
                                        style = TextStyle(
                                            color = GlanceTheme.colors.onSecondaryContainer,
                                            fontSize = 9.sp
                                        ),
                                        maxLines = 1
                                    )
                                }
                                Spacer(modifier = GlanceModifier.height(1.dp))
                                Text(
                                    formatSensorValue(sensor),
                                    style = TextStyle(
                                        color = GlanceTheme.colors.onSecondaryContainer,
                                        fontSize = 14.sp,
                                        fontWeight = FontWeight.Bold
                                    ),
                                    maxLines = 1
                                )
                            }
                        }
                    }
                    repeat(columns - rowItems.size) {
                        Box(modifier = GlanceModifier.width(cardWidth - 2.dp)) {}
                    }
                }
            }
        } else {
            // Compact horizontal mode for small widget
            Row(
                modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp)
            ) {
                visibleSensors.forEachIndexed { index, sensor ->
                    Column(
                        modifier = GlanceModifier.padding(horizontal = 3.dp)
                    ) {
                        Text(
                            sensor.name,
                            style = TextStyle(
                                color = GlanceTheme.colors.onSurface,
                                fontSize = 9.sp
                            ),
                            maxLines = 1
                        )
                        Text(
                            formatSensorValue(sensor),
                            style = TextStyle(
                                color = GlanceTheme.colors.onSurface,
                                fontSize = 13.sp,
                                fontWeight = FontWeight.Bold
                            ),
                            maxLines = 1
                        )
                    }
                    if (index < visibleSensors.lastIndex) {
                        Text(
                            "\u2502",
                            style = TextStyle(
                                color = GlanceTheme.colors.onSurfaceVariant,
                                fontSize = 13.sp
                            )
                        )
                    }
                }
            }
        }
    }
}

private fun formatSensorValue(sensor: WidgetSensorData): String {
    val v = sensor.value
    val formatted = if (v == v.toLong().toDouble()) v.toLong().toString()
    else String.format("%.1f", v)
    return if (sensor.unit.isNotBlank()) "$formatted ${sensor.unit}" else formatted
}

class HardwareInfoWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = HardwareInfoWidget()
}
