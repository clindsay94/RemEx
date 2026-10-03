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
 * So: a connection that has been up for [GRACE_MS] without its ack needs pairing again. A healthy
 * reconnect is acked about a second after the socket opens (kickoff ping, challenge, proof, result),
 * so the grace is generous and a slow network does not flash the warning. The ack is matched by
 * connection epoch, so the previous PC's ack can never vouch for this one.
 *
 * **ONLY A PC THAT SENDS THE ACK IS WATCHED.** A PC older than the ack (2.5 and before) never sends
 * `reconnect_result`, even to a phone it knows, so its silence means nothing. `ackingHost` is the
 * connection whose own `host_info` shows it is new enough; until that arrives for the connection that
 * is up, nothing is ever flagged.
 *
 * Plain coroutines with no Android dependency, so the timing is unit-tested on virtual time.
 */
internal object PairingAckWatch {
    /** How long a connection may wait for the host's ack before the phone says it needs pairing. */
    const val GRACE_MS = 6_000L

    /**
     * @param connected the connection that is up now, or null.
     * @param authenticated the connection the host last acked, or null.
     * @param ackingHost the connection whose `host_info` shows the PC sends the ack, or null.
     */
    @OptIn(ExperimentalCoroutinesApi::class)
    fun needsPairing(
            connected: Flow<EstablishedConnection?>,
            authenticated: Flow<EstablishedConnection?>,
            ackingHost: Flow<EstablishedConnection?>,
            graceMs: Long = GRACE_MS,
    ): Flow<Boolean> =
            combine(connected, authenticated, ackingHost) { up, acked, acking ->
                // Only the answers that matter for the connection that is up, so an unrelated
                // change (an old epoch's host_info) never restarts the grace.
                Triple(up, up != null && acked?.epoch == up.epoch, up != null && acking?.epoch == up.epoch)
            }
                    .distinctUntilChanged()
                    .transformLatest { (up, isAcked, sendsAck) ->
                        emit(false)
                        if (up != null && sendsAck && !isAcked) {
                            delay(graceMs)
                            emit(true)
                        }
                    }
                    .distinctUntilChanged()
}
