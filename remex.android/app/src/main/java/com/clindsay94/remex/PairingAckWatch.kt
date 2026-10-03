package com.clindsay94.remex

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.transformLatest

/**
 * Says when the PC this phone is connected to no longer recognises it (sweep P3, RemEx-wqo7a.7).
 *
 * **THERE IS NO "REJECTED" MESSAGE TO WAIT FOR.** The host answers a good reconnect proof with
 * `reconnect_result` (the [RemexClientManager.authenticatedConnection] edge) and says nothing at all
 * otherwise: a PC with no reconnect secret on file for this phone only logs "pairing required", and a
 * failed proof stays silent too (RemEx-0vpw5). Every pairing-gated request is then refused with a
 * `command_response` the phone never correlates, so Home said "Online" while Files and the process
 * list waited forever. The only thing the phone can observe is the ack NOT arriving.
 *
 * **WHEN THE CLOCK STARTS.** On open the host sends `host_info`, then the whole `launcher_sync`
 * (every app and its icon), and only THEN starts reading, so the kickoff ping that triggers the
 * challenge sits unread until the launcher list is out. Over a slow link (Tailscale on mobile data)
 * that list alone can take seconds. So the [GRACE_MS] grace starts when this connection's
 * `launcher_sync` ARRIVES: from there a known phone is acked within a couple of round trips
 * (challenge, proof, result). A PC that never gets the launcher list out (a send that failed host
 * side) is still judged, after [FALLBACK_MS] from its `host_info`, so an unknown phone is always
 * flagged in bounded time.
 *
 * The ack is matched by connection epoch, so the previous PC's ack can never vouch for this one.
 *
 * **ONLY A PC THAT SENDS THE ACK IS WATCHED.** A PC older than the ack (2.5 and before) never sends
 * `reconnect_result`, even to a phone it knows, so its silence means nothing. `ackingHost` is the
 * connection whose own `host_info` shows it is new enough; until that arrives for the connection that
 * is up, nothing is ever flagged.
 *
 * Plain coroutines with no Android dependency, so the timing is unit-tested on virtual time.
 */
internal object PairingAckWatch {
    /** How long after the launcher list lands a connection may wait for the host's ack. */
    const val GRACE_MS = 6_000L

    /** The limit from `host_info` when the launcher list never arrives. */
    const val FALLBACK_MS = 20_000L

    private data class Watch(val up: EstablishedConnection?, val acked: Boolean, val sendsAck: Boolean, val loopStarted: Boolean)

    /**
     * @param connected the connection that is up now, or null.
     * @param authenticated the connection the host last acked, or null.
     * @param ackingHost the connection whose `host_info` shows the PC sends the ack, or null.
     * @param launcherSynced the connection whose `launcher_sync` has arrived, or null.
     */
    @OptIn(ExperimentalCoroutinesApi::class)
    fun needsPairing(
            connected: Flow<EstablishedConnection?>,
            authenticated: Flow<EstablishedConnection?>,
            ackingHost: Flow<EstablishedConnection?>,
            launcherSynced: Flow<EstablishedConnection?>,
            graceMs: Long = GRACE_MS,
            fallbackMs: Long = FALLBACK_MS,
    ): Flow<Boolean> =
            combine(connected, authenticated, ackingHost, launcherSynced) { up, acked, acking, synced ->
                // Only the answers that matter for the connection that is up, so an unrelated
                // change (an old epoch's message) never restarts the clock.
                fun sameEpoch(other: EstablishedConnection?) = up != null && other?.epoch == up.epoch
                Watch(up, sameEpoch(acked), sameEpoch(acking), sameEpoch(synced))
            }
                    .distinctUntilChanged()
                    .transformLatest { watch ->
                        emit(false)
                        if (watch.up != null && watch.sendsAck && !watch.acked) {
                            delay(if (watch.loopStarted) graceMs else fallbackMs)
                            emit(true)
                        }
                    }
                    .distinctUntilChanged()
}
