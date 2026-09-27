package com.clindsay94.remex.ui.splash

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Signal assembly for the Live Handshake splash (RemEx-8g6n0): which PC is the target, and when
 * the lock-on, the failure and the answers land. Observes plain flows, never the client singleton.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class LiveHandshakeSignalsTest {

    private val desk = HandshakePeer("id-desk", "Desk", listOf("192.168.1.10", "desk.local"), 5005)
    private val htpc = HandshakePeer("id-htpc", "HTPC", listOf("192.168.1.20"), 5005)

    private var clock = 1_000L

    private fun signals(
        scope: TestScope,
        saved: String?,
        auth: MutableStateFlow<String?> = MutableStateFlow(null),
        connecting: MutableStateFlow<Boolean> = MutableStateFlow(false),
        connected: MutableStateFlow<Boolean> = MutableStateFlow(false),
        answers: List<PeerAnswer> = emptyList(),
    ) = LiveHandshakeSignals(
        scope = scope,
        loadPeers = { PeerSetup(listOf(desk, htpc), saved) },
        probe = { flowOf(*answers.toTypedArray()) },
        authenticatedHost = auth,
        isConnecting = connecting,
        isConnected = connected,
        nanoTime = { clock },
    )

    @Test
    fun `the saved host picks the target and answers are recorded`() {
        val scope = TestScope(StandardTestDispatcher())
        val s = signals(scope, saved = "desk.local", answers = listOf(PeerAnswer("id-htpc", 24)))
        scope.runCurrent()
        val snap = s.state.value
        assertEquals(true, snap.peersLoaded)
        assertEquals("id-desk", snap.targetId)
        assertEquals(24L, snap.answers["id-htpc"]?.rttMs)
        assertNotNull(snap.probeDoneAtNanos)
        assertNull(snap.linkedAtNanos)
        scope.cancel()
    }

    @Test
    fun `already authenticated at start is a lock at the splash origin`() {
        val scope = TestScope(StandardTestDispatcher())
        val s = signals(scope, saved = "192.168.1.10", auth = MutableStateFlow("192.168.1.10"),
            connected = MutableStateFlow(true))
        clock = 5_000L
        scope.runCurrent()
        assertEquals(1_000L, s.state.value.linkedAtNanos)
        assertEquals(1_000L, s.state.value.connectStartAtNanos)
        scope.cancel()
    }

    @Test
    fun `a connect attempt that ends unconnected is a failure, and a later lock does not erase it`() {
        val scope = TestScope(StandardTestDispatcher())
        val connecting = MutableStateFlow(false)
        val auth = MutableStateFlow<String?>(null)
        val s = signals(scope, saved = "192.168.1.10", connecting = connecting, auth = auth)
        scope.runCurrent()
        clock = 2_000L
        connecting.value = true
        scope.runCurrent()
        assertEquals(2_000L, s.state.value.connectStartAtNanos)
        clock = 3_000L
        connecting.value = false
        scope.runCurrent()
        assertEquals(3_000L, s.state.value.failedAtNanos)
        scope.cancel()
    }

    @Test
    fun `the PC that actually acks becomes the target`() {
        val scope = TestScope(StandardTestDispatcher())
        val auth = MutableStateFlow<String?>(null)
        val s = signals(scope, saved = "192.168.1.10", auth = auth)
        scope.runCurrent()
        clock = 4_000L
        auth.value = "192.168.1.20"
        scope.runCurrent()
        assertEquals("id-htpc", s.state.value.targetId)
        assertEquals(4_000L, s.state.value.linkedAtNanos)
        scope.cancel()
    }

    @Test
    fun `ready and skip keep their first time`() {
        val scope = TestScope(StandardTestDispatcher())
        val s = signals(scope, saved = null)
        scope.runCurrent()
        clock = 7_000L
        s.markReady()
        s.skip()
        clock = 9_000L
        s.markReady()
        s.skip()
        assertEquals(7_000L, s.state.value.readyAtNanos)
        assertEquals(7_000L, s.state.value.skipAtNanos)
        assertNull(s.state.value.targetId)
        scope.cancel()
    }
}
