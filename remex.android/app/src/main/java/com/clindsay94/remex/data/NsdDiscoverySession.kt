package com.clindsay94.remex.data

/**
 * The mDNS operations a discovery session needs, with no Android types in sight (RemEx-tslc1).
 *
 * **THIS INTERFACE EXISTS BECAUSE THE SESSION LOGIC IS WHERE THE BUGS ARE AND NsdManager IS WHERE
 * THE TESTS CANNOT GO.** `NsdDiscoveryManager` is entirely framework-bound — `NsdManager`,
 * `WifiManager`, `Context` — and this module has no Robolectric, so a streaming implementation
 * written directly against it would be unprovable off-device, on a feature whose only other
 * verification needs two PCs on one LAN. Behind these four calls the interesting questions become
 * ordinary Kotlin: what a second announcement for a name already resolving should do, what `stop`
 * has to unregister, whether a service that never resolves can still be lost.
 *
 * **THE IMPLEMENTOR CHOOSES THE THREAD, AND OWES [NsdDiscoverySession] A SINGLE ONE.** `NsdManager`
 * delivers `DiscoveryListener` and `ServiceInfoCallback` events on an `Executor` the implementor
 * supplies, so it is in a position to put both on the same one and to accept `start`/`stop` there
 * too. The requirement sits here rather than on the session for a reason: an implementation that
 * hides its executor makes the session's precondition impossible for any caller to satisfy, and a
 * draft of exactly that shape is why the real adapter was pulled back out into RemEx-pxgrm.
 *
 * Deliberately narrower than `NsdManager`: a service is identified by NAME here, because that is
 * what the session keys on and what [DiscoveredHostList] uses as identity. An implementation carries
 * whatever `NsdServiceInfo` it needs alongside.
 */
interface NsdPlatform {

    /** Starts discovery. Found and lost services arrive on [listener] until [stopDiscovery]. */
    fun startDiscovery(listener: Listener)

    /** Stops discovery. Safe to call when discovery never started. */
    fun stopDiscovery()

    /**
     * Begins resolving [serviceName], reporting the address and port through [onResolved].
     *
     * May report more than once — a service that changes address updates in place — and may never
     * report at all if the SRV record does not arrive. An implementation that cannot start the
     * resolve **must report the service lost**, or the session holds the name as in flight and
     * discards the re-announcements that would otherwise retry it.
     */
    fun startResolve(serviceName: String, onResolved: (host: String, port: Int) -> Unit)

    /** Cancels a resolve started by [startResolve]. Safe to call for a name that is not resolving. */
    fun cancelResolve(serviceName: String)

    interface Listener {
        fun onServiceFound(serviceName: String)

        fun onServiceLost(serviceName: String)

        /**
         * Discovery is not running, and no announcement will arrive.
         *
         * **THE INTERFACE NEEDS THIS EVEN THOUGH NOTHING RENDERS IT YET.** Without it a failed start
         * is indistinguishable from a LAN with no PCs on it: the list stays empty for as long as the
         * screen is open, and the user goes looking for a network problem that is not there. That is
         * the silence this feature exists to remove, and the one place the streaming path would
         * otherwise be weaker than the single-resolve path it replaces, which resolves to null on the
         * same event.
         */
        fun onDiscoveryFailed()
    }
}

/**
 * Keeps listening for RemEx hosts and accumulates them into a list (RemEx-tslc1, RemEx-8ih5 item 1).
 *
 * **THE DEFECT THIS REPLACES IS "RESOLVE THE FIRST ANSWER AND STOP".** With two PCs running RemEx on
 * one LAN the app picked one by coin flip and the user never learned the other existed. Discovery has
 * to keep running for as long as the screen is open, resolve every announcement it sees, and survive
 * services appearing, disappearing and reappearing while it does.
 *
 * The reduction itself is [DiscoveredHostList] and is not repeated here — this type owns the session:
 * what is being resolved, what a duplicate announcement means, and what has to be released on the way
 * out.
 *
 * **SINGLE-THREAD CONFINED, AND THE CONFINEMENT IS THE PLATFORM'S TO PROVIDE.** Nothing here is
 * synchronised, and a draft that added locks instead was abandoned rather than finished. It was right
 * about the deadlock it set out to avoid and kept producing fresh hazards under review: first a
 * callout to the consumer while holding the monitor, then — once that moved outside — emissions
 * delivered in a different order from the one they were computed in, so a resolve racing a stop could
 * leave the consumer holding a list this class had already discarded. Every one of those existed only
 * because a second thread had been introduced beneath it. [NsdPlatform] now states that the
 * implementor owes this class one thread, which it can honour because it is the one handing
 * `NsdManager` its `Executor`. One thread makes those hazards unreachable rather than guarded, which
 * is worth more than any of the guards were.
 */
