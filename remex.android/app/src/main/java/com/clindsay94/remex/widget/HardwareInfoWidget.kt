package com.clindsay94.remex.widget

import android.content.Context
import android.util.Log
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.actionStartActivity
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.ColorFilter
import androidx.glance.action.ActionParameters
import com.clindsay94.remex.R
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.service.RemexConnectionService
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

data class WidgetSensorData(
    val id: String,
    val name: String,
    val category: String,
    val value: Double,
    val unit: String
)

class HardwareInfoWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Exact

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

    val context = androidx.glance.LocalContext.current
    if (selectedIds.isEmpty()) {
        Box(
            modifier = GlanceModifier.fillMaxSize()
                .background(GlanceTheme.colors.surface)
                .cornerRadius(16.dp)
                .clickable(actionStartActivity<MainActivity>())
                .padding(12.dp),
            contentAlignment = Alignment.Center
        ) {
            Text(
                context.getString(R.string.widget_tap_to_open),
                style = TextStyle(
                    color = GlanceTheme.colors.onSurfaceVariant,
                    fontSize = 12.sp
                )
            )
        }
        return
    }

    val sensors = allSensors.filter { it.id in selectedIds }

    val showTitle = size.height >= 80.dp
    val showCategory = size.width >= 240.dp
    val useCards = size.height >= 60.dp

    // Balanced padding for all sizes
    val outerPadding = 8.dp
    val availableWidth = (size.width - (outerPadding * 2)).coerceAtLeast(0.dp)

    val cardMinWidth = if (useCards) 100.dp else 80.dp
    val columns = (availableWidth / cardMinWidth).toInt().coerceIn(1, 4)

    val titleHeight = if (showTitle) 24.dp else 0.dp
    val contentHeight = (size.height - (outerPadding * 2) - titleHeight).coerceAtLeast(0.dp)
    val cardMinHeight = if (useCards) 54.dp else 34.dp
    val maxRows = (contentHeight / (cardMinHeight + 4.dp)).toInt().coerceAtLeast(1)
    val maxItems = (columns * maxRows).coerceAtMost(sensors.size)
    val visibleSensors = sensors.take(maxItems)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .clickable(actionStartActivity<MainActivity>())
            .padding(outerPadding)
    ) {
        if (showTitle) {
            Row(
                modifier = GlanceModifier.fillMaxWidth().padding(bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    context.getString(R.string.widget_hardware_title),
                    style = TextStyle(
                        color = GlanceTheme.colors.onSurface,
                        fontWeight = FontWeight.Bold,
                        fontSize = 14.sp
                    ),
                    modifier = GlanceModifier.defaultWeight().padding(start = 4.dp)
                )
                RefreshButton(context)
            }
        }

        if (visibleSensors.isEmpty()) {
            Box(modifier = GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(context.getString(R.string.widget_waiting_data), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
            }
        } else if (useCards) {
            val cardWidth = ((availableWidth / columns) - 4.dp).coerceAtLeast(0.dp)
            val rows = visibleSensors.chunked(columns)

            Column(modifier = GlanceModifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
                rows.forEach { rowItems ->
                    Row(
                        modifier = GlanceModifier.fillMaxWidth().padding(vertical = 2.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        rowItems.forEach { sensor ->
                            Box(
                                modifier = GlanceModifier
                                    .width(cardWidth)
                                    .background(GlanceTheme.colors.secondaryContainer)
                                    .cornerRadius(12.dp)
                                    .padding(8.dp),
                                contentAlignment = Alignment.CenterStart
                            ) {
                                Column {
                                    Text(
                                        WidgetText.ellipsize(sensor.name, WidgetText.SensorNameBudget),
                                        style = TextStyle(
                                            color = GlanceTheme.colors.onSecondaryContainer,
                                            fontSize = 10.sp,
                                            fontWeight = FontWeight.Medium
                                        ),
                                        maxLines = 1
                                    )
                                    if (showCategory && sensor.category.isNotBlank()) {
                                        Text(
                                            WidgetText.ellipsize(sensor.category, WidgetText.SensorCategoryBudget),
                                            style = TextStyle(
                                                color = GlanceTheme.colors.onSecondaryContainer,
                                                fontSize = 8.sp
                                            ),
                                            maxLines = 1
                                        )
                                    }
                                    Spacer(modifier = GlanceModifier.height(2.dp))
                                    Text(
                                        WidgetText.ellipsize(formatSensorValue(sensor), WidgetText.SensorValueBudget),
                                        style = TextStyle(
                                            color = GlanceTheme.colors.onSecondaryContainer,
                                            fontSize = 14.sp,
                                            fontWeight = FontWeight.Bold
                                        ),
                                        maxLines = 1
                                    )
                                }
                            }
                            if (rowItems.indexOf(sensor) < rowItems.lastIndex) {
                                Spacer(modifier = GlanceModifier.width(4.dp))
                            }
                        }
                    }
                }
            }
        } else {
            Row(
                modifier = GlanceModifier.fillMaxWidth().padding(4.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                visibleSensors.forEachIndexed { index, sensor ->
                    Column(modifier = GlanceModifier.padding(horizontal = 6.dp)) {
                        Text(WidgetText.ellipsize(sensor.name, WidgetText.CompactSensorNameBudget), style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 9.sp), maxLines = 1)
                        Text(WidgetText.ellipsize(formatSensorValue(sensor), WidgetText.SensorValueBudget), style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 12.sp, fontWeight = FontWeight.Bold), maxLines = 1)
                    }
                    if (index < visibleSensors.lastIndex) {
                        Box(modifier = GlanceModifier.width(1.dp).height(16.dp).background(GlanceTheme.colors.outline)) {}
                    }
                }
            }
        }
    }
}

