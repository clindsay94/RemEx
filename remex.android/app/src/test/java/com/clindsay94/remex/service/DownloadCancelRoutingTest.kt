package com.clindsay94.remex.service

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The PC releasing a download it has given up on (RemEx-pp4cm.13, covering RemEx-pp4cm.9's route).
 *
 * A `file_transfer_control` cancel from the PC has to end the running download's wait with failure,
 * or the queue's single running slot stays held until the stall clock notices. The route is
 * [FileTransferEngine.onControlMessage] into the wait [awaitDownloadStream] is parked on; nothing
 * covered that join, so a rename of the action or a dropped branch would have failed silently.
 */
class DownloadCancelRoutingTest {

    private val registered = mutableListOf<String>()

    @After
    fun tearDown() {
        registered.forEach { FileTransferEngine.downloadAborts.remove(it) }
    }

    private fun register(id: String): CompletableDeferred<Boolean> {
        val done = CompletableDeferred<Boolean>()
        FileTransferEngine.downloadAborts[id] = done
        registered += id
        return done
    }

    private fun control(id: String, action: String) =
        JSONObject()
            .put("type", "file_transfer_control")
            .put("fileTransferControl", JSONObject().put("transferId", id).put("action", action))
            .toString()

    @Test
    fun aCancelFromThePc_completesTheRunsWaitWithFailure() = runBlocking {
        val done = register("dl-cancel-1")

        FileTransferEngine.onControlMessage(control("dl-cancel-1", FileTransferControlActions.CANCEL))

        assertTrue(done.isCompleted)
        assertFalse(done.await())
    }

    @Test
    fun aCancel_endsTheWaitTheDownloadIsParkedOn_longBeforeTheStallClock() = runBlocking {
        val done = register("dl-cancel-2")
        val clock = { System.nanoTime() / 1_000_000L }
        val startedAt = clock()
        FileTransferEngine.onControlMessage(control("dl-cancel-2", FileTransferControlActions.CANCEL))

        val ok = withTimeout(5_000) {
            awaitDownloadStream(
                done = done,
                ceilingMs = 6L * 60 * 60 * 1000,
                stallMs = 60_000,
                lastActivityMs = { startedAt },
                clockMs = clock,
                pollMs = 20,
            )
        }

        assertFalse(ok)
    }

    @Test
    fun aCancelForAnotherTransfer_leavesThisRunAlone() {
        val mine = register("dl-mine")
        val other = register("dl-other")

        FileTransferEngine.onControlMessage(control("dl-other", FileTransferControlActions.CANCEL))

        assertTrue(other.isCompleted)
        assertFalse("a cancel for another transfer must not touch this one", mine.isCompleted)
    }

    @Test
    fun aControlMessageThatIsNotACancel_doesNotEndTheRun() {
        val done = register("dl-pause")

        FileTransferEngine.onControlMessage(control("dl-pause", FileTransferControlActions.PAUSE))
        FileTransferEngine.onControlMessage(control("dl-pause", FileTransferControlActions.RESUME))

        assertFalse(done.isCompleted)
        assertEquals(1, FileTransferEngine.downloadAborts.keys.count { it == "dl-pause" })
    }

    @Test
    fun aCancelForAnUnknownTransfer_isIgnored() {
        FileTransferEngine.onControlMessage(control("nobody-is-running-this", FileTransferControlActions.CANCEL))
    }
}
