package com.clindsay94.remex.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Base64
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
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
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
import com.clindsay94.remex.RemexCoreClient
import org.json.JSONArray
import org.json.JSONObject

val SELECTED_APPS_KEY = stringPreferencesKey("selected_apps")
val APP_PATH_PARAM = ActionParameters.Key<String>("app_path")

private val SMALL = DpSize(110.dp, 80.dp)
private val MEDIUM = DpSize(180.dp, 140.dp)
private val LARGE = DpSize(250.dp, 200.dp)
private val XLARGE = DpSize(320.dp, 280.dp)

data class WidgetAppEntry(
    val name: String,
    val path: String,
    val icon: Bitmap?
)

class AppLauncherWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Responsive(setOf(SMALL, MEDIUM, LARGE, XLARGE))

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
                    icon = bitmap
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

    if (selectedPaths.isEmpty()) {
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

    val apps = allApps.filter { it.path in selectedPaths }
    val availableWidth = size.width - 8.dp
    val availableHeight = size.height - 8.dp

    // Dynamic icon size based on widget dimensions
    val iconSize = when {
        size.width >= 320.dp -> 52.dp
        size.width >= 250.dp -> 48.dp
        size.width >= 180.dp -> 44.dp
        else -> 38.dp
    }

    val cellSize = iconSize + 4.dp
    val columns = (availableWidth / cellSize).toInt().coerceAtLeast(1)
    val maxRows = (availableHeight / cellSize).toInt().coerceAtLeast(1)
    val maxItems = columns * maxRows
    val visibleApps = apps.take(maxItems)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .padding(4.dp)
    ) {
        if (visibleApps.isEmpty() && selectedPaths.isNotEmpty()) {
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
        } else {
            val rows = visibleApps.chunked(columns)
            rows.forEach { rowApps ->
                Row(
                    modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp)
                ) {
                    rowApps.forEach { app ->
                        Box(
                            modifier = GlanceModifier
                                .size(cellSize)
                                .padding(2.dp)
                                .clickable(
                                    actionRunCallback<LaunchAppCallback>(
                                        actionParametersOf(APP_PATH_PARAM to app.path)
                                    )
                                ),
                            contentAlignment = Alignment.Center
                        ) {
                            if (app.icon != null) {
                                Image(
                                    provider = ImageProvider(app.icon),
                                    contentDescription = app.name,
                                    modifier = GlanceModifier.size(iconSize)
                                        .cornerRadius(8.dp),
                                    contentScale = ContentScale.Fit
                                )
                            } else {
                                Box(
                                    modifier = GlanceModifier
                                        .size(iconSize)
                                        .background(GlanceTheme.colors.tertiaryContainer)
                                        .cornerRadius(8.dp),
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

private fun (androidx.compose.ui.unit.Dp).toInt(): Int = this.value.toInt()

class LaunchAppCallback : ActionCallback {
    override suspend fun onAction(
        context: Context,
        glanceId: GlanceId,
        parameters: ActionParameters
    ) {
        val path = parameters[APP_PATH_PARAM] ?: return
        if (RemexCoreClient.isLibraryLoaded) {
            val request = JSONObject().apply {
                put("action", "LaunchApp")
                put("parameters", JSONObject().apply {
                    put("TargetPath", path)
                })
            }
            RemexCoreClient.SendCommand(request.toString())
        }
    }
}

class AppLauncherWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = AppLauncherWidget()
}
