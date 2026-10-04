package com.clindsay94.remex.ui.screens

import com.clindsay94.remex.data.KnownPcEntry

/** An address and port, the way the Connection screen tells one remembered PC row from another. */
data class PcEndpoint(val address: String, val port: Int) {
    fun matches(entry: KnownPcEntry): Boolean =
        port == entry.port && address.trim().equals(entry.address.trim(), ignoreCase = true)
}

/** What a "Your PCs" card says about its PC, in the order the screen cares about. */
enum class YourPcStatus {
    /** This phone is connected to this PC at this address right now. */
    ConnectedNow,

    /**
     * Connected right now, but the PC no longer recognises this phone, so it refuses everything
     * until the phone pairs again (sweep P3, [com.clindsay94.remex.RemexClientManager.needsPairing]).
     */
    ConnectedNeedsPairing,

    /** A connection to this address is being made. */
    Connecting,

    /** The phone isn't paired at this address yet, so connecting asks for the PC's PIN. */
    NeedsPairing,

    /** Paired and not connected: one tap reconnects. */
    Ready,
}

/** One card on the Connection screen's "Your PCs" list (RemEx-wqo7a.6). */
data class YourPcCard(val entry: KnownPcEntry, val status: YourPcStatus) {
    /** The card for the connection that is up right now; it gets no Connect button. */
    val isCurrent: Boolean
        get() = status == YourPcStatus.ConnectedNow || status == YourPcStatus.ConnectedNeedsPairing

    /** Stable per card: the rows are deduped by address (RecentConnections.rows). */
    val key: String
        get() = entry.address
}

/**
 * Builds the "Your PCs" cards from the remembered rows (RemEx-wqo7a.6).
 *
 * One card per remembered ADDRESS, not per machine, on purpose: RemEx-obxlo showed that folding a
 * PC's addresses into one row hid the address the person actually needed (a DHCP move, a Tailscale
 * address), and RecentConnections already caps any one machine at two rows.
 *
 * Status comes only from what the phone already knows: the connection that is up, the one being
 * made, and the pairing on file. Nothing is probed on the network to fill it in.
 *
 * The connected card goes first, so the PC you are using is always the top card; every other card
 * keeps the rows' most-recent-first order.
 */
object YourPcCards {
    fun build(
        rows: List<KnownPcEntry>,
        connected: PcEndpoint?,
        connecting: PcEndpoint?,
        connectedNeedsPairing: Boolean = false,
    ): List<YourPcCard> {
        var currentMarked = false
        val cards = rows.map { entry ->
            val status = when {
                !currentMarked && connected?.matches(entry) == true -> {
                    currentMarked = true
                    if (connectedNeedsPairing) YourPcStatus.ConnectedNeedsPairing else YourPcStatus.ConnectedNow
                }
                connecting?.matches(entry) == true -> YourPcStatus.Connecting
                !entry.isTrusted -> YourPcStatus.NeedsPairing
                else -> YourPcStatus.Ready
            }
            YourPcCard(entry, status)
        }
        val (current, rest) = cards.partition { it.isCurrent }
        return current + rest
    }

    /**
     * What the "Your PCs" header says, from the same facts as the cards under it (3.0 comb,
     * conn-header-contradicts): a connection the PC refuses reads "Needs pairing" up there too,
     * instead of "Connected" above a card that says otherwise. Null when nothing is connected or
     * being connected, where the view model's own line ("Disconnected", an error) stands.
     *
     * The connected PC may have no card yet (its address is recorded once the connection lands), so
     * the flag counts as well as the card.
     */
    fun headerStatus(
        cards: List<YourPcCard>,
        isConnected: Boolean,
        isConnecting: Boolean,
        connectedNeedsPairing: Boolean,
    ): YourPcStatus? =
        when {
            isConnected &&
                (connectedNeedsPairing || cards.any { it.status == YourPcStatus.ConnectedNeedsPairing }) ->
                YourPcStatus.ConnectedNeedsPairing
            isConnected -> YourPcStatus.ConnectedNow
            isConnecting -> YourPcStatus.Connecting
            else -> null
        }
}
