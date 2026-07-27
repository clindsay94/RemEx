package com.clindsay94.remex.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Base64
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
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.action.actionStartActivity
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.currentState
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ContentScale
import androidx.glance.layout.Row
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.clindsay94.remex.R
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import org.json.JSONArray
import org.json.JSONObject

val SELECTED_APPS_KEY = stringPreferencesKey("selected_apps")
val APP_PATH_PARAM = ActionParameters.Key<String>("app_path")
val APP_NAME_PARAM = ActionParameters.Key<String>("app_name")

data class WidgetAppEntry(
    val name: String,
    val path: String,
    val icon: Bitmap?,
    val order: Int = 0
)

class AppLauncherWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val launcherJson = WidgetDataCache.getLauncherJson(context)
        val allApps = parseLauncherEntries(launcherJson)

        provideContent {
            GlanceTheme {
                AppLauncherContent(allApps)
            }
        }
    }

    private fun parseLauncherEntries(json: String?): List<WidgetAppEntry> {
        if (json.isNullOrBlank()) return emptyList()
        return try {
            val array = JSONArray(json)
            List(array.length()) { i ->
                val obj = array.getJSONObject(i)
                val iconBase64 = obj.optString("iconBase64", "")
                val bitmap = if (iconBase64.isNotBlank()) {
                    try {
                        val bytes = Base64.decode(iconBase64, Base64.DEFAULT)
                        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    } catch (_: Exception) {
                        null
                    }
                } else null
                WidgetAppEntry(
                    name = obj.optString("displayName", "App"),
                    path = obj.optString("targetPath", ""),
                    icon = bitmap,
                    order = obj.optInt("order", 0)
                )
            }
        } catch (e: Exception) {
            Log.w("AppLauncherWidget", "Failed to parse launcher entries", e)
            emptyList()
        }
    }
}

@Composable
private fun AppLauncherContent(allApps: List<WidgetAppEntry>) {
    val prefs = currentState<Preferences>()
    val selectedStr = prefs[SELECTED_APPS_KEY] ?: ""
    val selectedPaths = selectedStr.split("\n").filter { it.isNotBlank() }.toSet()
    val size = LocalSize.current

    val context = androidx.glance.LocalContext.current
    if (selectedPaths.isEmpty()) {
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

    val apps = allApps.filter { it.path in selectedPaths }.sortedBy { it.order }

    val outerPadding = 6.dp
    val availableWidth = (size.width - (outerPadding * 2)).coerceAtLeast(0.dp)
    val availableHeight = (size.height - (outerPadding * 2)).coerceAtLeast(0.dp)

    val iconSize = when {
        size.width >= 300.dp -> 56.dp
        size.width >= 200.dp -> 48.dp
        else -> 42.dp
    }

    // Tighter gutters so icons breathe without wasting space.
    val itemPadding = 2.dp
    val cellSize = iconSize + (itemPadding * 2)
    // Each Row also adds 1.dp of vertical padding top+bottom (see below).
    val rowStride = cellSize + 2.dp

    // Fit a near-complete extra column/row instead of dropping it: any overflow up
    // to a cell's own padding (itemPadding on each edge) is absorbed by the outer
    // cells' padding, so icons never clip even when we round up to the edge.
    val columns = ((availableWidth + itemPadding * 2) / cellSize).toInt().coerceAtLeast(1)
    val maxRows = ((availableHeight + itemPadding * 2) / rowStride).toInt().coerceAtLeast(1)
    val maxItems = columns * maxRows
    val visibleApps = apps.take(maxItems)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .padding(outerPadding)
    ) {
        if (visibleApps.isEmpty()) {
            Box(modifier = GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(context.getString(R.string.widget_waiting_data), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
            }
        } else {
            val rows = visibleApps.chunked(columns)
            rows.forEach { rowApps ->
                Row(
                    modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    rowApps.forEach { app ->
                        Box(
                            modifier = GlanceModifier
                                .size(cellSize)
                                .padding(itemPadding)
                                .clickable(
                                    actionRunCallback<LaunchAppCallback>(
                                        actionParametersOf(
                                            APP_PATH_PARAM to app.path,
                                            APP_NAME_PARAM to app.name
                                        )
                                    )
                                ),
                            contentAlignment = Alignment.Center
                        ) {
                            if (app.icon != null) {
                                Image(
                                    provider = ImageProvider(app.icon),
                                    contentDescription = app.name,
                                    modifier = GlanceModifier.size(iconSize).cornerRadius(10.dp),
                                    contentScale = ContentScale.Fit
                                )
                            } else {
                                Box(
                                    modifier = GlanceModifier
                                        .size(iconSize)
                                        .background(GlanceTheme.colors.tertiaryContainer)
                                        .cornerRadius(10.dp),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Text(
                                        app.name.take(2).uppercase(),
                                        style = TextStyle(
                                            color = GlanceTheme.colors.onTertiaryContainer,
                                            fontSize = 14.sp,
                                            fontWeight = FontWeight.Bold
                                        )
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

class LaunchAppCallback : ActionCallback {
    override suspend fun onAction(
        context: Context,
        glanceId: GlanceId,
        parameters: ActionParameters
    ) {
        val path = parameters[APP_PATH_PARAM] ?: return
        val name = parameters[APP_NAME_PARAM] ?: "app"
        if (!RemexCoreClient.isLibraryLoaded) {
            widgetToast(context, context.getString(R.string.widget_toast_remex_not_ready))
            return
        }
        if (!RemexClientManager.isConnected.value) {
            widgetToast(context, context.getString(R.string.widget_toast_not_connected))
            return
        }
        val request = JSONObject().apply {
            put("action", "LaunchApp")
            put("parameters", JSONObject().apply {
                put("TargetPath", path)
            })
        }
        RemexCoreClient.SendCommand(request.toString()).getOrNull()
        widgetToast(context, context.getString(R.string.widget_toast_launching, name))
    }
}

class AppLauncherWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = AppLauncherWidget()
}
