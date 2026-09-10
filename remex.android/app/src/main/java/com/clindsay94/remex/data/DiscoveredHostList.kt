package com.clindsay94.remex.data

/**
 * Accumulates mDNS discovery results into a stable list (RemEx-ado4).
 *
 * **THE DEFECT THIS REPLACES:** discovery resolved the FIRST answer and stopped, so with two PCs
 * running RemEx on one LAN the app picked one by coin flip and the user never learned the other
 * existed. A list needs the opposite behaviour — keep listening, and survive services appearing,
 * disappearing and reappearing while the screen is open.
 *
 * Pure and NSD-free on purpose: the reduction is where the bugs live, and it is provable off-device.
 */
object DiscoveredHostList {

    /**
     * Identity of a discovered service.
     *
     * **THE SERVICE NAME, NOT THE ADDRESS**, and the distinction matters on both sides. A PC that
     * changes address — DHCP renewal, or a hop between Wi-Fi and Ethernet — is the SAME machine and
     * must update in place rather than appear twice. Conversely two different PCs can briefly share
     * an address as a lease moves between them, and keying on address would merge them.
     *
     * mDNS guarantees the service name is unique on the link, which is exactly the property needed
     * here. Note this is a DISCOVERY identity and deliberately not [com.clindsay94.remex.security.HostIdentity]:
     * that one keys on the pinned SPKI hash and is only knowable AFTER pairing, whereas this list
     * shows machines the user has never paired with.
     */
    private fun identity(host: DiscoveredHost): String = host.serviceName

    /**
     * Adds or updates a discovered service.
     *
     * @return a new list; the input is not modified.
     */
    fun onFound(current: List<DiscoveredHost>, found: DiscoveredHost): List<DiscoveredHost> {
        val index = current.indexOfFirst { identity(it) == identity(found) }

        // REPLACE IN PLACE RATHER THAN MOVE TO THE END. The list is rendered with entrance
        // animations, and re-resolution happens on its own schedule - so appending a re-found
        // service would make rows shuffle under the user's finger for reasons they cannot see, and
        // a tap could land on a different PC than the one they aimed at.
        if (index >= 0) {
            return current.toMutableList().also { it[index] = found }
        }

        return current + found
    }

    /**
     * Removes a service that mDNS reports as lost.
     *
     * Matched on identity rather than on equality, because a lost-service callback may carry a
     * stale address or a zero port — the resolve that filled those in happened separately, and the
     * loss notification does not repeat it. Matching on the whole object would silently fail to
     * remove anything, leaving a dead PC in the list for the user to tap.
     */
    fun onLost(current: List<DiscoveredHost>, lostServiceName: String): List<DiscoveredHost> =
        current.filterNot { identity(it) == lostServiceName }

    /**
     * Whether the single-result autofill convenience should fire.
     *
     * Exactly one host, or none of them. **Autofilling when several are visible is the original bug
     * wearing a list**: the user would see three PCs and find the field already filled with
     * whichever answered first, which is precisely the silent coin flip being removed.
     */
    fun shouldAutofill(current: List<DiscoveredHost>): Boolean = current.size == 1

    /**
     * Discards entries that are not usable as a connection target.
     *
     * A service can be announced before it is resolved, leaving a blank host or a zero port. Such a
     * row is a tap that does nothing, which reads as the app being broken rather than as discovery
     * still being in progress.
     */
    fun usableOnly(current: List<DiscoveredHost>): List<DiscoveredHost> = current.filter(::isUsable)

    /**
     * The same rule for one host, so the single-result path can apply it too (RemEx-7gk69).
     *
     * EXTRACTED BECAUSE THE RULE HAD NO PRODUCTION CALLER AT ALL. `usableOnly` was written for the
     * list this object exists to build, and that list is not wired up yet — while the live path,
     * `NsdDiscoveryManager.discoverHost`, returns a single resolved service straight to the connect
     * form. Both of its construction sites guard the HOST and neither validates the port, and this
     * predicate was the only port range check anywhere in the app's main source. A rule that nothing
     * calls is indistinguishable from a rule that works.
     *
     * One definition with two callers rather than a copy at the call site: if these two ever
     * disagree about what "usable" means, the list and the autofill start offering different sets of
     * PCs for reasons no one can see.
     */
    fun isUsable(host: DiscoveredHost): Boolean =
        host.host.isNotBlank() && host.port in 1..65535
}
