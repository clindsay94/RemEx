package com.clindsay94.remex.service

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.onSubscription
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * RemEx-1iszs: file-control messages come off the JNI thread faster than the slowest subscriber
 * (the file host writing chunks to disk) can take them. Every subscriber must still see every one.
 */
class LosslessEventRelayTest {

    @Test
    fun aBurstFarLargerThanTheBuffer_reachesEverySubscriberWhole_andInOrder() = runBlocking {
        val pumpScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            val relay = LosslessEventRelay<Int>(pumpScope, bufferCapacity = 4)
            val count = 300
            val eagerReady = CompletableDeferred<Unit>()
            val slowReady = CompletableDeferred<Unit>()

            val eager =
                async(Dispatchers.Default) {
                    relay.events.onSubscription { eagerReady.complete(Unit) }.take(count).toList()
                }
            val slow =
                async(Dispatchers.Default) {
                    val seen = mutableListOf<Int>()
                    relay.events.onSubscription { slowReady.complete(Unit) }.take(count).collect {
                        // A subscriber doing real work per event, like a disk write per chunk.
                        delay(1)
                        seen.add(it)
                    }
                    seen
                }
            eagerReady.await()
            slowReady.await()

            // Offered back to back from one non-suspending caller, as the JNI callback does.
            repeat(count) { assertTrue(relay.offer(it)) }

            withTimeout(10_000) {
                assertEquals((0 until count).toList(), slow.await())
                assertEquals((0 until count).toList(), eager.await())
            }
        } finally {
            pumpScope.cancel()
        }
    }
}
