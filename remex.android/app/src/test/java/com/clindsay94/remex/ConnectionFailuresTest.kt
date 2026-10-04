package com.clindsay94.remex

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The connection error card shows a localized sentence, not the native layer's .NET exception text
 * (3.0 comb, raw-errors; RemEx-pp4cm.3).
 *
 * THE CERTIFICATE CASE IS THE ONE THAT MUST SURVIVE. The Connection screen decides to show its
 * "security identity changed" message and the repair button from the error TEXT. If classification
 * stopped recognising it, or [ConnectionFailures.messageRes] replaced its text, a pinned-pairing
 * mismatch would turn into "Could not connect" with no way to repair it.
 */
class ConnectionFailuresTest {

    @Test
    fun `the three words the connection screen always used still mean a certificate problem`() {
        assertEquals(ConnectionFailureKind.CertificateProblem, ConnectionFailures.classify("SPKI pin mismatch"))
        assertEquals(
            ConnectionFailureKind.CertificateProblem,
            ConnectionFailures.classify("The remote certificate is invalid"))
        assertEquals(
            ConnectionFailureKind.CertificateProblem,
            ConnectionFailures.classify("The SSL connection could not be established"))
    }

    @Test
    fun `a timeout on a vpn address gets vpn advice instead of same-network advice`() {
        // The usual line tells someone on Tailscale to put both devices on one network, which is the
        // opposite of why they use it. RemEx-pp4cm.3.
        for (host in listOf("100.64.0.1", "100.101.102.103", "100.127.255.254", "pc.tail1234.ts.net", "fd7a:115c:a1e0::1")) {
            assertEquals(
                host,
                R.string.connection_error_timeout_vpn,
                ConnectionFailures.messageRes(ConnectionFailureKind.TimedOut, host))
        }
    }

    @Test
    fun `a timeout on a normal address or with no known host keeps the same-network advice`() {
        for (host in listOf("192.168.1.20", "100.128.0.1", "100.63.255.255", "mypc.local", "evil.ts.net.attacker.com", null)) {
            assertEquals(
                host,
                R.string.pairing_error_timeout,
                ConnectionFailures.messageRes(ConnectionFailureKind.TimedOut, host))
        }
    }

    @Test
    fun `a certificate problem keeps its text for the screen to recognise`() {
        assertNull(ConnectionFailures.messageRes(ConnectionFailureKind.CertificateProblem))
        assertTrue(ConnectionFailures.isCertificateProblem("spki mismatch"))
        assertFalse(ConnectionFailures.isCertificateProblem("Connection refused"))
    }

    @Test
    fun `certificate wins over a timeout or unreachable word in the same message`() {
        // An SSL handshake that timed out is still a trust question first; the repair button matters more.
        assertEquals(
            ConnectionFailureKind.CertificateProblem,
            ConnectionFailures.classify("SSL handshake timed out, connection refused"))
    }

    @Test
    fun `timeouts are recognised`() {
        assertEquals(ConnectionFailureKind.TimedOut, ConnectionFailures.classify("The operation timed out"))
        assertEquals(ConnectionFailureKind.TimedOut, ConnectionFailures.classify("Connect timeout"))
    }

    @Test
    fun `unreachable addresses are recognised`() {
        listOf(
                "No such host is known",
                "Connection refused",
                "Network is unreachable",
                "No route to host",
                "Name or service not known",
            )
            .forEach { assertEquals(it, ConnectionFailureKind.Unreachable, ConnectionFailures.classify(it)) }
    }

    @Test
    fun `anything else is unknown and blank is unknown`() {
        assertEquals(ConnectionFailureKind.Unknown, ConnectionFailures.classify("Something odd happened"))
        assertEquals(ConnectionFailureKind.Unknown, ConnectionFailures.classify(""))
    }

    @Test
    fun `every non-certificate kind has its own localized message`() {
        val ids =
            listOf(ConnectionFailureKind.TimedOut, ConnectionFailureKind.Unreachable, ConnectionFailureKind.Unknown)
                .map { ConnectionFailures.messageRes(it) }
        ids.forEach { assertNotNull(it) }
        assertEquals("each kind says something different", ids.size, ids.toSet().size)
    }
}
