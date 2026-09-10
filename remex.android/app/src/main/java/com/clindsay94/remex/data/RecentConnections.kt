package com.clindsay94.remex.data

/**
 * One address this phone has successfully connected to (RemEx-obxlo).
 *
 * Address-keyed, which is the deliberate opposite of [KnownHost]. That list answers "which PCs am I
 * paired with", and collapsing a machine's several addresses into one row is right for it. This one
 * answers "where have I connected", and the address IS the answer — a PC on a DHCP lease that moved
 * is exactly the thing the user is trying to get back to.
 *
 * [identity] is the [com.clindsay94.remex.security.HostIdentity] the PC presented at the time, kept
 * only so the per-machine cap and an unpair can find a machine's addresses. It is blank when the
 * connection had no pin to derive one from, and it is never treated as proof of current trust: a
 * certificate can change under an address that is still in this list.
 */
data class RecentConnection(
    val address: String,
    val port: Int,
    val identity: String,
    val lastConnectedAtMillis: Long
)

/**
 * A row in the Known PCs card: one address, plus whatever is currently known about the PC behind it.
 *
 * Built by [RecentConnections.rows] and stored nowhere — a join of history against the live
 * pinned-host map, recomputed whenever either changes, so a row can never assert a trust that has
 * since lapsed.
 */
data class KnownPcEntry(
    val address: String,
    val port: Int,
    /** What the user called the PC, or blank when they never named it or it is unrecognised. */
    val nickname: String,
    val lastConnectedAtMillis: Long,
    /**
     * The paired PC this row's rename and unpair act on, or null when nothing pinned matches.
     *
     * Present does NOT imply [isTrusted]: a machine still paired at its Tailscale address supplies
     * the nickname for a stale LAN address that would have to be paired again.
     */
    val knownHost: KnownHost?,
    /**
     * Whether THIS address is currently pinned — i.e. whether connecting to it needs a PIN.
     *
     * The pin store is address-keyed, so the machine being paired elsewhere buys this address
     * nothing.
     */
    val isTrusted: Boolean
) {
    val hasEverConnected: Boolean
        get() = lastConnectedAtMillis > 0L

    /** The row's headline: the PC's name when it has one, otherwise the address itself. */
    val displayName: String
        get() = nickname.ifBlank { address }
}

/**
 * The last few addresses this phone connected to, and the rows the Known PCs card renders.
 *
 * Pure and DataStore-free for the same reason [KnownHosts] is: the ordering, the caps and the join
 * against the pinned-host map are where the bugs live, and all three are provable off-device.
 *
 * **Kept separate from the pinned-host store on purpose.** An entry has to outlive its pairing — a
 * PC that was reinstalled, or whose certificate changed, is precisely the one the user is trying to
 * reach again, and a list derived from current trust would drop it at the moment it became useful.
 * Tapping such a row starts an ordinary PIN pairing instead.
 */
object RecentConnections {

    /** How many address rows the card offers. */
    const val MaxEntries = 5

    /**
     * How many of those rows one machine may occupy.
     *
     * Without this a single PC on a churning DHCP lease fills every slot and hides the others —
     * which is the failure the certificate-keyed grouping in [KnownHosts] exists to prevent, and
     * re-introducing it wholesale would trade one bad list for another.
     */
    const val MaxPerMachine = 2

    /**
     * Preference key holding the whole list, encoded by [encode].
     *
     * One key rather than a `recentConnection_<n>_<field>` family: the list is rewritten as a unit
     * on every connection, and indexed keys would need every removal to be part of that same
     * transaction or leave a stale tail behind.
     *
     * **Not in `SettingsExport.ExportableKeys`**, and that whitelist is what decides — a key nobody
     * added is simply absent from an export. Same call as the `knownHost_` keys and for the same
     * reason: a list of addresses and times says which machines someone reaches and when.
     */
    const val KeyName = "recentConnections"

