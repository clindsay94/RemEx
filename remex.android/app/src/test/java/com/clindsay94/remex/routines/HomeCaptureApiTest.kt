package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.HomeCapture
import com.clindsay94.remex.routines.home.HomeCaptureRefusal
import com.clindsay94.remex.routines.home.NetworkSnapshot
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Spec §13.3 `HomeCaptureApiTest`, R-SYS-39, R-SEC-17: the capture API's facts, `pcReachable` and refusals. */
class HomeCaptureApiTest {
    private val wifi =
        NetworkSnapshot(
            wifiOrEthernet = true,
            vpn = false,
            gateways = listOf("192.168.1.1"),
            prefixes = listOf("192.168.1.0/24"),
            dnsServers = listOf("192.168.1.1"),
            domains = listOf("lan"),
        )

    @Test
    fun `on home Wi-Fi with the PC on the LAN it is ready, with the facts to show`() {
        val result = HomeCapture.evaluate(wifi, "192.168.1.40", authenticated = true)
        assertTrue(result.ready)
        assertNull(result.refusal)
        assertTrue(result.pcReachable)
        assertEquals(listOf("192.168.1.1"), result.facts?.gateways)
        assertEquals(listOf("192.168.1.0/24"), result.facts?.prefixes)
        assertEquals("lan", result.facts?.domain)
    }

    @Test
    fun `not on Wi-Fi is refused`() {
        assertEquals(HomeCaptureRefusal.NOT_ON_WIFI, HomeCapture.evaluate(null, "192.168.1.40", true).refusal)
        val mobile = wifi.copy(wifiOrEthernet = false)
        assertEquals(HomeCaptureRefusal.NOT_ON_WIFI, HomeCapture.evaluate(mobile, "192.168.1.40", true).refusal)
        assertEquals(HomeCaptureRefusal.NOT_ON_WIFI, HomeCapture.evaluate(wifi.copy(gateways = emptyList()), "192.168.1.40", true).refusal)
    }

    @Test
    fun `a VPN is refused`() {
        val result = HomeCapture.evaluate(wifi.copy(vpn = true), "192.168.1.40", true)
        assertEquals(HomeCaptureRefusal.VPN_ACTIVE, result.refusal)
        assertFalse(result.ready)
    }

    @Test
    fun `a PC reached over Tailscale does not prove home`() {
        val result = HomeCapture.evaluate(wifi, "100.101.102.103", true)
        assertEquals(HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN, result.refusal)
        assertFalse(result.pcReachable)
        // The network's own facts are still there for the sheet to show.
        assertNotNull(result.facts)
    }

    @Test
    fun `a PC outside this network's range, public, or not connected is refused`() {
        assertEquals(HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN, HomeCapture.evaluate(wifi, "192.168.7.40", true).refusal)
        assertEquals(HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN, HomeCapture.evaluate(wifi.copy(prefixes = listOf("8.8.8.0/24")), "8.8.8.8", true).refusal)
        assertEquals(HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN, HomeCapture.evaluate(wifi, "192.168.1.40", authenticated = false).refusal)
        assertEquals(HomeCaptureRefusal.PC_NOT_REACHABLE_ON_LAN, HomeCapture.evaluate(wifi, null, true).refusal)
    }

    @Test
    fun `refusal codes are the spec's`() {
        assertEquals(listOf("not_on_wifi", "vpn_active", "pc_not_reachable_on_lan"), HomeCaptureRefusal.entries.map { it.code })
    }

    @Test
    fun `drift is a capturable LAN that does not match the stored home`() {
        assertTrue(HomeCapture.isDrift(wifi, "192.168.1.40", matchesHome = false))
        assertFalse(HomeCapture.isDrift(wifi, "192.168.1.40", matchesHome = true))
        // Away over Tailscale is not drift: it is simply away.
        assertFalse(HomeCapture.isDrift(wifi, "100.101.102.103", matchesHome = false))
        assertFalse(HomeCapture.isDrift(wifi.copy(vpn = true), "192.168.1.40", matchesHome = false))
    }
}
