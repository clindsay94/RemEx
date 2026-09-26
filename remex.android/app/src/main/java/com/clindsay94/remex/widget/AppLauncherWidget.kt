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
import androidx.glance.appwidget.lazy.GridCells
import androidx.glance.appwidget.lazy.LazyVerticalGrid
import androidx.glance.appwidget.lazy.items
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.currentState
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ContentScale
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.clindsay94.remex.R
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.RemexCoreClient
import org.json.JSONArray
import org.json.JSONObject

val SELECTED_APPS_KEY = stringPreferencesKey("selected_apps")
val APP_PATH_PARAM = ActionParameters.Key<String>("app_path")
val APP_NAME_PARAM = ActionParameters.Key<String>("app_name")

data class WidgetAppEntry(
    val name: String,
    val path: String,
    private val iconBase64: String,
    val order: Int = 0,
    /** Longest edge the icon is decoded to: the largest cell it is drawn in, in px (A7). */
    private val maxIconPx: Int
) {
    /**
     * Decoded on first access, not at parse time (perf audit P3-12): the host sends the WHOLE
     * launcher list, but the widget only draws the apps the user selected for it and only as many as
     * fit its current size. Decoding every icon up front paid a base64 + bitmap decode for entries
     * that were filtered out immediately afterwards. Lazy also caches the bitmap across Glance
     * recompositions of the same session, since the parsed list is captured once in provideGlance.
     */
    val icon: Bitmap? by lazy {
        if (iconBase64.isBlank()) return@lazy null
        try {
            val bytes = Base64.decode(iconBase64, Base64.DEFAULT)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.let { boundedIcon(it, maxIconPx) }
        } catch (_: Exception) {
            null
        }
    }
}

/**
 * The largest icon this widget draws (the >= 300dp tier below). Icons decode to THIS size on the
 * current screen, not per tier: SizeMode.Exact can compose a portrait and a landscape layout in one
 * update at different tiers, and one bitmap per app keeps both pointing at the same pixels.
 */
private const val LARGEST_ICON_DP = 56f

/**
 * The grid now renders every selected app (live-check A4), and all their bitmaps travel in one
 * RemoteViews payload with a hard memory ceiling. Bound each icon to the pixels its cell can show
 * (live-check A7) so a host sending large ones cannot push the widget over it and leave it blank.
 */
private fun boundedIcon(bitmap: Bitmap, maxPx: Int): Bitmap {
    val (w, h) = WidgetGridMath.scaledIconSize(bitmap.width, bitmap.height, maxPx)
    if (w == bitmap.width && h == bitmap.height) return bitmap
    return Bitmap.createScaledBitmap(bitmap, w, h, true)
}

class AppLauncherWidget : GlanceAppWidget() {

    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val launcherJson = WidgetDataCache.getLauncherJson(context)
        // Live-check A7: icons sized from the cell's dp and this screen's density, and a cap on what
        // they may spend of the launcher's bitmap ceiling, which is itself derived from the screen.
        val metrics = context.resources.displayMetrics
        val maxIconPx = WidgetGridMath.iconPx(LARGEST_ICON_DP, metrics.density)
        val iconBudget = WidgetGridMath.iconBudgetBytes(metrics.widthPixels, metrics.heightPixels)
        val allApps = parseLauncherEntries(launcherJson, maxIconPx)

        provideContent {
            GlanceTheme {
                AppLauncherContent(allApps, iconBudget)
            }
        }
    }

    private fun parseLauncherEntries(json: String?, maxIconPx: Int): List<WidgetAppEntry> {
        if (json.isNullOrBlank()) return emptyList()
        return try {
            val array = JSONArray(json)
            List(array.length()) { i ->
                val obj = array.getJSONObject(i)
                WidgetAppEntry(
                    name = obj.optString("displayName", "App"),
                    path = obj.optString("targetPath", ""),
                    iconBase64 = obj.optString("iconBase64", ""),
                    order = obj.optInt("order", 0),
                    maxIconPx = maxIconPx
                )
            }
        } catch (e: Exception) {
            Log.w("AppLauncherWidget", "Failed to parse launcher entries", e)
            emptyList()
        }
    }
}

@Composable
private fun AppLauncherContent(allApps: List<WidgetAppEntry>, iconBudgetBytes: Long) {
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
    // Live-check A7: the config screen does not cap how many apps are picked and the grid draws them
    // all, so past the budget an app shows its letter tile instead of taking the widget down with it.
    // Only selected apps reach this read, so unselected icons never decode.
    val drawIcon = WidgetGridMath.iconsWithinBudget(
        apps.map { app -> app.icon?.allocationByteCount?.toLong() },
        iconBudgetBytes
    )
    val cells = apps.zip(drawIcon)

    val outerPadding = 6.dp
    val availableWidth = (size.width - (outerPadding * 2)).coerceAtLeast(0.dp)

    val iconSize = when {
        size.width >= 300.dp -> 56.dp
        size.width >= 200.dp -> 48.dp
        else -> 42.dp
    }

    // Tighter gutters so icons breathe without wasting space.
    val itemPadding = 2.dp
    val cellSize = iconSize + (itemPadding * 2)
    val columns = WidgetGridMath.columns(availableWidth.value, cellSize.value)

    Column(
        modifier = GlanceModifier.fillMaxSize()
            .background(GlanceTheme.colors.surface)
            .cornerRadius(16.dp)
            .padding(outerPadding)
    ) {
        if (apps.isEmpty()) {
            Box(modifier = GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(context.getString(R.string.widget_waiting_data), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
            }
        } else {
            // Every selected app, scrolling when there are more than the widget shows (live-check
            // A4). This used to take() only as many as fit and drop the rest without a trace.
            LazyVerticalGrid(
                gridCells = GridCells.Fixed(columns),
                modifier = GlanceModifier.fillMaxSize(),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                items(cells) { (app, withinBudget) ->
                    Box(
                        modifier = GlanceModifier.fillMaxWidth().height(cellSize),
                        contentAlignment = Alignment.Center
                    ) {
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
                            val icon = if (withinBudget) app.icon else null
                            if (icon != null) {
                                Image(
                                    provider = ImageProvider(icon),
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
        // Live-check A8: a backgrounded or killed app gets one bounded connect attempt here instead
        // of an immediate "not connected".
        if (!ensureWidgetConnection(context)) {
            widgetToast(context, context.getString(R.string.widget_toast_not_connected))
            return
        }
        // Handed over rather than awaited: this runs inside a goAsync() broadcast window, and
        // SendCommand now waits for the PC's answer on a ten-second budget (RemEx-66rf).
        sendWidgetCommand("LaunchApp", JSONObject().apply { put("TargetPath", path) })

        // "SENT", NOT "LAUNCHING" (RemEx-4nxfz). This toast used to say "Launching Firefox..." the
        // instant the command was handed over - a claim about what the PC is doing, made before
        // anything is known about whether it will. The PC can refuse: a missing executable, a
        // declined command, a round trip that runs out its budget. Those are logged and never shown,
        // so the user read "Launching Firefox..." and then watched nothing happen.
        //
        // What IS known here is that the command left this phone, which is exactly what the shared
        // string says - and it is the same thing RemoteControlWidget has always claimed. Telling the
        // user whether it WORKED needs a surface that outlives this broadcast; that is RemEx-mug0,
        // and this is the honest "sent" half of the distinction it asks for.
        widgetToast(context, context.getString(R.string.widget_toast_command_sent, name))
    }
}

class AppLauncherWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = AppLauncherWidget()
}
