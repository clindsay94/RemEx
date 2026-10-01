package com.clindsay94.remex.ui.splash

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onCompletion
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** A paired PC as the splash draws it. [id] is the stable [com.clindsay94.remex.data.KnownHost.identity]. */
data class HandshakePeer(
    val id: String,
    val name: String,
    /** Every address this PC is paired at; the first is the one the probe knocks on. */
    val addresses: List<String>,
    val port: Int,
) {
    val host: String get() = addresses.firstOrNull().orEmpty()

    fun answersTo(address: String?): Boolean =
        !address.isNullOrBlank() && addresses.any { it.equals(address.trim(), ignoreCase = true) }

    companion object {
        private val Ipv4Literal = Regex("""^\d{1,3}(\.\d{1,3}){3}$""")

        /**
         * What the splash calls a PC. The order — nickname, then the machine name the PC reported,
         * then the address — is [com.clindsay94.remex.data.KnownHosts.displayName], the same one the
         * Known PCs rows lead with (RemEx-odqj5); only the address step is splash-specific. There,
         * the best address to show is a hostname the PC is paired at (`desk-rig.local`, a MagicDNS
         * `desk-rig.tail1234.ts.net`), by its first label, and a bare IP is the last resort.
         */
        fun displayNameFor(nickname: String, addresses: List<String>, machineName: String = ""): String =
            com.clindsay94.remex.data.KnownHosts.displayName(nickname, machineName, addressLabel(addresses))

        private fun addressLabel(addresses: List<String>): String {
            val hostname = addresses.map { it.trim() }.firstOrNull { a ->
                a.isNotEmpty() && !Ipv4Literal.matches(a) && !a.contains(':') && a.any { it.isLetter() }
            }
            if (hostname != null) return hostname.substringBefore('.').ifEmpty { hostname }
            return addresses.firstOrNull { it.isNotBlank() }?.trim().orEmpty()
        }
    }
}

/** What the splash knows about the paired PCs before anything has been heard from them. */
data class PeerSetup(
    val peers: List<HandshakePeer>,
    /** The saved host (what the heartbeat auto-connect dials), as typed/stored. */
    val savedHost: String?,
)

data class TimedAnswer(val atNanos: Long, val rttMs: Long)

/**
 * Everything the Live Handshake splash observes, as raw [System.nanoTime] stamps. The UI turns
 * them into splash-relative seconds against its own clock origin (anything that happened before
 * the splash became visible counts as 0.0).
 */
data class HandshakeSnapshot(
    val peersLoaded: Boolean = false,
    val peers: List<HandshakePeer> = emptyList(),
    val targetId: String? = null,
    val answers: Map<String, TimedAnswer> = emptyMap(),
    /** When the probe finished with every PC (answered or given up). */
    val probeDoneAtNanos: Long? = null,
    /** When the connection to the target started (heartbeat connect in flight, or already up). */
    val connectStartAtNanos: Long? = null,
    /** When the host acked the reconnect challenge: the lock-on. */
    val linkedAtNanos: Long? = null,
    /** When a connect attempt ended without a connection. */
    val failedAtNanos: Long? = null,
    /** When the app underneath had composed its first frame. */
    val readyAtNanos: Long? = null,
    val skipAtNanos: Long? = null,
    /** The host of the connection the host acked, once it has. */
    val linkedHost: String? = null,
) {
    val target: HandshakePeer? get() = targetId?.let { id -> peers.firstOrNull { it.id == id } }
}

/**
 * Assembles the splash's [HandshakeSnapshot] from the app's real startup (RemEx-8g6n0).
 *
 * It only OBSERVES the connection the heartbeat auto-connect already makes
 * ([com.clindsay94.remex.RemexClientManager.initialize]); it never starts, stops or redirects one.
 * The one thing it does itself is [PeerReachabilityProbe.probe], once, to hear which paired PCs
 * are awake.
 *
 * All inputs are plain flows so the assembly is JVM-testable without the client singleton.
 */
