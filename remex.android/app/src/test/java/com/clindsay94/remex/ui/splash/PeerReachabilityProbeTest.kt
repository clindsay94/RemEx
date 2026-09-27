package com.clindsay94.remex.ui.splash

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket

/**
 * The Live Handshake reachability probe (RemEx-8g6n0): a PC that is listening answers with a
 * measured connect time, a closed port stays silent, a slow name lookup is abandoned inside the
 * same budget, loopback is never dialled, and the flow completes either way so the splash learns
 * the silent ones gave up.
 */
class PeerReachabilityProbeTest {

    private val loopback: String = InetAddress.getLoopbackAddress().hostAddress!!

    /** These tests listen on loopback, which the real probe refuses to dial. */
    private fun probe(timeoutMs: Int = 1500, maxParallel: Int = 8) =
        TcpPeerReachabilityProbe(timeoutMs = timeoutMs, maxParallel = maxParallel, addressFilter = { true })

    /** A port that was free a moment ago and is closed now: connecting to it is refused. */
    private fun closedPort(): Int = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }

    @Test
    fun `a listening PC answers with its connect time`() = runBlocking {
        ServerSocket(0, 8, InetAddress.getLoopbackAddress()).use { server ->
            val answers = probe().probe(listOf(ProbeTarget("desk", loopback, server.localPort))).toList()
            assertEquals(1, answers.size)
            assertEquals("desk", answers.single().id)
            assertTrue("rtt is positive and bounded", answers.single().rttMs in 1..1500)
        }
    }

    @Test
    fun `a closed port is silent and the probe still completes`() = runBlocking {
        val answers = probe(timeoutMs = 500).probe(listOf(ProbeTarget("asleep", loopback, closedPort()))).toList()
        assertTrue(answers.isEmpty())
    }

    @Test
    fun `only the PCs that answer are reported, and unusable targets are skipped`() = runBlocking {
        ServerSocket(0, 8, InetAddress.getLoopbackAddress()).use { server ->
            val answers = probe(timeoutMs = 500, maxParallel = 2)
                .probe(
                    listOf(
                        ProbeTarget("awake", loopback, server.localPort),
                        ProbeTarget("asleep", loopback, closedPort()),
                        ProbeTarget("blank", "", 5005),
                        ProbeTarget("badport", loopback, 0),
                    )
                )
                .toList()
            assertEquals(listOf("awake"), answers.map { it.id })
        }
    }

    @Test
    fun `the real probe never dials loopback`() = runBlocking {
        assertFalse(TcpPeerReachabilityProbe.isDialable(InetAddress.getLoopbackAddress()))
        ServerSocket(0, 8, InetAddress.getLoopbackAddress()).use { server ->
            val answers = TcpPeerReachabilityProbe(timeoutMs = 500)
                .probe(listOf(ProbeTarget("self", loopback, server.localPort)))
                .toList()
            assertTrue("loopback is skipped even though it is listening", answers.isEmpty())
        }
    }

    @Test
    fun `a slow name lookup is abandoned inside the same budget`() = runBlocking {
        val slowResolver: (String) -> InetAddress = {
            Thread.sleep(3_000)
            InetAddress.getLoopbackAddress()
        }
        val start = System.nanoTime()
        val answers = TcpPeerReachabilityProbe(timeoutMs = 300, resolver = slowResolver, addressFilter = { true })
            .probe(listOf(ProbeTarget("magicdns", "desk.tail1234.ts.net", 5005)))
            .toList()
        val elapsedMs = (System.nanoTime() - start) / 1_000_000
        assertTrue(answers.isEmpty())
        assertTrue("completed in ${elapsedMs}ms, well before the 3 s lookup", elapsedMs < 2_000)
    }

    @Test
    fun `a PC is named by its nickname, then a hostname, then its address`() {
        assertEquals("Studio", HandshakePeer.displayNameFor("  Studio ", listOf("10.0.0.3")))
        assertEquals("desk-rig", HandshakePeer.displayNameFor("", listOf("10.0.0.3", "desk-rig.local")))
        assertEquals("desk-rig", HandshakePeer.displayNameFor("", listOf("desk-rig.tail1234.ts.net")))
        assertEquals("10.0.0.3", HandshakePeer.displayNameFor("", listOf("10.0.0.3", "fe80::1")))
    }
}
