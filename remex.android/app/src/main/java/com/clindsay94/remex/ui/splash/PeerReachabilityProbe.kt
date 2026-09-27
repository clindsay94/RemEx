package com.clindsay94.remex.ui.splash

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.asExecutor
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.future.await
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.CompletableFuture

/** One paired PC to knock on: its stable id and the last address:port it answered at. */
data class ProbeTarget(val id: String, val host: String, val port: Int)

/** A PC that answered, and how long its TCP connect took. */
data class PeerAnswer(val id: String, val rttMs: Long)

/**
 * "Which of my PCs are awake right now, and how far away are they?" for the Live Handshake
 * splash (RemEx-8g6n0).
 *
 * The returned flow emits one [PeerAnswer] per PC that answered, in the order they answered, and
 * completes once every target has either answered or given up. PCs that never answer emit
 * nothing; completion is how a caller learns they are silent.
 *
 * Deliberately NOT discovery: it never touches NSD or `ConnectionViewModel.discoveryJob` (the
 * single in-flight discovery guard in docs/REGRESSION-GUARDS.md), and it never connects to
 * anything but a PC the phone is already paired with — phone to PC, as every connection in this
 * app is.
 */
fun interface PeerReachabilityProbe {
    fun probe(targets: List<ProbeTarget>): Flow<PeerAnswer>
}

/**
 * The real probe: one plain TCP connect per PC, timed, then closed immediately. At most
 * [maxParallel] PCs in flight, each bounded by ONE [timeoutMs] budget that covers the name lookup
 * and the connect together, all on [dispatcher].
 *
 * The lookup is awaited, not blocked on: `InetAddress` resolution is not interruptible, so a slow
 * MagicDNS or `.local` name is abandoned at the deadline (its lookup thread finishes on its own)
 * rather than holding back the probe's completion — which is how the splash learns a PC is not
 * answering.
 *
 * [addressFilter] decides which resolved addresses may be dialled at all; by default loopback and
 * wildcard addresses are skipped (the client link is always phone to PC, never loopback). Tests
 * that listen on loopback pass their own.
 */
class TcpPeerReachabilityProbe(
    private val timeoutMs: Int = DefaultTimeoutMs,
    private val maxParallel: Int = DefaultMaxParallel,
    private val dispatcher: CoroutineDispatcher = Dispatchers.IO,
    private val nanoTime: () -> Long = System::nanoTime,
    private val resolver: (String) -> InetAddress = InetAddress::getByName,
    private val addressFilter: (InetAddress) -> Boolean = ::isDialable,
) : PeerReachabilityProbe {

    override fun probe(targets: List<ProbeTarget>): Flow<PeerAnswer> = channelFlow {
        val permits = Semaphore(maxParallel.coerceAtLeast(1))
        for (target in targets) {
            launch {
                permits.withPermit {
                    val rtt = withTimeoutOrNull(timeoutMs.toLong()) { connectMillis(target) }
                    if (rtt != null) send(PeerAnswer(target.id, rtt))
                }
            }
        }
    }.flowOn(dispatcher)

    /** The connect time in ms, or null when the PC did not answer (or cannot be dialled). */
    private suspend fun connectMillis(target: ProbeTarget): Long? {
        if (target.host.isBlank() || target.port !in 1..65535) return null
        val deadline = nanoTime() + timeoutMs * 1_000_000L
        val address = resolve(target.host.trim()) ?: return null
        if (!addressFilter(address)) return null
        val remainingMs = ((deadline - nanoTime()) / 1_000_000L).toInt()
        if (remainingMs <= 0) return null
        return try {
            Socket().use { socket ->
                val start = nanoTime()
                socket.connect(InetSocketAddress(address, target.port), remainingMs)
                ((nanoTime() - start) / 1_000_000L).coerceAtLeast(1L)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: IOException) {
            null
        } catch (_: SecurityException) {
            null
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    /** Name lookup that the caller's timeout can abandon; null when the name does not resolve. */
    private suspend fun resolve(host: String): InetAddress? = try {
        CompletableFuture.supplyAsync({ resolver(host) }, dispatcher.asExecutor()).await()
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        // UnknownHostException, SecurityException, or either wrapped by the future.
        null
    }

    companion object {
        const val DefaultTimeoutMs = 1500
        const val DefaultMaxParallel = 8

        /** Only real remote addresses: never loopback or the wildcard address. */
        fun isDialable(address: InetAddress): Boolean =
            !address.isLoopbackAddress && !address.isAnyLocalAddress
    }
}