@Composable
private fun RefreshButton(context: Context) {
    Box(
        modifier = GlanceModifier
            .cornerRadius(12.dp)
            .clickable(actionRunCallback<RefreshTelemetryCallback>())
            .padding(4.dp),
        contentAlignment = Alignment.Center
    ) {
        Image(
            provider = ImageProvider(R.drawable.ic_refresh),
            contentDescription = context.getString(R.string.widget_refresh),
            colorFilter = ColorFilter.tint(GlanceTheme.colors.onSurfaceVariant),
            modifier = GlanceModifier.width(20.dp).height(20.dp)
        )
    }
}

/**
 * Manual refresh from the widget. When connected, pulls a fresh on-demand telemetry snapshot and
 * re-renders. When not connected, kicks off an auto-connect and lets the live caching flow update
 * the widget once telemetry starts arriving.
 */
class RefreshTelemetryCallback : ActionCallback {
    override suspend fun onAction(
        context: Context,
        glanceId: GlanceId,
        parameters: ActionParameters
    ) {
        if (!RemexCoreClient.isLibraryLoaded) {
            widgetToast(context, context.getString(R.string.widget_toast_remex_not_ready))
            return
        }

        if (RemexClientManager.isConnected.value) {
            widgetToast(context, context.getString(R.string.widget_toast_refreshing))
            val snapshot = RemexCoreClient.GetTelemetry().getOrNull()
            if (!snapshot.isNullOrBlank()) {
                WidgetDataCache.putTelemetryJson(context, snapshot)
            }
            WidgetDataCache.refreshHardwareWidgets(context)
            widgetToast(context, context.getString(R.string.widget_toast_refreshed))
        } else {
            widgetToast(context, context.getString(R.string.widget_toast_connecting))
            val appContext = context.applicationContext
            RemexClientManager.initialize(appContext)
            WidgetDataCache.startCaching(appContext)
            runCatching { RemexConnectionService.start(appContext) }
            RemexClientManager.toggleConnection()
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
