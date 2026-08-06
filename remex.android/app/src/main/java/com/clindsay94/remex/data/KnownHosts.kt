package com.clindsay94.remex.data

import com.clindsay94.remex.security.HostIdentity

/**
 * One physical PC the phone is paired with, as the Known PCs list shows it (RemEx-k62t).
 *
 * Keyed by [HostIdentity], never by address. `PinnedHostStore.listPaired` is address-keyed and one
 * PC answers at several — LAN IP, Tailscale, hostname, plus whatever DHCP hands it next — so an
 * address-keyed row would show one machine three times, each holding its own third of the user's
 * nickname and last-connected time.
 */
data class KnownHost(
    /** Stable opaque key from [HostIdentity.keyFor]. Never the pin itself. */
    val identity: String,
    /** What the user called it, or blank when they never named it. */
    val nickname: String,
    /** Every address this PC is paired at, the one that last worked first. Never empty. */
    val addresses: List<String>,
    /** The port that last worked, or the app default when this PC has never connected. */
    val port: Int,
    /** Epoch millis of the last successful connection, or 0 when there has never been one. */
    val lastConnectedAtMillis: Long
) {
    /** The address to dial. */
    val preferredAddress: String
        get() = addresses.first()

    val hasEverConnected: Boolean
        get() = lastConnectedAtMillis > 0L
}

/** What is persisted per PC. Everything here is user-supplied or observed, never derived from a pin. */
data class KnownHostRecord(
    val nickname: String = "",
    val lastAddress: String = "",
    val lastPort: Int = 0,
    val lastConnectedAtMillis: Long = 0L
)

/**
 * Turns the paired-host map and the stored per-PC records into the Known PCs list.
 *
 * Pure and DataStore-free on purpose, the same way [DiscoveredHostList] is NSD-free: the grouping,
 * the ordering and the key parsing are where the bugs live, and all three are provable off-device.
 */
object KnownHosts {

    /** The app's default port, used for a PC that has never completed a connection. */
    const val DefaultPort = 5005

    /**
     * Preference-key scheme: `knownHost_<identity>_<field>`.
     *
     * Follows the existing `fileTrust_<deviceId>_<field>` precedent in [SettingsManager]. The
     * identity is lowercase hex and the field names carry no underscore, so the LAST underscore
     * separates them unambiguously.
     *
     * These keys are **not** in `SettingsExport.ExportableKeys`, and that whitelist is what decides:
     * a key nobody added is simply absent from an export. Right answer here — a nickname and a
     * last-connected time say which PCs someone owns and when they use them.
     */
    const val KeyPrefix = "knownHost_"

    const val NicknameField = "nickname"
    const val LastAddressField = "lastAddress"
    const val LastPortField = "lastPort"
    const val LastConnectedField = "lastConnected"

    fun nicknameKeyName(identity: String): String = "$KeyPrefix${identity}_$NicknameField"

    fun lastAddressKeyName(identity: String): String = "$KeyPrefix${identity}_$LastAddressField"

    fun lastPortKeyName(identity: String): String = "$KeyPrefix${identity}_$LastPortField"

    fun lastConnectedKeyName(identity: String): String = "$KeyPrefix${identity}_$LastConnectedField"

    /**
     * Reassembles the per-PC records from a flat preference snapshot.
     *
     * Takes the raw name-to-value map rather than a `Preferences` object so this stays a JVM
     * function with no Android on the classpath. Anything that is not a well-formed known-host key,
     * or whose value is the wrong type, is IGNORED rather than defaulted: a preference file written
     * by an older or newer build must not be able to invent a PC.
     */
    fun parseRecords(preferences: Map<String, Any?>): Map<String, KnownHostRecord> {
        val records = LinkedHashMap<String, KnownHostRecord>()

        for ((name, value) in preferences) {
            if (!name.startsWith(KeyPrefix)) continue

            val rest = name.removePrefix(KeyPrefix)
            val separator = rest.lastIndexOf('_')
            if (separator <= 0 || separator == rest.length - 1) continue

            val identity = rest.substring(0, separator)
            val field = rest.substring(separator + 1)
            val current = records[identity] ?: KnownHostRecord()

            val updated =
                when (field) {
                    NicknameField -> (value as? String)?.let { current.copy(nickname = it.trim()) }
                    LastAddressField ->
                        (value as? String)?.let { current.copy(lastAddress = it.trim()) }
                    LastPortField -> (value as? Int)?.let { current.copy(lastPort = it) }
                    LastConnectedField ->
                        (value as? Long)?.let { current.copy(lastConnectedAtMillis = it) }
                    else -> null
                }
                    ?: continue

            records[identity] = updated
        }

        return records
    }

    /**
     * Builds the list the Known PCs section renders.
     *
     * Most recently connected first, because that is the PC the user is most likely to want and it
     * is what [mostRecentlyConnected] offers as the default target. PCs that have never connected
     * keep their pairing order behind the rest — the sort is stable, so they do not shuffle between
     * recompositions the way a tie-broken-by-hash order would.
     */
    fun build(
        pairedByAddress: Map<String, String>,
        records: Map<String, KnownHostRecord>,
        defaultPort: Int = DefaultPort
    ): List<KnownHost> =
        HostIdentity.groupByIdentity(pairedByAddress)
            .map { (identity, addresses) ->
                val record = records[identity] ?: KnownHostRecord()
                KnownHost(
                    identity = identity,
                    nickname = record.nickname,
                    addresses = preferLastUsed(addresses, record.lastAddress),
                    port = record.lastPort.takeIf { it in 1..65535 } ?: defaultPort,
                    lastConnectedAtMillis = record.lastConnectedAtMillis
                )
            }
            .sortedByDescending { it.lastConnectedAtMillis }

    /**
     * The PC to offer by default.
     *
     * Null when nothing has ever connected — a paired-but-never-connected PC is not a "last used"
     * one, and pre-filling the connect form with a machine the user has never reached would look
     * like a remembered choice while being a guess.
     */
    fun mostRecentlyConnected(hosts: List<KnownHost>): KnownHost? =
        hosts.filter { it.hasEverConnected }.maxByOrNull { it.lastConnectedAtMillis }

    /**
     * Puts the address that last worked at the front.
     *
     * A PC reachable at both a LAN IP and a Tailscale name is reachable at exactly one of them from
     * where the user is standing, and the last one that worked is the better guess. A recorded
     * address that is no longer paired is dropped rather than re-added: unpairing one address of a
     * machine must not leave the row dialling it.
     */
    private fun preferLastUsed(addresses: List<String>, lastAddress: String): List<String> {
        if (lastAddress.isBlank() || lastAddress !in addresses) return addresses
        return listOf(lastAddress) + addresses.filterNot { it == lastAddress }
    }
}