class NsdDiscoverySession(
    private val platform: NsdPlatform,
    /**
     * Receives the accumulated list, **unfiltered**. Prefer binding [usable]; see its note.
     *
     * **MAY RE-ENTER THIS CLASS, AND THAT IS NOT THEORETICAL.** The natural wiring for this feature
     * reads the list, sees a single host, autofills it and calls [stop] — `DiscoveredHostList.shouldAutofill`
     * exists for precisely that. So this can call back in while the method that emitted is still on
     * the stack, and the entry points are written to survive it.
     */
    private val onHostsChanged: (List<DiscoveredHost>) -> Unit,
) : NsdPlatform.Listener {

    private var hosts: List<DiscoveredHost> = emptyList()

    /** Names with a resolve in flight, so a repeat announcement does not start a second one. */
    private val resolving = mutableSetOf<String>()

    private var started = false

    /** The accumulated list, INCLUDING services that have not resolved yet. */
    val current: List<DiscoveredHost> get() = hosts

    /**
     * The list as it should be shown: resolved entries only.
     *
     * **THE ONE TO BIND A LIST TO.** [current] is the accumulator and holds services that have been
     * announced but not yet resolved — rows that do nothing when tapped, which reads as the app being
     * broken rather than as discovery still working. Offering the filtered view rather than only
     * documenting the obligation means the obvious wiring is also the correct one.
     */
    val usable: List<DiscoveredHost> get() = DiscoveredHostList.usableOnly(hosts)

    /**
     * Whether discovery failed to start, as opposed to simply having found nothing yet.
     *
     * Recorded rather than rendered: RemEx-8ih5 owns what a screen says about it. What matters here is
     * that the two stop being the same state.
     */
    var failed: Boolean = false
        private set

    fun start() {
        if (started) return
        started = true
        failed = false
        platform.startDiscovery(this)
    }

    override fun onDiscoveryFailed() {
        if (!started) return
        failed = true
    }

    override fun onServiceFound(serviceName: String) {
        // **REFUSED AFTER stop(), BECAUSE stopServiceDiscovery IS ASYNCHRONOUS.** An announcement
        // already queued can still be delivered after the session has been torn down, and without
        // this it did real and permanent damage: the name was added to `resolving` for a resolve that
        // never started, so the NEXT time the screen opened that PC was skipped as already resolving
        // and stayed a blank row hidden by `usable` for the life of the session. The user simply
        // never saw their desk PC again.
        if (!started || serviceName.isBlank()) return

        // ENTERED INTO THE LIST BEFORE IT RESOLVES, WITH A BLANK ADDRESS, TO CLAIM THE SLOT. The entry
        // is the identity the resolve then UPDATES IN PLACE, which is the shipped onFound rule and
        // what stops rows shuffling under the user's finger between announcement and resolution. It
        // also means this list is not directly renderable, which is what [usable] is for.
        if (hosts.none { it.serviceName == serviceName }) {
            emit(DiscoveredHostList.onFound(hosts, DiscoveredHost(serviceName, "", 0)))
        }

        // **RE-CHECKED BECAUSE THE EMIT ABOVE CAN HAVE STOPPED US.** onHostsChanged is the consumer's
        // code, and the natural wiring autofills a single result and calls stop() from inside it.
        // That stop() ran to completion here - clearing `resolving` - and then this method carried on
        // and put the name back, for a resolve it started on a platform that had already stopped. The
        // session was left with a name pinned in `resolving` forever, so on the next visit that PC
        // was skipped as already resolving and never appeared again. The same failure the guard at
        // the top of this method exists to prevent, reached through the consumer rather than through
        // a late callback, and no second thread required.
        if (!started) return

        // A SECOND ANNOUNCEMENT FOR A NAME ALREADY RESOLVING IS NOT A SECOND PC. mDNS re-announces,
        // and every extra resolve is a callback somebody has to unregister - the leak shape that
        // crashed the app in RemEx-0ov when a late event landed on a dead executor.
        if (!resolving.add(serviceName)) return

        platform.startResolve(serviceName) { host, port ->
            // Guarded because a resolve can report after its service was lost: unregistering is
            // asynchronous, so a final update can already be in flight. Re-adding here would
            // resurrect a PC the user just watched disappear, and nothing would remove it again - the
            // lost callback has already been and gone.
            if (serviceName in resolving) {
                emit(DiscoveredHostList.onFound(hosts, DiscoveredHost(serviceName, host, port)))
            }
        }
    }

    override fun onServiceLost(serviceName: String) {
        if (!started) return

        val wasResolving = resolving.remove(serviceName)
        val known = hosts.any { it.serviceName == serviceName }
        if (!wasResolving && !known) return

        // **THESE TWO LINES ARE GUARDED TWICE OVER AND IT TAKES BOTH SLIPS TO BREAK THEM.** The
        // interface permits a platform to report the loss synchronously from inside cancelResolve, so
        // this method can re-enter itself. The first version cancelled UNCONDITIONALLY and did it
        // BEFORE removing the host, so the re-entry sailed past the early return above and cancelled
        // again: unbounded recursion, and a StackOverflowError on the way out of a screen.
        //
        // Removing the host first stops it, because the second entry then knows nothing about the
        // name and returns. Making the cancel conditional stops it too, because the second entry is
        // no longer resolving. Mutation testing says either alone is sufficient - restore BOTH slips
        // and the crash comes back. Written this way because the ordering is not visibly
        // load-bearing, and a later edit that swapped these lines for tidiness would look harmless.
        if (known) emit(DiscoveredHostList.onLost(hosts, serviceName))
        if (wasResolving) platform.cancelResolve(serviceName)
    }

    /**
     * Stops discovery and releases every resolve still in flight.
     *
     * **THE RESOLVES ARE THE PART THAT IS EASY TO FORGET.** Stopping discovery ends the announcements
     * but says nothing about the per-service callbacks already registered; each one outlives the
     * session and eventually posts to an executor that is gone. Idempotent, because a screen can be
     * left in more than one way and a second stop must not re-enter the platform.
     */
    fun stop() {
        if (!started) return
        started = false

        platform.stopDiscovery()
        // COPIED BEFORE ITERATING, AND THE REASON CHANGED UNDER IT. The copy was added because a
        // synchronous onServiceLost from cancelResolve would mutate the set mid-walk; `started` is
        // false by this point, so that re-entry returns before touching it and removing the copy
        // breaks nothing. Kept as cheap defence, but a mutation test does NOT kill its removal, and
        // pretending otherwise is how a comment outlives the code it describes.
        for (name in resolving.toList()) platform.cancelResolve(name)
        // LOAD BEARING SINCE THE started GUARD LANDED, having been redundant before it: the
        // re-entrant losses used to drain this set and now return early instead. Remove it and a
        // restarted session skips every service it saw last time as already resolving.
        resolving.clear()

        // **THE LIST IS DROPPED RATHER THAN KEPT WARM FOR THE NEXT VISIT.** Holding it would show the
        // user PCs that left the LAN while the screen was closed - discovery was not running, so no
        // lost callback arrived for them and nothing would ever remove them. Re-discovery takes about
        // a second and is correct; a stale list is wrong for as long as it is shown.
        emit(emptyList())
    }

    private fun emit(next: List<DiscoveredHost>) {
        if (next == hosts) return
        // ASSIGNED BEFORE THE CALLOUT, NOT AFTER. onHostsChanged may re-enter this class, and a
        // re-entrant reader that saw the OLD list would be reading state this call has superseded.
        hosts = next
        onHostsChanged(next)
    }
}
