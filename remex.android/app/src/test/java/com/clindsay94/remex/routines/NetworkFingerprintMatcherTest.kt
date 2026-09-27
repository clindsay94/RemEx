package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.HomeFacts
import com.clindsay94.remex.routines.home.HomeTokenizer
import com.clindsay94.remex.routines.home.IpText
import com.clindsay94.remex.routines.home.NetworkFingerprintMatcher
import com.clindsay94.remex.routines.home.NetworkSnapshot
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Spec §13.3 `NetworkFingerprintMatcherTest`, §9 T19, R-SEC-17: primary AND secondary facts, VPN
 * excluded, the weak-fingerprint flag, and the normalization the tokens are made from.
 */
class NetworkFingerprintMatcherTest {
    private val key = ByteArray(32) { 7 }
    private val tokenizer = HomeTokenizer(key)

    private val homeWifi =
        NetworkSnapshot(
            wifiOrEthernet = true,
            vpn = false,
            gateways = listOf("192.168.1.1"),
            prefixes = listOf("192.168.1.0/24", "fe80::/64"),
            dnsServers = listOf("192.168.1.1"),
            domains = listOf("lan"),
            dhcpServer = "192.168.1.1",
            addresses = listOf("192.168.1.23", "2001:db8:1234:5:abcd::17"),
        )
    private val home = tokenizer.tokens(HomeFacts.of(homeWifi))

    private fun matches(network: NetworkSnapshot) = NetworkFingerprintMatcher.match(home, tokenizer.tokens(HomeFacts.of(network)), network.qualifies)

    @Test
    fun `the home network matches`() {
        val result = matches(homeWifi)
        assertTrue(result.matches)
        assertFalse(result.weak)
    }

    @Test
    fun `T19 - same gateway and range but different DNS, DHCP, domain and prefix48 is not home`() {
        val cafe =
            homeWifi.copy(
                dnsServers = listOf("8.8.8.8"),
                domains = listOf("cafe.example"),
                dhcpServer = "192.168.1.254",
                addresses = listOf("192.168.1.80", "2001:db8:9999:5::17"),
            )
        assertFalse(matches(cafe).matches)
        // And with no secondary facts at all on the foreign network.
        assertFalse(matches(cafe.copy(dnsServers = emptyList(), domains = emptyList(), dhcpServer = null, addresses = emptyList())).matches)
    }

    @Test
    fun `one secondary fact in common is enough`() {
        assertTrue(matches(homeWifi.copy(dnsServers = listOf("1.1.1.1"), dhcpServer = null, addresses = emptyList())).matches)
        assertTrue(matches(homeWifi.copy(dnsServers = emptyList(), domains = emptyList(), dhcpServer = null)).matches)
    }

    @Test
    fun `a different gateway or range is not home`() {
        assertFalse(matches(homeWifi.copy(gateways = listOf("192.168.1.254"))).matches)
        assertFalse(matches(homeWifi.copy(prefixes = listOf("192.168.2.0/24"))).matches)
    }

    @Test
    fun `a VPN or a mobile network never matches`() {
        assertFalse(matches(homeWifi.copy(vpn = true)).matches)
        assertFalse(matches(homeWifi.copy(wifiOrEthernet = false)).matches)
    }

    @Test
    fun `a home with no secondary facts matches on the primary pair and is flagged weak`() {
        val bare = homeWifi.copy(dnsServers = emptyList(), domains = emptyList(), dhcpServer = null, addresses = listOf("192.168.1.23"))
        val weakHome = tokenizer.tokens(HomeFacts.of(bare))
        val result = NetworkFingerprintMatcher.match(weakHome, tokenizer.tokens(HomeFacts.of(homeWifi.copy(dnsServers = listOf("9.9.9.9")))), true)
        assertTrue(result.matches)
        assertTrue(result.weak)
        assertFalse(HomeFacts.of(bare).hasSecondary)
    }

