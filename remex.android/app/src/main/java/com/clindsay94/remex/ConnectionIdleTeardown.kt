package com.clindsay94.remex

/**
 * When to let go of the background connection's keepalive after the app has sat unused
 * (RemEx-9yei0).
 *
 * A widget tap, a routine, or a hardware-widget refresh in a killed process starts
 * [com.clindsay94.remex.service.RemexConnectionService], the foreground service that keeps the process
 * (and its socket) alive. Before this, nothing ever stopped it again: one tap on a widget meant a
 * process held awake indefinitely, which undid the battery work in [ReconnectGate].
 *
 * The rule, all of which must hold for [IDLE_TIMEOUT_MS] without a break:
 * - **The app is in the background.** Coming back to the foreground cancels the countdown and
 *   restores the service, and the heartbeat reconnects as it always has.
 * - **Nothing still wants the live connection** ([ReconnectGate.allows] is false): no hardware
 *   widget on a lit screen. Tearing down what the heartbeat would rebuild a moment later is churn.
 * - **Nothing is in flight:** no routine run, no file transfer, no widget refresh, and never while
 *   Remote Desktop streams. Runs and streams hold the connection with [ConnectionActivity.hold] for
 *   as long as they last; transfers are read from the transfer machinery itself when the countdown
 *   ends; a widget tap or a file message is a [ConnectionActivity.touch]. Each restarts the
 *   countdown from zero.
 *
 * What is stopped is the keepalive service, not the socket: the native client has no disconnect
 * call. Without the foreground service the process is an ordinary cached app again, which Android
 * freezes and reclaims, taking the quiet socket with it.
 *
 * Plain Kotlin with no Android dependency so the rule is unit-testable; [RemexClientManager] runs it.
 */
internal object ConnectionIdlePolicy {

    /**
     * OFF until Connor decides (2026-10-02). As built, the teardown stops the keepalive service for
     * every phone after ten background minutes, not only for widget- or routine-started connections
     * (RemEx-9yei0's original scope), so a PC can no longer reach an idle phone in the background
     * (PC-to-phone file pushes, anything else the PC starts). That trade of battery against
     * background reachability is a product call; flipping this to true wires the policy below in.
     */
    const val ENABLED = false

    /** How long the app must sit unused in the background before the keepalive is stopped. */
    const val IDLE_TIMEOUT_MS = 10 * 60_000L

    /**
     * How long to wait before tearing down, given what is true now: null while teardown is off the
     * table (foreground, the connection still wanted, or something in flight), 0 to tear down at
     * once, otherwise the milliseconds left in the idle countdown.
     *
     * @param foreground whether any of the app's UI is started.
     * @param connectionWanted whether [ReconnectGate.allows] would keep (re)connecting right now.
     * @param busy whether anything holds the connection: a routine run, a transfer, a widget
     *   refresh, or a Remote Desktop stream.
     * @param quietMs how long it has been since the last activity ended (or the app left the
     *   foreground, whichever is later).
     */
    fun teardownDelayMs(foreground: Boolean, connectionWanted: Boolean, busy: Boolean, quietMs: Long): Long? {
        if (foreground || connectionWanted || busy) return null
        return (IDLE_TIMEOUT_MS - quietMs.coerceAtLeast(0L)).coerceAtLeast(0L)
    }
}

/**
 * What is using the background connection right now (RemEx-9yei0). Callers [hold] a named reason
 * while their work runs and [release] it after; [touch] marks a one-off use such as a widget tap.
 * Holds are keyed rather than counted so a doubled release, or a release with no matching hold,
 * cannot drive the count negative and leave the connection held (or freed) forever.
 */
internal class ConnectionActivityTracker(private val clock: () -> Long = { System.nanoTime() / 1_000_000L }) {
    private val holds = mutableSetOf<String>()
    private var lastActivityMs = clock()

    /** Marks [reason] as using the connection until [release]. */
    @Synchronized
    fun hold(reason: String) {
        holds += reason
        lastActivityMs = clock()
    }

    /** Ends [reason]'s use. The idle countdown starts again from now. */
    @Synchronized
    fun release(reason: String) {
        holds -= reason
        lastActivityMs = clock()
    }

    /** Records a one-off use (a widget tap, an inbound transfer message). */
    @Synchronized
    fun touch() {
        lastActivityMs = clock()
    }

    @get:Synchronized
    val busy: Boolean get() = holds.isNotEmpty()

    /** Milliseconds since the last [hold], [release] or [touch]. */
    @get:Synchronized
    val quietMs: Long get() = clock() - lastActivityMs
}

/** The process-wide [ConnectionActivityTracker] and the hold names its callers use. */
internal object ConnectionActivity {
    val tracker = ConnectionActivityTracker()

    /** One hold per run, so two routines running at once each keep the connection until they end. */
    fun routineRun(runId: String) = "routine_run:$runId"

    const val REMOTE_DESKTOP = "remote_desktop"

    fun hold(reason: String) = tracker.hold(reason)

    fun release(reason: String) = tracker.release(reason)

    fun touch() = tracker.touch()
}
