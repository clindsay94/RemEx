package com.clindsay94.remex.security

import java.security.MessageDigest
import java.util.Locale

/**
 * A stable key for "this particular PC", independent of how it is currently reachable (RemEx-9x5i).
 *
 * **THE PROBLEM THIS SOLVES.** `PinnedHostStore.listPaired` is keyed by ADDRESS, and one PC has
 * several: a LAN IP, a Tailscale address, a hostname, plus whatever DHCP hands it next week.
 * `recordAliases` already groups those, and its own comment says the same PC gets re-paired at new
 * addresses over time. So anything keyed by address — a nickname, a last-connected timestamp,
 * per-host preferences — shows the same machine two or three times, each with its own half of the
 * user's settings.
 *
 * **WHY NOT KEY ON THE PIN DIRECTLY.** The SPKI pin genuinely identifies the machine, and it is
 * public certificate material rather than a credential — the reconnect secret is the secret. But it
 * still says *which* PC a user has paired with, and it would end up as a literal map key in
 * preference stores that are exported, backed up and read by anyone the user shares a settings file
 * with (see `SettingsExport`, which excludes anything pin-shaped by rule). A derived opaque key
 * gives the same grouping with none of that.
 *
 * The derivation is one-way and carries no address, so a stored key reveals neither the certificate
 * nor where the machine lives.
 */
object HostIdentity {

    /**
     * Characters of hex kept from the digest.
     *
     * 16 hex characters is 64 bits. For a set of paired PCs — realistically under ten, absurdly
     * under a thousand — collision probability is negligible, and a short key keeps preference
     * names readable when someone is looking at a settings export by hand.
     */
    private const val KeyLength = 16

    /**
     * Derives the stable identity for a host from its pinned SPKI hash.
     *
     * @return a lowercase hex key, or null when the pin is missing or blank — a host with no pin
     *   has not completed pairing, and inventing an identity for it would create a Known-PCs row
     *   that can never be connected to.
     */
    fun keyFor(spkiPin: String?): String? {
        val normalized = normalizePin(spkiPin) ?: return null

        val digest = MessageDigest.getInstance("SHA-256")
            .digest(normalized.toByteArray(Charsets.UTF_8))

        return digest.take(KeyLength / 2)
            .joinToString("") { "%02x".format(it) }
    }

    /**
     * Strips the decoration a pin may carry so two spellings of one pin do not become two PCs.
     *
     * `sha256/` prefixing is used elsewhere in the app (`FileTransferChannelClient` normalizes the
     * same way), and case differences in base64 would be a different pin entirely — so case is
     * NOT folded here. Only whitespace and the scheme prefix are removed.
     */
    private fun normalizePin(spkiPin: String?): String? {
        val trimmed = spkiPin?.trim()?.removePrefix("sha256/")?.trim()
        return if (trimmed.isNullOrEmpty()) null else trimmed
    }

    /**
     * Collapses an address-keyed paired-host map into one entry per physical PC.
     *
     * @param pairedByAddress what `PinnedHostStore.listPaired` returns: address to SPKI pin.
     * @return identity key to the addresses that reach that machine, preserving the order the
     *   addresses arrived in so a caller can offer the most recently recorded one first.
     */
    fun groupByIdentity(pairedByAddress: Map<String, String>): Map<String, List<String>> {
        val grouped = LinkedHashMap<String, MutableList<String>>()

        for ((address, pin) in pairedByAddress) {
            // A host whose pin will not derive is SKIPPED, not given a placeholder identity. A
            // placeholder would merge every unidentifiable host into one Known-PCs row, which is
            // worse than omitting them: the user would see a single entry claiming to be several
            // machines.
            val key = keyFor(pin) ?: continue
            grouped.getOrPut(key) { mutableListOf() }.add(address)
        }

        return grouped
    }

    /** True when the two pins denote the same machine. */
    fun isSameHost(pinA: String?, pinB: String?): Boolean {
        val a = keyFor(pinA) ?: return false
        val b = keyFor(pinB) ?: return false
        return a == b
    }

    private fun String.format(vararg args: Any): String = String.format(Locale.ROOT, this, *args)
}