    @Test
    fun `a DNS or DHCP server that is the gateway is weak, not a secondary signal`() {
        // Most routers: gateway = DNS = DHCP. A foreign network on the same default address agrees on
        // all three for free, so none of them may stand in for a real secondary fact.
        val routerOnly = homeWifi.copy(dnsServers = listOf("192.168.1.1"), domains = emptyList(), dhcpServer = "192.168.1.1", addresses = listOf("192.168.1.23"))
        val facts = HomeFacts.of(routerOnly)
        assertFalse(facts.hasSecondary)
        val weakHome = tokenizer.tokens(facts)
        assertTrue(weakHome.secondary.isEmpty())
        assertTrue(NetworkFingerprintMatcher.match(weakHome, tokenizer.tokens(facts), true).weak)
        // A distinct DNS server still counts.
        assertTrue(HomeFacts.of(routerOnly.copy(dnsServers = listOf("192.168.1.1", "192.168.1.2"))).hasSecondary)
    }

    @Test
    fun `any of several networks can be home`() {
        val mobile = NetworkSnapshot(wifiOrEthernet = false, vpn = false, gateways = listOf("10.0.0.1"), prefixes = listOf("10.0.0.0/8"))
        assertTrue(NetworkFingerprintMatcher.anyMatches(home, listOf(mobile, homeWifi), tokenizer))
        assertFalse(NetworkFingerprintMatcher.anyMatches(home, listOf(mobile), tokenizer))
        assertFalse(NetworkFingerprintMatcher.anyMatches(home, emptyList(), tokenizer))
    }

    @Test
    fun `tokens depend on the key, so a token alone reveals nothing`() {
        val other = HomeTokenizer(ByteArray(32) { 8 }).tokens(HomeFacts.of(homeWifi))
        assertNotEquals(home.gateways, other.gateways)
        assertFalse(home.gateways.any { it.contains("192") })
    }

    @Test
    fun `facts are normalized before they are tokenized`() {
        val facts =
            HomeFacts.of(
                homeWifi.copy(
                    gateways = listOf("fe80::1%wlan0", "192.168.001.001".replace("001", "1")),
                    prefixes = listOf("192.168.1.77/24", "2001:DB8:1234:5:0:0:0:0/64", "169.254.0.0/16"),
                    dnsServers = listOf("/192.168.1.1", "2001:0db8:0000:0000:0000:0000:0000:0001"),
                    domains = listOf("Home.LAN."),
                    dhcpServer = "0.0.0.0",
                ),
            )
        assertEquals(listOf("fe80::1", "192.168.1.1"), facts.gateways)
        assertEquals(listOf("192.168.1.0/24", "2001:db8:1234:5::/64"), facts.prefixes)
        assertEquals(listOf("192.168.1.1", "2001:db8::1"), facts.dnsServers)
        assertEquals("home.lan", facts.domain)
        assertNull(facts.dhcpServer)
        assertEquals("2001:db8:1234::/48", facts.ipv6Prefix48)
    }

    @Test
    fun `IP helpers - RFC 5952, prefixes, private and shared space`() {
        assertEquals("2001:db8::1:0:0:1", IpText.normalize("2001:db8:0:0:1:0:0:1"))
        assertEquals("::1", IpText.normalize("0:0:0:0:0:0:0:1"))
        assertNull(IpText.normalize("host.example"))
        assertNull(IpText.normalize("1234"))
        assertNull(IpText.normalize("300.1.1.1"))
        assertTrue(IpText.inPrefix(IpText.parse("192.168.1.9")!!, "192.168.1.0/24"))
        assertFalse(IpText.inPrefix(IpText.parse("192.168.2.9")!!, "192.168.1.0/24"))
        assertTrue(IpText.isPrivateLan(IpText.parse("10.1.2.3")!!))
        assertTrue(IpText.isPrivateLan(IpText.parse("172.20.0.5")!!))
        assertTrue(IpText.isPrivateLan(IpText.parse("fd12::5")!!))
        assertFalse(IpText.isPrivateLan(IpText.parse("100.101.102.103")!!))
        assertTrue(IpText.isSharedAddressSpace(IpText.parse("100.101.102.103")!!))
        assertNull(IpText.prefix48("fd12:3456:789a::1"))
    }
}
