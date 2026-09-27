package com.clindsay94.remex.routines.home

import java.net.InetAddress
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

// Home presence without location (routines spec §8.3.1, D9, R-SYS-14, R-SEC-17; RemEx-pp0rt.8).
// Pure JVM: everything here is proven off-device. SSID and BSSID are never read (they need location);
// home is recognised from the network's own addresses, which only need ACCESS_NETWORK_STATE.

/** IP literals: parsing, RFC 5952 formatting, prefixes and address classes. Never resolves a name. */
object IpText {
    private val v4 = Regex("^\\d{1,3}(\\.\\d{1,3}){3}$")
    private val v6 = Regex("^[0-9a-fA-F:.]+$")

    /** The address bytes of an IPv4 or IPv6 literal (brackets and `%scope` dropped), or null. */
    fun parse(text: String?): ByteArray? {
        val raw = text?.trim()?.removePrefix("/")?.removePrefix("[")?.removeSuffix("]")?.substringBefore('%') ?: return null
        return when {
            v4.matches(raw) -> raw.split('.').map { it.toInt() }.takeIf { parts -> parts.all { it in 0..255 } }?.map { it.toByte() }?.toByteArray()
            raw.contains(':') && v6.matches(raw) -> runCatching { InetAddress.getByName(raw).address }.getOrNull()?.takeIf { it.size == 16 }
            else -> null
        }
    }

    /** IPv4 dotted quad, IPv6 compressed lower case (RFC 5952). */
    fun format(bytes: ByteArray): String =
        if (bytes.size == 4) {
            bytes.joinToString(".") { (it.toInt() and 0xff).toString() }
        } else {
            val groups = IntArray(8) { ((bytes[it * 2].toInt() and 0xff) shl 8) or (bytes[it * 2 + 1].toInt() and 0xff) }
            var bestStart = -1
            var bestLength = 0
            var i = 0
            while (i < 8) {
                if (groups[i] == 0) {
                    var j = i
                    while (j < 8 && groups[j] == 0) j++
                    if (j - i > bestLength && j - i >= 2) {
                        bestStart = i
                        bestLength = j - i
                    }
                    i = j
                } else {
                    i++
                }
            }
            if (bestStart < 0) {
                groups.joinToString(":") { Integer.toHexString(it) }
            } else {
                val head = groups.take(bestStart).joinToString(":") { Integer.toHexString(it) }
                val tail = groups.drop(bestStart + bestLength).joinToString(":") { Integer.toHexString(it) }
                "$head::$tail"
            }
        }

    fun normalize(text: String?): String? = parse(text)?.let(::format)

    /** `address/length` with the host bits cleared, or null when it is not a prefix. */
    fun normalizePrefix(text: String?): String? {
        val parts = text?.trim()?.split('/') ?: return null
        if (parts.size != 2) return null
        val bytes = parse(parts[0]) ?: return null
        val length = parts[1].toIntOrNull()?.takeIf { it in 0..bytes.size * 8 } ?: return null
        return format(mask(bytes, length)) + "/" + length
    }

    fun inPrefix(address: ByteArray, prefix: String): Boolean {
        val parts = prefix.split('/')
        val base = parse(parts.getOrNull(0)) ?: return false
        val length = parts.getOrNull(1)?.toIntOrNull() ?: return false
        if (base.size != address.size || length !in 0..address.size * 8) return false
        return mask(address, length).contentEquals(mask(base, length))
    }

    /** RFC 1918, IPv4 link-local, IPv6 unique local (fc00::/7) and link-local (fe80::/10). */
    fun isPrivateLan(address: ByteArray): Boolean {
        val a = address.map { it.toInt() and 0xff }
        return if (address.size == 4) {
            a[0] == 10 || (a[0] == 172 && a[1] in 16..31) || (a[0] == 192 && a[1] == 168) || (a[0] == 169 && a[1] == 254)
        } else {
            (a[0] and 0xfe) == 0xfc || (a[0] == 0xfe && (a[1] and 0xc0) == 0x80)
        }
    }

    /** Carrier-grade NAT space, which Tailscale uses: 100.64.0.0/10. Never "home" (spec §8.3.1). */
    fun isSharedAddressSpace(address: ByteArray): Boolean =
        address.size == 4 && (address[0].toInt() and 0xff) == 100 && ((address[1].toInt() and 0xff) and 0xc0) == 64

