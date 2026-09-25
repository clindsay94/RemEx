package com.clindsay94.remex

/**
 * Decides when the auto-reconnect heartbeat in [RemexClientManager.initialize] may try to reach the
 * PC, and how long it backs off between tries (perf audit P0-11).
 *
 * Before this, the heartbeat ran with no gate at all: a 5 s wake while connected just to check the
 * flag, and while disconnected a TLS attempt on an exponential backoff (plus a 3 s multicast burst
 * after three failures), with no network, the screen off and nobody using the app. The START_STICKY
 * keepalive service keeps the process alive, so that was a permanent background wake source.
 *
 * **NO NETWORK, NO ATTEMPT.** With no default network there is nothing to connect over; every try
 * is a guaranteed failure that still wakes the CPU and grows the backoff for when it matters.
 *
 * **BACKGROUNDED, NO ATTEMPT - UNLESS A HARDWARE WIDGET IS ON A LIT SCREEN.** Nobody is looking at a
 * backgrounded app, but the home-screen hardware widget renders from the live connection (the same
 * exception [TelemetryBackgroundGate] makes), so leaving it disconnected freezes it. It only matters
 * while the screen is on, which is the one place screen state enters the decision.
 *
 * The gate only ever PAUSES attempts; it never tears down a live connection, and the caller resumes
 * the moment it reopens. Plain Kotlin with no Android dependency so the table is unit-testable.
 */
internal object ReconnectGate {

    /** The heartbeat's base interval: the first backoff step, and the grace after a disconnect. */
    const val BASE_DELAY_MS = 5_000L

    /** Ceiling on the exponential backoff between failed attempts. */
    const val MAX_DELAY_MS = 300_000L // 5 minutes

    /**
     * Whether an auto-reconnect attempt is worth making right now.
     *
     * @param networkAvailable whether the device has a default network (ConnectivityManager).
     * @param foreground whether any of the app's UI is started ([RemexClientManager.appForeground]).
     * @param screenInteractive whether the screen is on (PowerManager.isInteractive / SCREEN_ON/OFF).
     * @param widgetPlaced whether a hardware widget is on the home screen and needs the live stream.
     */
    fun allows(
            networkAvailable: Boolean,
            foreground: Boolean,
            screenInteractive: Boolean,
            widgetPlaced: Boolean,
    ): Boolean = networkAvailable && (foreground || (screenInteractive && widgetPlaced))

    /**
     * The heartbeat's backoff after [consecutiveFailures] failed attempts:
     * min(BASE_DELAY_MS * 2^failures, MAX_DELAY_MS). Unchanged by P0-11; extracted so it is pinned.
     */
    fun backoffDelayMs(consecutiveFailures: Int): Long =
            // 2^20 * 5000ms ≈ 87 minutes, which already exceeds MAX_DELAY_MS (5 min),
            // so coerceAtMost(20) safely avoids Long overflow on the shift.
            minOf(BASE_DELAY_MS * (1L shl consecutiveFailures.coerceAtMost(20)), MAX_DELAY_MS)

    /**
     * Whether a trusted host found by mDNS self-heal should reset the backoff (perf audit P1-1).
     *
     * Only a DIFFERENT address is news worth retrying at once. If discovery just finds the address
     * that is already saved - and already failing - the PC answers mDNS but refuses the connection
     * (firewall, agent not running), and resetting here pinned the retry near the fast end (~40 s)
     * forever instead of letting it climb to [MAX_DELAY_MS]. Hostnames compare case-insensitively.
     */
    fun selfHealFoundNewAddress(
            savedHost: String,
            savedPort: Int,
            discoveredHost: String,
            discoveredPort: Int,
    ): Boolean =
            savedPort != discoveredPort ||
                    !savedHost.trim().equals(discoveredHost.trim(), ignoreCase = true)
}
