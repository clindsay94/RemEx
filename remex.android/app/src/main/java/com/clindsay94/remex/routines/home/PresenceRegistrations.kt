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
     * Brings the registrations from [previous] (what was last applied, persisted) to [plan], and
     * returns what is now applied.
     *
     * **NEVER RE-REGISTERS THE PENDINGINTENT WHEN NOTHING CHANGED.** Re-registering it makes
     * ConnectivityService drop the old request and immediately deliver "available" again for a Wi-Fi
     * that already matches, and if the handler of that broadcast re-registered, the result was a
     * broadcast -> register -> broadcast loop that also kept cancelling its own evaluation
     * (REGRESSION-GUARDS). So the PendingIntent is (re)registered only when the callback plan turns on,
     * or when [force] says a reboot, an update or a process start may have dropped it; never from the
     * network broadcast, never after an evaluation.
     *
     * [force] re-does everything the plan wants: a registration from before a reboot or an update no
     * longer exists, and nothing reports that it is gone.
     */
    fun apply(
        plan: PresenceRegistrationPlan,
        previous: PresenceRegistrationPlan?,
        force: Boolean,
        port: PresenceRegistrationPort,
    ): PresenceRegistrationPlan {
        if (!force && plan == previous) return plan
        if (plan.networkCallback) {
            if (force || previous?.networkCallback != true) port.registerPendingIntentCallback()
            port.registerInProcessCallback()
        } else if (force || previous?.networkCallback != false) {
            port.unregisterPendingIntentCallback()
            port.unregisterInProcessCallback()
        }
        if (plan.periodicCheck) {
            if (force || previous?.periodicCheck != true) port.schedulePeriodicCheck()
        } else if (force || previous?.periodicCheck != false) {
            port.cancelPeriodicCheck()
        }
        return plan
    }
}

/** What a presence broadcast makes RemEx do (spec §8.3.1, §12). Pure, so the rules are proven off-device. */
enum class PresenceAction {
    /** Reset presence to UNKNOWN: after a reboot nothing is known (T20, L9). */
    FORGET_PRESENCE,

    /** Re-register everything the plan wants, whatever was applied before. */
    SYNC_FORCED,

    /** Queue one presence evaluation. */
    EVALUATE,

    /** A network broadcast arrived while nothing is armed: drop the stale registration, read nothing else. */
    UNREGISTER_STALE,
}

object PresenceEventRouter {
    const val NETWORK = "network"
    const val BOOT = "boot"
    const val PACKAGE_REPLACED = "package_replaced"

    /**
     * [armed] is the cheap flag (a plain pref of the applied plan): when false, no path decrypts the
     * store, starts the native core or enqueues work (§12 "0 registrations, 0 work"). The network
     * broadcast only ever evaluates; it never re-registers (see [PresenceRegistrar.apply]).
     */
    fun actionsFor(event: String, armed: Boolean): List<PresenceAction> =
        when {
            event == NETWORK && armed -> listOf(PresenceAction.EVALUATE)
            event == NETWORK -> listOf(PresenceAction.UNREGISTER_STALE)
            !armed -> emptyList()
            event == BOOT -> listOf(PresenceAction.FORGET_PRESENCE, PresenceAction.SYNC_FORCED, PresenceAction.EVALUATE)
            event == PACKAGE_REPLACED -> listOf(PresenceAction.SYNC_FORCED, PresenceAction.EVALUATE)
            else -> emptyList()
        }
}