    /**
     * Between records: ASCII RS (0x1E), which no hostname, address, port or timestamp can contain.
     *
     * Written as a code point rather than a literal so it stays visible in a diff — an invisible
     * control character in source reviews as an empty string.
     */
    val RecordSeparator: String = Char(0x1E).toString()

    /** Between fields: ASCII US (0x1F), the separator `PinnedHostStore` uses for its aliases. */
    val FieldSeparator: String = Char(0x1F).toString()

    private const val FieldCount = 4

    fun encode(entries: List<RecentConnection>): String =
        entries.joinToString(RecordSeparator) { entry ->
            listOf(
                    entry.address,
                    entry.port.toString(),
                    entry.identity,
                    entry.lastConnectedAtMillis.toString()
                )
                .joinToString(FieldSeparator)
        }

    /**
     * Reads the stored list back.
     *
     * A malformed record is IGNORED rather than defaulted, matching [KnownHosts.parseRecords]: a
     * preference file written by an older or newer build must not be able to invent an address the
     * user never connected to, and a record with an unparseable port would otherwise become a row
     * that dials port 0.
     *
     * The caps are applied on the way out as well as the way in, so a file holding more than
     * [MaxEntries] — hand-edited, or written by a build with a different limit — still renders the
     * list this app promises.
     */
    fun parse(encoded: String?): List<RecentConnection> {
        if (encoded.isNullOrEmpty()) return emptyList()

        val entries = mutableListOf<RecentConnection>()
        for (record in encoded.split(RecordSeparator)) {
            if (record.isBlank()) continue

            val fields = record.split(FieldSeparator)
            if (fields.size != FieldCount) continue

            val address = fields[0].trim()
            if (address.isEmpty()) continue

            val port = fields[1].toIntOrNull() ?: continue
            if (port !in 1..65535) continue

            val lastConnectedAtMillis = fields[3].toLongOrNull() ?: continue
            if (lastConnectedAtMillis < 0L) continue

            entries.add(
                RecentConnection(
                    address = address,
                    port = port,
                    identity = fields[2].trim(),
                    lastConnectedAtMillis = lastConnectedAtMillis
                )
            )
        }

        return capped(dedupedByAddress(entries))
    }

    /**
     * Records a connection that actually succeeded, at the front of the list.
     *
     * Attempts are deliberately not recorded, for the reason
     * `SettingsManager.recordKnownHostConnection` already gives: an ordering the user can trust has
     * to mean connected, or a PC that is powered off climbs to the top every time they try it and
     * fail.
     *
     * Re-connecting to an address already listed MOVES it to the front rather than adding a second
     * copy, and refreshes its port and identity — the port because the user may have changed it, the
     * identity because a re-pair after a certificate change is exactly when it moves.
     */
    fun record(
        existing: List<RecentConnection>,
        address: String,
        port: Int,
        identity: String,
        atMillis: Long
    ): List<RecentConnection> {
        val trimmed = address.trim()
        if (trimmed.isEmpty()) return existing
        if (port !in 1..65535) return existing

        val entry =
            RecentConnection(
                address = trimmed,
                port = port,
                identity = identity.trim(),
                lastConnectedAtMillis = atMillis
            )

        return capped(listOf(entry) + existing.filterNot { it.address == trimmed })
    }

    /**
     * Drops every address belonging to one machine.
     *
     * Called when the user unpairs it: they asked to forget the PC, and leaving its addresses behind
     * would go on offering it by name. A certificate that merely CHANGED does not come through here
     * — that PC keeps its rows, which is the entire point of the list outliving trust.
     *
     * Matches on the recorded identity AND on membership of the unpaired machine's address list,
     * because the two disagree in an ordinary case: an address connected to before this build
     * started recording identities carries a blank one while still belonging to the machine.
     */
    fun forgetMachine(
        existing: List<RecentConnection>,
        identity: String,
        addresses: Collection<String>
    ): List<RecentConnection> {
        val key = identity.trim()
        val owned = addresses.map { it.trim() }.filter { it.isNotEmpty() }.toSet()
        if (key.isEmpty() && owned.isEmpty()) return existing

        return existing.filterNot {
            (key.isNotEmpty() && it.identity == key) || it.address in owned
        }
    }

