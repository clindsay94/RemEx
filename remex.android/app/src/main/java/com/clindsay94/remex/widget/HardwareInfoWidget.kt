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
import androidx.glance.action.Action
import androidx.glance.action.actionStartActivity
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.lazy.GridCells
import androidx.glance.appwidget.lazy.LazyVerticalGrid
import androidx.glance.appwidget.lazy.items
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

    // Short widgets get a tighter frame so the one compact row they can show is whole (A5).
    val outerPadding = if (useCards) 8.dp else 4.dp
    val availableWidth = (size.width - (outerPadding * 2)).coerceAtLeast(0.dp)

    // Cells reflow by width and the grid scrolls vertically, so every selected sensor is reachable
    // and nothing is pushed off the sides or the bottom (live-check A5/A6). The old layout computed
    // how many fit and dropped the rest; the compact one was a single Row that ran off the edge.
    val cardMinWidth = if (useCards) 100.dp else 80.dp
    val columns = WidgetGridMath.columns(
        availableWidth.value,
        cardMinWidth.value,
        maxColumns = if (useCards) 4 else WidgetGridMath.MAX_GRID_COLUMNS
    )
    val cellPadding = 2.dp
    val openApp = actionStartActivity<MainActivity>()

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

        if (sensors.isEmpty()) {
            Box(modifier = GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(context.getString(R.string.widget_waiting_data), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
            }
        } else {
            LazyVerticalGrid(
                gridCells = GridCells.Fixed(columns),
                modifier = GlanceModifier.fillMaxWidth().defaultWeight()
            ) {
                items(sensors) { sensor ->
                    Box(modifier = GlanceModifier.fillMaxWidth().padding(cellPadding)) {
                        if (useCards) {
                            SensorCard(sensor, showCategory, openApp)
                        } else {
                            CompactSensorCell(sensor, openApp)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SensorCard(sensor: WidgetSensorData, showCategory: Boolean, onClick: Action) {
    Box(
        modifier = GlanceModifier
            .fillMaxWidth()
            .background(GlanceTheme.colors.secondaryContainer)
            .cornerRadius(12.dp)
            .clickable(onClick)
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
}

/** Two-line name/value cell for widgets too short for cards (under 60dp tall). */
@Composable
private fun CompactSensorCell(sensor: WidgetSensorData, onClick: Action) {
    Column(
        modifier = GlanceModifier
            .fillMaxWidth()
            .clickable(onClick)
            .padding(horizontal = 4.dp)
    ) {
        Text(
            WidgetText.ellipsize(sensor.name, WidgetText.CompactSensorNameBudget),
            style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 9.sp),
            maxLines = 1
        )
        Text(
            WidgetText.ellipsize(formatSensorValue(sensor), WidgetText.SensorValueBudget),
            style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 12.sp, fontWeight = FontWeight.Bold),
            maxLines = 1
        )
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
            val result = RemexCoreClient.GetTelemetry()
            val snapshot = result.getOrNull()
            if (!snapshot.isNullOrBlank()) {
                WidgetDataCache.putTelemetryJson(context, snapshot)
                WidgetDataCache.refreshHardwareWidgets(context)
                widgetToast(context, context.getString(R.string.widget_toast_refreshed))
            } else {
                // Do NOT re-render or claim success: the cache is untouched, so re-rendering here
                // would just repaint the same stale values while telling the user it worked.
                Log.w(
                    "HardwareInfoWidget",
                    "Manual refresh got no telemetry snapshot",
                    result.exceptionOrNull()
                )
                widgetToast(context, context.getString(R.string.widget_toast_refresh_failed))
            }
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
