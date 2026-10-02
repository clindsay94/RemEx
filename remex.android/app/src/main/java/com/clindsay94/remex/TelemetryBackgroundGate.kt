package com.clindsay94.remex

/**
 * Decides when to tell the connected PC to stop and restart its telemetry push as the app goes to the
 * background and comes back (perf audit P0-5).
 *
 * The host pushes a full 60-100 KB envelope every second. Before this, a backgrounded phone, or one
 * with its screen off, kept receiving it with nobody looking, waking the radio and burning CPU on both
 * ends. `telemetry_pause` / `telemetry_resume` switch that stream off and on for THIS connection only.
 *
 * **THE HOST'S PAUSE DIES WITH THE SOCKET.** Every new connection starts unpaused on the host, so a
 * phone that reconnects while still backgrounded has to say so again. [reconcile] therefore tracks
 * which connection it last paused, not just a flag: a new [EstablishedConnection] (the epoch makes
 * every one distinct, even a reconnect to the same PC) resets what the host is assumed to know.
 *
 * **A PLACED HARDWARE WIDGET KEEPS THE STREAM RUNNING.** The home-screen widget is rendered from this
 * same stream while the app is backgrounded (WidgetDataCache), so pausing would freeze it on its last
 * reading. The widget's own poll interval already throttles what it does with each sample.
 *
 * **OPEN IS NOT ENOUGH (Leanness K8).** In the foreground the stream also pauses when no visible
 * surface reads it (no [TelemetryDemand] lease), and resumes when one appears; the first reading
 * then lands within about a second, at the host's next sample.
 *
 * Plain Kotlin with no Android dependency so the decision table is unit-testable. Synchronized
 * because the lifecycle observer and the authentication collector may call it from different threads.
 */
internal class TelemetryBackgroundGate {

    enum class Action {
        /** The host already has the right state, or there is no authenticated connection to tell. */
        NONE,
        /** Send `telemetry_pause`. */
        PAUSE,
        /** Send `telemetry_resume`. */
        RESUME
    }

    private var pausedConnection: EstablishedConnection? = null

    /**
     * Returns what, if anything, must be sent so the host's stream matches what the app needs now.
     *
     * @param foreground whether any of the app's UI is started (ProcessLifecycleOwner ON_START).
     * @param connection the current authenticated connection, or null when there is none.
     * @param widgetPlaced whether a hardware widget is on the home screen and needs live telemetry.
     * @param telemetryWanted whether anything on screen reads telemetry right now: a
     *   [TelemetryDemand] lease is held (Leanness K8). The app being open is no longer enough on its
     *   own; the default keeps the pre-K8 meaning for callers that only ask about the background.
     */
    @Synchronized
    fun reconcile(
            foreground: Boolean,
            connection: EstablishedConnection?,
            widgetPlaced: Boolean,
            telemetryWanted: Boolean = true,
    ): Action {
        // A different (or no) connection: whatever was paused belonged to a socket that is gone,
        // and the new one starts unpaused on the host.
        if (connection != pausedConnection) pausedConnection = null
        if (connection == null) return Action.NONE

        // Streams for a placed widget always, and otherwise only while the app is open AND a
        // visible surface holds a lease. A lease held in the background (a screen that missed its
        // ON_STOP) does not keep a pocketed phone streaming.
        val wantPaused = !widgetPlaced && !(foreground && telemetryWanted)
        val isPaused = pausedConnection != null
        return when {
            wantPaused == isPaused -> Action.NONE
            wantPaused -> {
                pausedConnection = connection
                Action.PAUSE
            }
            else -> {
                pausedConnection = null
                Action.RESUME
            }
        }
    }

    /**
     * Perf audit P0-5 review fix: [reconcile] records the new state before the caller has actually
     * sent the message, so a failed send left the host desynced from the gate with no retry - a
     * failed RESUME left the dashboard stale until the next reconnect or background cycle, and a
     * failed PAUSE kept streaming to a backgrounded phone. Call this when the send for [action]
     * failed, on the SAME [connection] passed to the [reconcile] call that produced it, to undo the
     * state change; the next [reconcile] call (on a foreground or connection change) will then see
     * a mismatch again and retry.
     */
    @Synchronized
    fun revertFailedSend(connection: EstablishedConnection?, action: Action) {
        if (connection == null) return
        when (action) {
            Action.PAUSE -> if (pausedConnection == connection) pausedConnection = null
            Action.RESUME -> if (pausedConnection == null) pausedConnection = connection
            Action.NONE -> Unit
        }
    }
}