class LiveHandshakeSignals(
    scope: CoroutineScope,
    private val loadPeers: suspend () -> PeerSetup,
    private val probe: PeerReachabilityProbe,
    /** Host of the authenticated connection, null until the host acks (and on every drop). */
    private val authenticatedHost: StateFlow<String?>,
    private val isConnecting: StateFlow<Boolean>,
    private val isConnected: StateFlow<Boolean>,
    private val nanoTime: () -> Long = System::nanoTime,
    private val maxPeers: Int = MaxPeers,
) {
    private val startNanos = nanoTime()
    private val _state = MutableStateFlow(HandshakeSnapshot())
    val state: StateFlow<HandshakeSnapshot> = _state.asStateFlow()

    init {
        // Connection observers first, so nothing that happens while the peer list loads is missed.
        scope.launch { observeLink() }
        scope.launch { observeConnectAttempts() }
        scope.launch { loadAndProbe() }
    }

    /** The app underneath has composed; the first call wins. */
    fun markReady() {
        val now = nanoTime()
        _state.update { if (it.readyAtNanos == null) it.copy(readyAtNanos = now) else it }
    }

    /** The user tapped to skip; the first call wins. */
    fun skip() {
        val now = nanoTime()
        _state.update { if (it.skipAtNanos == null) it.copy(skipAtNanos = now) else it }
    }

    private suspend fun observeLink() {
        val host = authenticatedHost.value
        val at: Long
        val linkedHost: String
        if (host != null) {
            at = startNanos
            linkedHost = host
        } else {
            linkedHost = authenticatedHost.first { it != null }!!
            at = nanoTime()
        }
        _state.update { s ->
            // The PC that actually acked is the target, whatever the saved host said.
            val acked = s.peers.firstOrNull { it.answersTo(linkedHost) }
            s.copy(
                linkedAtNanos = s.linkedAtNanos ?: at,
                connectStartAtNanos = s.connectStartAtNanos ?: at,
                targetId = acked?.id ?: s.targetId,
                linkedHost = linkedHost,
            )
        }
    }

    private suspend fun observeConnectAttempts() {
        if (isConnecting.value || isConnected.value) {
            _state.update { it.copy(connectStartAtNanos = it.connectStartAtNanos ?: startNanos) }
        }
        var previous = isConnecting.value
        isConnecting.collect { connecting ->
            val now = nanoTime()
            if (connecting) {
                _state.update { it.copy(connectStartAtNanos = it.connectStartAtNanos ?: now) }
            } else if (previous && !isConnected.value) {
                // The attempt ended and nothing is connected: the target did not answer the
                // connection. Only the first failure before a lock counts.
                _state.update {
                    if (it.linkedAtNanos == null && it.failedAtNanos == null) it.copy(failedAtNanos = now) else it
                }
            }
            previous = connecting
        }
    }

    private suspend fun loadAndProbe() {
        val setup = runCatching { loadPeers() }.getOrElse { e ->
            if (e is kotlinx.coroutines.CancellationException) throw e
            PeerSetup(emptyList(), null)
        }
        val peers = selectPeers(setup, _state.value.linkedHost)
        _state.update { s ->
            val linkedMatch = s.linkedHost?.let { h -> peers.firstOrNull { it.answersTo(h) } }
            val saved = peers.firstOrNull { it.answersTo(setup.savedHost) }
            s.copy(peersLoaded = true, peers = peers, targetId = (linkedMatch ?: saved)?.id)
        }
        if (peers.isEmpty()) {
            _state.update { it.copy(probeDoneAtNanos = nanoTime()) }
            return
        }
        probe.probe(peers.map { ProbeTarget(it.id, it.host, it.port) })
            .onCompletion { cause ->
                if (cause == null) _state.update { it.copy(probeDoneAtNanos = nanoTime()) }
            }
            .collect { answer ->
                val now = nanoTime()
                _state.update { s ->
                    if (s.answers.containsKey(answer.id)) s
                    else s.copy(answers = s.answers + (answer.id to TimedAnswer(now, answer.rttMs)))
                }
            }
    }

    /**
     * At most [maxPeers] PCs, most recently used first (the order the store already gives), with
     * the target always among them: an orbit of ten ghosts says nothing, and a target left off
     * the orbit could not lock on.
     */
    private fun selectPeers(setup: PeerSetup, linkedHost: String?): List<HandshakePeer> {
        val all = setup.peers
        if (all.size <= maxPeers) return all
        val kept = all.take(maxPeers).toMutableList()
        val target = all.firstOrNull { it.answersTo(linkedHost) } ?: all.firstOrNull { it.answersTo(setup.savedHost) }
        if (target != null && kept.none { it.id == target.id }) kept[kept.lastIndex] = target
        return kept
    }

    companion object {
        const val MaxPeers = 8
    }
}