    /** A global IPv6 address truncated to its /48 (`2001:db8:1::/48`), or null for anything else. */
    fun prefix48(text: String?): String? {
        val bytes = parse(text)?.takeIf { it.size == 16 } ?: return null
        if ((bytes[0].toInt() and 0xe0) != 0x20) return null
        return format(mask(bytes, 48)) + "/48"
    }

    private fun mask(bytes: ByteArray, length: Int): ByteArray =
        ByteArray(bytes.size) { i ->
            val bits = (length - i * 8).coerceIn(0, 8)
            (bytes[i].toInt() and (0xff shl (8 - bits))).toByte()
        }
}

/**
 * What the phone can see of one network (spec §8.3.1), from `LinkProperties` and
 * `NetworkCapabilities`. Values are raw; [HomeFacts.of] normalizes them.
 */
data class NetworkSnapshot(
    val wifiOrEthernet: Boolean,
    val vpn: Boolean,
    val gateways: List<String> = emptyList(),
    val prefixes: List<String> = emptyList(),
    val dnsServers: List<String> = emptyList(),
    val domains: List<String> = emptyList(),
    val dhcpServer: String? = null,
    val addresses: List<String> = emptyList(),
) {
    /** Wi-Fi or Ethernet and not a VPN: the only networks that can be home. */
    val qualifies: Boolean get() = wifiOrEthernet && !vpn
}

/** The displayable facts of a home (spec §6.6), normalized. Shown to the user; never exported, backed up or logged. */
data class HomeFacts(
    val gateways: List<String>,
    val prefixes: List<String>,
    val dnsServers: List<String> = emptyList(),
    val domain: String? = null,
    val dhcpServer: String? = null,
    val ipv6Prefix48: String? = null,
) {
    /** DNS, domain, DHCP server or IPv6 /48: what tells two networks with the same router apart. */
    val hasSecondary: Boolean get() = dnsServers.isNotEmpty() || domain != null || dhcpServer != null || ipv6Prefix48 != null

    companion object {
        const val MAX_GATEWAYS = 4
        const val MAX_PREFIXES = 8
        const val MAX_DNS = 4

        fun of(snapshot: NetworkSnapshot): HomeFacts =
            HomeFacts(
                gateways = snapshot.gateways.mapNotNull(IpText::normalize).distinct().take(MAX_GATEWAYS),
                // Link-local prefixes are on every network; they identify nothing.
                prefixes =
                    snapshot.prefixes.mapNotNull(IpText::normalizePrefix).distinct()
                        .filterNot { it.startsWith("fe80::/") || it.startsWith("169.254.") }
                        .take(MAX_PREFIXES),
                dnsServers = snapshot.dnsServers.mapNotNull(IpText::normalize).distinct().take(MAX_DNS),
                domain = snapshot.domains.flatMap { it.split(' ', ',') }.map { it.trim().trimEnd('.').lowercase() }.firstOrNull { it.isNotEmpty() },
                dhcpServer = IpText.normalize(snapshot.dhcpServer)?.takeIf { it != "0.0.0.0" },
                ipv6Prefix48 = snapshot.addresses.firstNotNullOfOrNull(IpText::prefix48),
            )
    }
}

/**
 * One-way tokens of a network's facts: HMAC-SHA256 under the phone's home key (in the encrypted
 * secrets store), truncated to 128 bits. Matching compares tokens, so the check never needs the
 * stored facts in the clear and a token alone reveals nothing about the network (D9).
 */
data class HomeTokens(val gateways: Set<String>, val prefixes: Set<String>, val secondary: Set<String>)

class HomeTokenizer(key: ByteArray) {
    private val keySpec = SecretKeySpec(key, "HmacSHA256")

    fun tokens(facts: HomeFacts): HomeTokens =
        HomeTokens(
            gateways = facts.gateways.map { token("gw", it) }.toSet(),
            prefixes = facts.prefixes.map { token("prefix", it) }.toSet(),
            secondary =
                buildSet {
                    facts.dnsServers.forEach { add(token("dns", it)) }
                    facts.domain?.let { add(token("domain", it)) }
                    facts.dhcpServer?.let { add(token("dhcp", it)) }
                    facts.ipv6Prefix48?.let { add(token("v6", it)) }
                },
        )

