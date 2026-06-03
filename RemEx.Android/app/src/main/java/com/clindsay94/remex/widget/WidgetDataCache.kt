package com.clindsay94.remex.widget

import android.content.Context
import android.util.Log
import androidx.glance.appwidget.GlanceAppWidgetManager
import com.clindsay94.remex.RemexClientManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
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

    fun startCaching(context: Context) {
        if (cachingJob != null) return
        val appContext = context.applicationContext
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        cachingJob = scope.launch {
            launch {
                RemexClientManager.telemetry.collect { data ->
                    prefs(appContext).edit().putString(KEY_TELEMETRY, data).apply()
                    try {
                        val manager = GlanceAppWidgetManager(appContext)
                        val widget = HardwareInfoWidget()
                        manager.getGlanceIds(HardwareInfoWidget::class.java).forEach { glanceId ->
                            widget.update(appContext, glanceId)
                        }
                    } catch (e: Exception) {
                        Log.w(TAG, "Failed to update hardware widgets", e)
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

    fun getLauncherJson(context: Context): String? =
        prefs(context).getString(KEY_LAUNCHER, null)

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
}