    /**
     * Drops one address.
     *
     * For a row whose PC is no longer paired anywhere: unpair cannot reach it — there is nothing
     * left to unpair — so without this a dead address would sit in the list forever.
     */
    fun forgetAddress(existing: List<RecentConnection>, address: String): List<RecentConnection> {
        val trimmed = address.trim()
        if (trimmed.isEmpty()) return existing
        return existing.filterNot { it.address == trimmed }
    }

    /**
     * The rows the Known PCs card renders: recent addresses first, then any paired PC without one.
     *
     * **The trailing rows are not optional.** Rename and unpair are reached from a row's overflow, so
     * a paired machine with nothing in the recent list — a fresh pairing that has not connected yet,
     * or an install predating this list — would otherwise be invisible and unmanageable while still
     * holding a pin. One row per such PC at its preferred address, most recently connected first,
     * which is exactly the list this card showed before it became address-keyed.
     */
    fun rows(recent: List<RecentConnection>, knownHosts: List<KnownHost>): List<KnownPcEntry> {
        val rows = mutableListOf<KnownPcEntry>()
        val represented = mutableSetOf<String>()

        for (entry in capped(dedupedByAddress(recent))) {
            // Trust is decided by THIS address being pinned, never by the identity: the pin store is
            // address-keyed, so a machine paired at its Tailscale name buys a stale LAN address
            // nothing. The identity match below may only supply a nickname and a rename/unpair
            // target.
            val pairedHere = knownHosts.firstOrNull { entry.address in it.addresses }
            val machine =
                pairedHere
                    ?: entry.identity
                        .takeIf { it.isNotEmpty() }
                        ?.let { id -> knownHosts.firstOrNull { it.identity == id } }

            machine?.let { represented.add(it.identity) }

            rows.add(
                KnownPcEntry(
                    address = entry.address,
                    port = entry.port,
                    nickname = machine?.nickname.orEmpty(),
                    lastConnectedAtMillis = entry.lastConnectedAtMillis,
                    knownHost = machine,
                    isTrusted = pairedHere != null
                )
            )
        }

        for (host in knownHosts.sortedByDescending { it.lastConnectedAtMillis }) {
            if (host.identity in represented) continue
            rows.add(
                KnownPcEntry(
                    address = host.preferredAddress,
                    port = host.port,
                    nickname = host.nickname,
                    lastConnectedAtMillis = host.lastConnectedAtMillis,
                    knownHost = host,
                    isTrusted = true
                )
            )
        }

        return rows
    }

    /** First occurrence wins, so the caller's most-recent-first order survives. */
    private fun dedupedByAddress(entries: List<RecentConnection>): List<RecentConnection> {
        val seen = mutableSetOf<String>()
        return entries.filter { seen.add(it.address) }
    }

    /**
     * [MaxEntries] overall, at most [MaxPerMachine] from any one PC.
     *
     * An entry with no identity is counted against its own address rather than pooled with the other
     * unidentified ones: they are not known to be the same machine, and treating them as one would
     * hide addresses on a guess.
     */
    private fun capped(entries: List<RecentConnection>): List<RecentConnection> {
        val kept = mutableListOf<RecentConnection>()
        val perMachine = mutableMapOf<String, Int>()

        for (entry in entries) {
            if (kept.size >= MaxEntries) break

            val machine = entry.identity.ifEmpty { "@${entry.address}" }
            val used = perMachine[machine] ?: 0
            if (used >= MaxPerMachine) continue

            perMachine[machine] = used + 1
            kept.add(entry)
        }

        return kept
    }
}