    private fun token(kind: String, value: String): String {
        val mac = Mac.getInstance("HmacSHA256").apply { init(keySpec) }
        val digest = mac.doFinal("remex-home/v1:$kind:$value".toByteArray(Charsets.UTF_8))
        return Base64.getUrlEncoder().withoutPadding().encodeToString(digest.copyOf(16))
    }
}

/** A match verdict; [weak] when the home has no secondary fact, so router and range alone decided. */
data class FingerprintMatch(val matches: Boolean, val weak: Boolean)

/**
 * The match rule (spec §8.3.1, T19): a Wi-Fi or Ethernet network that is not a VPN, whose default
 * gateway AND an on-link prefix are the home's, AND, when the home has any secondary fact, at least
 * one of those too. A different network that happens to use the same router address (192.168.1.1 is
 * everywhere) therefore does not count as home unless its DNS, domain, DHCP server or IPv6 /48 also
 * agree.
 */
object NetworkFingerprintMatcher {
    fun match(home: HomeTokens, network: HomeTokens, qualifies: Boolean): FingerprintMatch {
        val weak = home.secondary.isEmpty()
        if (!qualifies) return FingerprintMatch(false, weak)
        val primary = home.gateways.any { it in network.gateways } && home.prefixes.any { it in network.prefixes }
        val secondary = weak || home.secondary.any { it in network.secondary }
        return FingerprintMatch(primary && secondary, weak)
    }

    /** Whether any of [networks] is home. */
    fun anyMatches(home: HomeTokens, networks: List<NetworkSnapshot>, tokenizer: HomeTokenizer): Boolean =
        networks.any { match(home, tokenizer.tokens(HomeFacts.of(it)), it.qualifies).matches }
}

/** Why home cannot be captured right now (spec §8.3.1 `Refusal`, §1.4). */
enum class HomeCaptureRefusal(val code: String) {
    NOT_ON_WIFI("not_on_wifi"),
    VPN_ACTIVE("vpn_active"),
    PC_NOT_REACHABLE_ON_LAN("pc_not_reachable_on_lan"),
}

/** The capture API's answer (spec §8.3.1, UX need 10): what the capture sheet renders before the user confirms. */
data class HomeCaptureResult(
    val ready: Boolean,
    val refusal: HomeCaptureRefusal?,
    val facts: HomeFacts?,
    val pcReachable: Boolean,
)

/**
 * Whether the network the phone is using now can be captured as home (spec §8.3.1 "Capture"): the
 * phone must be authenticated to the PC over Wi-Fi or Ethernet, with no VPN, and the PC's address
 * must be a private LAN address inside one of this network's own prefixes. Reaching the PC over a
 * tunnel (Tailscale's 100.64.0.0/10, or any VPN) proves nothing about home and is refused.
 */
object HomeCapture {
    fun evaluate(active: NetworkSnapshot?, pcAddress: String?, authenticated: Boolean): HomeCaptureResult {
        if (active == null) return HomeCaptureResult(false, HomeCaptureRefusal.NOT_ON_WIFI, null, false)
        if (active.vpn) return HomeCaptureResult(false, HomeCaptureRefusal.VPN_ACTIVE, null, false)
        if (!active.wifiOrEthernet) return HomeCaptureResult(false, HomeCaptureRefusal.NOT_ON_WIFI, null, false)
        val facts = HomeFacts.of(active)
        if (facts.gateways.isEmpty() || facts.prefixes.isEmpty()) return HomeCaptureResult(false, HomeCaptureRefusal.NOT_ON_WIFI, facts, false)
        val pc = if (authenticated) IpText.parse(pcAddress) else null
        val reachable =
            pc != null && !IpText.isSharedAddressSpace(pc) && IpText.isPrivateLan(pc) && facts.prefixes.any { IpText.inPrefix(pc, it) }
        return if (reachable) {
            HomeCaptureResult(true, null, facts, true)
        } else {
            HomeCaptureResult(false, HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN, facts, false)
        }
    }

    /**
     * Drift (spec §8.3.1 "Drift detection"): authenticated to the home's own PC over a network that
     * would qualify as a home capture, and yet it does not match the stored home.
     */
    fun isDrift(active: NetworkSnapshot?, pcAddress: String?, matchesHome: Boolean): Boolean =
        !matchesHome && evaluate(active, pcAddress, authenticated = true).ready
}
