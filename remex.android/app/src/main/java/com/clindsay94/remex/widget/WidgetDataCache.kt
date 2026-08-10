package com.clindsay94.remex.widget

import android.content.Context
import android.util.Log
import androidx.glance.appwidget.GlanceAppWidgetManager
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.SettingsManager
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.retry
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

    /**
     * How long to wait before re-reading the widget poll interval after a failed read.
     *
     * Long because nothing is broken while it waits — the last known interval stays in effect, and a
     * setting the user is not currently editing does not need second-by-second attention. The old
     * per-tick read effectively retried every second, which is what made the failure invisible and
     * the success expensive.
     */
    private const val SETTINGS_RETRY_MS = 30_000L

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
            // The native client pushes telemetry at ~1 Hz, and this collector runs for the LIFE OF
            // THE PROCESS — the scope above is deliberately never cancelled. So anything done per
            // tick is done once a second, forever, on a phone. The widget re-render is throttled to
            // the user's configured poll interval; the cache write is now gated behind the same
            // check, and the interval is read from a separate collector instead of per tick.
            //
            // **BOTH OF THOSE USED TO HAPPEN EVERY SECOND TO SUPPORT A RENDER THAT USUALLY DID NOT
            // (RemEx-o02rf).** The write called itself a cheap SharedPreferences write, true per call
            // and misleading in aggregate — apply() is still a disk commit, and nothing reads the
            // value between renders. Worse, the DataStore read that decided whether to SKIP the
            // render cost more than the work it was avoiding.
            //
            // **THE WRITE STAYS BEFORE THE RENDER, WHICH IS NOT INCIDENTAL:** refreshHardwareWidgets
            // renders FROM this cache, so gating the write without keeping that order would render
            // the previous interval's numbers.
            //
            // The cost of gating is that renders this loop does NOT drive — a home screen redraw,
            // a resize, a reboot, the config activity — now read a cache up to one poll interval old
            // rather than up to one second old. That is the freshness the setting asks for, so it is
            // the honest reading of the user's choice rather than a regression, but it IS a change.
            // Note it COMPOUNDS on screen: such a redraw paints data up to one interval old, and that
            // frame stays up until this loop's next render, so the worst case for what the user is
            // actually looking at is two intervals rather than one.
            launch {
                val settings = SettingsManager(appContext)

                // Kept current by its own collector instead of read per tick. `first()` every second
                // was a DataStore read whose only job was to answer a question that changes when the
                // user edits a setting — which is what a flow is for.
                //
                // AtomicLong, not a plain `var`: this is written by the collector below and read by
                // the telemetry collector, on different coroutines, and a JVM long write is not
                // guaranteed atomic without it. A torn read here would be a garbage interval.
                val intervalMs = AtomicLong(renderIntervalMs(SettingsManager.WIDGET_TELEMETRY_POLL_DEFAULT))
                launch {
                    // **`retry`, NOT `runCatching`, AND THE DIFFERENCE IS TWO SEPARATE BUGS.**
                    //
                    // CONTAINMENT: these are children of the same job, not of the SupervisorJob
                    // directly, so an uncaught throw here cancels the parent and stops BOTH the
                    // telemetry and launcher collectors — every widget frozen on its last render for
                    // the life of the process, behind one log line.
                    //
                    // RECOVERY: DataStore reports read failures by throwing into the collector and
                    // terminating the flow. The old code called `first()` inside a per-tick
                    // runCatching, so a transient IOException self-healed a second later. Collecting
                    // once does not get that for free — catching without retrying would pin the
                    // interval at the seeded default forever, which for a user on a 600s setting is
                    // twenty times more writing and rendering than they asked for.
                    //
                    // CANCELLATION: `runCatching` catches Throwable, which includes the
                    // CancellationException raised when the surrounding scope dies — the trap already
                    // documented in PairingScreen. Swallowing it here would log "setting unreadable"
                    // during a teardown actually caused by the telemetry collector, pointing at the
                    // wrong subsystem in exactly the incident where the log is all anyone has.
                    // `retry` is cancellation-transparent by construction.
                    settings.widgetTelemetryPollSecondsFlow
                            .retry { cause ->
                                Log.w(TAG, "Widget poll-interval setting unreadable; retrying", cause)
                                delay(SETTINGS_RETRY_MS)
                                true
                            }
                            .collect { seconds -> intervalMs.set(renderIntervalMs(seconds)) }
                }

                var lastRenderMs = 0L
                RemexClientManager.telemetry.collect { data ->
                    val now = System.currentTimeMillis()
                    if (shouldRender(now, lastRenderMs, intervalMs.get())) {
                        lastRenderMs = now
                        renderTick(
                                data,
                                write = { putTelemetryJson(appContext, it) },
                                render = { refreshHardwareWidgets(appContext) }
                        )
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

    /**
     * The configured widget poll interval in milliseconds, floored at the supported minimum.
     *
     * Internal and extracted so the default seeded before the setting arrives and the value applied
     * after it does cannot drift apart — and so the clamp is testable without a DataStore.
     */
    internal fun renderIntervalMs(seconds: Int): Long =
        seconds.coerceAtLeast(SettingsManager.WIDGET_TELEMETRY_POLL_MIN) * 1000L

    /**
     * Whether enough time has passed to redraw the widget — and, since RemEx-o02rf, to save the
     * cache the redraw reads from.
     *
     * Extracted rather than left inline because it is the decision this bead changed, and inline it
     * was reachable only through a coroutine collecting a global flow.
     *
     * **THE `lastRenderMs == 0L` CASE IS SPELLED OUT RATHER THAN LEFT TO ARITHMETIC.** Zero means
     * nothing has been rendered yet, and a freshly placed widget must draw from the first reading
     * instead of waiting out an interval. The subtraction alone appears to give that for free, but
     * only because the caller passes a wall clock — roughly 1.7e12 — which dwarfs any interval. Swap
     * to `SystemClock.elapsedRealtime()`, which is the MORE correct clock for throttling and starts
     * near zero at boot, and the guarantee silently inverts: a widget on a freshly rebooted phone
     * would stay blank for a whole interval. Stating the case makes this independent of the clock.
     */
    internal fun shouldRender(nowMs: Long, lastRenderMs: Long, intervalMs: Long): Boolean =
        lastRenderMs == 0L || nowMs - lastRenderMs >= intervalMs

    /**
     * Saves the reading, then draws the widget from it. In that order, always.
     *
     * **A SEAM RATHER THAN TWO ADJACENT LINES, BECAUSE THIS IS THE ONE MISTAKE HERE THAT WOULD NOT
     * ANNOUNCE ITSELF.** [refreshHardwareWidgets] renders FROM the cache [write] populates, so
     * swapping them draws the PREVIOUS interval's numbers — for ever, on schedule, at the right rate,
     * with no log, no crash and no rate anomaly to notice. Every other way to break this bead fails
     * either toward the old behaviour or toward something that writes a warning.
     *
     * A comment stops a careful reader reordering two lines. It does not stop a formatter, a merge
     * resolution, or a refactor that hoists the write. Passing them in as functions lets a test assert
     * the order the build can then keep.
     */
    internal suspend fun renderTick(data: String, write: (String) -> Unit, render: suspend () -> Unit) {
        write(data)
        render()
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
