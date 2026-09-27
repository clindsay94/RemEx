package com.clindsay94.remex.routines.home

/**
 * Which presence event sources should exist right now (spec §8.3.1 "Event sources", §12 budget).
 *
 * - [networkCallback]: the `registerNetworkCallback(request, PendingIntent)` registration AND the
 *   in-process callback. Only while a home is set and at least one enabled `home.*` routine exists:
 *   a phone with no home routines has zero registrations and zero work.
 * - [periodicCheck]: the 15-minute leave fallback. Only while that home is HOME and an enabled
 *   `home.leave` routine exists. A PendingIntent network callback reports only the "available" edge,
 *   so with mobile data always on, leaving home produces no new network and no event at all: without
 *   this check the leave would never be seen while RemEx is not running (REGRESSION-GUARDS).
 */
data class PresenceRegistrationPlan(val networkCallback: Boolean, val periodicCheck: Boolean) {
    companion object {
        val NONE = PresenceRegistrationPlan(networkCallback = false, periodicCheck = false)

        fun of(homeSet: Boolean, enabledHomeRoutines: Int, enabledLeaveRoutines: Int, state: PresenceState?): PresenceRegistrationPlan {
            val callback = homeSet && enabledHomeRoutines > 0
            return PresenceRegistrationPlan(
                networkCallback = callback,
                periodicCheck = callback && enabledLeaveRoutines > 0 && state == PresenceState.HOME,
            )
        }
    }
}

/** The Android registrations behind a seam (fake in `NetworkRegistrationTest`). Each call is idempotent. */
interface PresenceRegistrationPort {
    /** `registerNetworkCallback(request, PendingIntent)`; re-registering the same PendingIntent replaces it. */
    fun registerPendingIntentCallback()

    fun unregisterPendingIntentCallback()

    /** The in-process `NetworkCallback`, for a fast leave while the process lives. */
    fun registerInProcessCallback()

    fun unregisterInProcessCallback()

    /** The `routine-presence-check` periodic work (15 min). */
    fun schedulePeriodicCheck()

    fun cancelPeriodicCheck()
}

object PresenceRegistrar {
    /**
     * Makes the registrations match [plan]. Called on process start, after BOOT_COMPLETED and
     * MY_PACKAGE_REPLACED (both drop every registration an app holds), after every routine edit and
     * after every presence change. Registering is always re-done, never assumed: a registration from
     * before a reboot or an update no longer exists, and nothing reports that it is gone.
     */
    fun apply(plan: PresenceRegistrationPlan, port: PresenceRegistrationPort) {
        if (plan.networkCallback) {
            port.registerPendingIntentCallback()
            port.registerInProcessCallback()
        } else {
            port.unregisterPendingIntentCallback()
            port.unregisterInProcessCallback()
        }
        if (plan.periodicCheck) port.schedulePeriodicCheck() else port.cancelPeriodicCheck()
    }
}
