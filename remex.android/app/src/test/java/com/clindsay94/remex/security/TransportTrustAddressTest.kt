package com.clindsay94.remex.security

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransportTrustAddressTest {

    @Test
    fun `isTailscaleAddress accepts exactly the 100_64_0_0 slash 10 CGNAT range`() {
        assertFalse(TransportTrust.isTailscaleAddress("100.63.255.255"))
        assertTrue(TransportTrust.isTailscaleAddress("100.64.0.0"))
        assertTrue(TransportTrust.isTailscaleAddress("100.100.12.34"))
        assertTrue(TransportTrust.isTailscaleAddress("100.127.255.255"))
        assertFalse(TransportTrust.isTailscaleAddress("100.128.0.0"))
        assertFalse(TransportTrust.isTailscaleAddress("101.64.0.1"))
        assertFalse(TransportTrust.isTailscaleAddress("192.168.1.10"))
    }

    @Test
    fun `isTailscaleAddress accepts the fd7a_115c_a1e0 IPv6 prefix in any case`() {
        assertTrue(TransportTrust.isTailscaleAddress("fd7a:115c:a1e0::1"))
        assertTrue(TransportTrust.isTailscaleAddress("FD7A:115C:A1E0:AB12::53"))
        assertTrue(TransportTrust.isTailscaleAddress("  fd7a:115c:a1e0::1  "))
        assertFalse(TransportTrust.isTailscaleAddress("fd7a:115c:a1e1::1"))
        assertFalse(TransportTrust.isTailscaleAddress("fe80::1"))
    }

    @Test
    fun `isTailscaleAddress rejects garbage and blank input`() {
        assertFalse(TransportTrust.isTailscaleAddress(""))
        assertFalse(TransportTrust.isTailscaleAddress("   "))
        assertFalse(TransportTrust.isTailscaleAddress("100.64"))
        assertFalse(TransportTrust.isTailscaleAddress("100.64.0.0.1"))
        assertFalse(TransportTrust.isTailscaleAddress("abc.64.0.1"))
        assertFalse(TransportTrust.isTailscaleAddress("100.x.0.1"))
        assertFalse(TransportTrust.isTailscaleAddress("not-an-address"))
    }

    @Test
    fun `isTailscaleHostname matches only a real ts_net suffix`() {
        assertTrue(TransportTrust.isTailscaleHostname("m.tailnet.ts.net"))
        assertTrue(TransportTrust.isTailscaleHostname("M.Tailnet.TS.NET"))
        assertTrue(TransportTrust.isTailscaleHostname("m.tailnet.ts.net."))
        assertFalse(TransportTrust.isTailscaleHostname("evil.ts.net.attacker.com"))
        assertFalse(TransportTrust.isTailscaleHostname("evilts.net"))
        assertFalse(TransportTrust.isTailscaleHostname("ts.net"))
        assertFalse(TransportTrust.isTailscaleHostname("desktop-pc"))
        assertFalse(TransportTrust.isTailscaleHostname(""))
        assertFalse(TransportTrust.isTailscaleHostname("   "))
    }

    @Test
    fun `isLoopback recognises localhost, IPv6 loopback and the 127 slash 8 block`() {
        assertTrue(TransportTrust.isLoopback("localhost"))
        assertTrue(TransportTrust.isLoopback("LocalHost"))
        assertTrue(TransportTrust.isLoopback("::1"))
        assertTrue(TransportTrust.isLoopback("127.0.0.1"))
        assertTrue(TransportTrust.isLoopback("127.255.255.255"))
    }

    @Test
    fun `isLoopback rejects lookalikes and blank input`() {
        assertFalse(TransportTrust.isLoopback("1270.0.1"))
        assertFalse(TransportTrust.isLoopback("127"))
        assertFalse(TransportTrust.isLoopback("128.0.0.1"))
        assertFalse(TransportTrust.isLoopback("192.168.1.10"))
        assertFalse(TransportTrust.isLoopback(""))
        assertFalse(TransportTrust.isLoopback("   "))
    }

    @Test
    fun `requiresLocalNetworkAccess is false for loopback and Tailscale, true for LAN`() {
        assertFalse(TransportTrust.requiresLocalNetworkAccess("127.0.0.1"))
        assertFalse(TransportTrust.requiresLocalNetworkAccess("localhost"))
        assertFalse(TransportTrust.requiresLocalNetworkAccess("100.101.102.103"))
        assertFalse(TransportTrust.requiresLocalNetworkAccess("fd7a:115c:a1e0::1"))
        assertFalse(TransportTrust.requiresLocalNetworkAccess("desktop.tailnet.ts.net"))

        assertTrue(TransportTrust.requiresLocalNetworkAccess("192.168.1.10"))
        assertTrue(TransportTrust.requiresLocalNetworkAccess("10.0.0.5"))
        assertTrue(TransportTrust.requiresLocalNetworkAccess("desktop-pc.local"))
        assertTrue(TransportTrust.requiresLocalNetworkAccess("100.128.0.1"))
    }
}
