package com.clindsay94.remex.ui.screens

/**
 * Decides when the Remote Desktop stream is torn down and restored as the screen leaves and returns to
 * the foreground (perf audit P0-4).
 *
 * Without it, backgrounding the app during a stream left the PC capturing, encoding and sending video
 * at full rate to a phone that kept receiving and decoding it with nobody looking — the heaviest path
 * in the app, running for nothing. The protocol already has the right tool: `desktop_stop` (sent by
 * `StopDesktopStream`) makes the host cancel its capture loop for this client, and a fresh
 * `StartDesktopStream` builds a new encoder whose first output is SPS/PPS+IDR. So a background pause is
 * a stop plus a remembered intent to start again, not a new wire message.
 *
 * **THE FRAME-ARRIVAL WATCHDOG MUST NOT OUTLIVE THE PAUSE** (docs/REGRESSION-GUARDS.md, "Frame-arrival
 * watchdog"). A stopped stream delivers no frames, so a watchdog left running reads the pause as a
 * stall and reconnects — restarting the stream in the background, which is worse than never pausing.
 * [allowsStreamStart] is the backstop: every start path (reconnect backoff, catalog timeout, display
 * switch restart) checks it, so nothing can bring the stream back while backgrounded.
 *
 * Plain Kotlin with no Android dependency so the decision table is unit-testable without a socket.
 * [onForegroundChanged] is driven from the main thread only (the screen's LifecycleEventObserver),
 * but [allowsStreamStart] is also read from sendDispatcher (the reconnect backoff and the delayed
 * display-switch restart) - hence [isForeground] being `@Volatile` rather than this being fully
 * single-threaded.
 */
internal class RemoteDesktopBackgroundGate {

    enum class Action {
        /** Nothing to do: no transition, or no stream to pause or restore. */
        NONE,
        /** Stop the stream and disarm the watchdog; a resume is now owed. */
        PAUSE,
        /** Restart the stream the pause took down (the start re-arms the watchdog). */
        RESUME
    }

    // @Volatile: onForegroundChanged runs on the main thread (LifecycleEventObserver), but
    // allowsStreamStart is also read from sendDispatcher (restartStreamWithCurrentTarget's delayed
    // restart, reconnect backoff), so a plain field could let a background-thread read see a stale
    // value across threads.
    @Volatile
    var isForeground: Boolean = true
        private set

    /** True while a stream was wanted when the app went to the background, and not yet restored. */
    var resumeOnForeground: Boolean = false
        private set

    /**
     * Records a foreground/background transition and returns what the caller must do.
     *
     * @param streamWanted whether a stream is running or about to be (streaming, a reconnect in
     *   flight, or a start waiting on the display catalog). Only read on the way to the background.
     */
    fun onForegroundChanged(foreground: Boolean, streamWanted: Boolean): Action {
        if (foreground == isForeground) return Action.NONE
        isForeground = foreground
        if (!foreground) {
            if (!streamWanted) return Action.NONE
            resumeOnForeground = true
            return Action.PAUSE
        }
        if (!resumeOnForeground) return Action.NONE
        resumeOnForeground = false
        return Action.RESUME
    }

    /** Whether a stream may start now. False while backgrounded, whatever asked for it. */
    fun allowsStreamStart(): Boolean = isForeground

    /** A deliberate stop ends the intent: nothing should come back on the next foreground. */
    fun cancelResume() {
        resumeOnForeground = false
    }
}
