package com.clindsay94.remex.widget

import android.content.Context
import android.util.Log
import androidx.glance.appwidget.GlanceAppWidgetManager
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Show a short toast from a widget action callback (always on the main thread). */
suspend fun widgetToast(context: Context, message: String) {
    withContext(Dispatchers.Main) {
        android.widget.Toast
            .makeText(context.applicationContext, message, android.widget.Toast.LENGTH_SHORT)
            .show()
    }
}

object WidgetDataCache {
    private const val TAG = "WidgetDataCache"
    private const val PREFS_NAME = "remex_widget_data"
    private const val KEY_TELEMETRY = "telemetry_json"
    private const val KEY_LAUNCHER = "launcher_json"

    private var cachingJob: Job? = null

    /**
     * Starts the widget data cache once per process. Idempotent.
     *
     * `@Synchronized` because the guard is a check-then-act on three callers that are NOT all on the
     * same thread: `MainActivity.onCreate`, `RemexConnectionService`, and `HardwareInfoWidget`'s
     * update path, which runs from a broadcast. Two of those arriving together would each read a
     * null job and start a second collector on a second scope — a permanent duplicate, since nothing
     * ever cancels these.
     *
     * The scope is deliberately process-lifetime and is never cancelled. That is not an oversight:
     * a widget outlives every Activity and Service that might have started this, so tying the cache
     * to any of their lifecycles would stop refreshing the widget while it is still on the home
     * screen. It is one scope for the life of the process, which is why starting a second one
     * matters enough to synchronize.
     */
    @Synchronized
    fun startCaching(context: Context) {
        if (cachingJob != null) return
        val appContext = context.applicationContext
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        cachingJob = scope.launch {
            // The native client pushes telemetry at ~1 Hz. We always keep the cache warm (cheap
            // SharedPreferences write) but THROTTLE the actual widget re-render to the user's
            // configured poll interval — otherwise the widget thrashes once per second, ignoring
            // the setting and burning battery. The first emission renders immediately.
            launch {
                val settings = SettingsManager(appContext)
                var lastRenderMs = 0L
                RemexClientManager.telemetry.collect { data ->
                    putTelemetryJson(appContext, data)
                    val intervalMs =
                            runCatching { settings.widgetTelemetryPollSecondsFlow.first() }
                                    .getOrDefault(SettingsManager.WIDGET_TELEMETRY_POLL_DEFAULT)
                                    .coerceAtLeast(SettingsManager.WIDGET_TELEMETRY_POLL_MIN) * 1000L
                    val now = System.currentTimeMillis()
                    if (now - lastRenderMs >= intervalMs) {
                        lastRenderMs = now
                        refreshHardwareWidgets(appContext)
                    }
                }
            }
            launch {
                RemexClientManager.launcherEntries.collect { data ->
                    prefs(appContext).edit().putString(KEY_LAUNCHER, data).apply()
                    try {
                        val manager = GlanceAppWidgetManager(appContext)
                        val widget = AppLauncherWidget()
                        manager.getGlanceIds(AppLauncherWidget::class.java).forEach { glanceId ->
                            widget.update(appContext, glanceId)
                        }
                    } catch (e: Exception) {
                        Log.w(TAG, "Failed to update launcher widgets", e)
                    }
                }
            }
        }
    }

    fun getTelemetryJson(context: Context): String? =
        prefs(context).getString(KEY_TELEMETRY, null)

    /** Persist a telemetry JSON snapshot to the widget cache. */
    fun putTelemetryJson(context: Context, json: String) {
        prefs(context.applicationContext).edit().putString(KEY_TELEMETRY, json).apply()
    }

    /** Re-render all hardware-info widgets from the current cache. */
    suspend fun refreshHardwareWidgets(context: Context) {
        try {
            val appContext = context.applicationContext
            val manager = GlanceAppWidgetManager(appContext)
            val widget = HardwareInfoWidget()
            manager.getGlanceIds(HardwareInfoWidget::class.java).forEach { glanceId ->
                widget.update(appContext, glanceId)
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to update hardware widgets", e)
        }
    }

    fun getLauncherJson(context: Context): String? =
        prefs(context).getString(KEY_LAUNCHER, null)

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
}
