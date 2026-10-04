package com.clindsay94.remex.service

import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * RemEx-pp4cm.9: a download's first frames can arrive before the receiver has registered its sink
 * (the PC streams the moment it answers ready, on a different socket). A frame with no sink is
 * dropped and never acknowledged, so the PC gave up after its ack-idle window and the phone waited
 * on a frame that was not coming while holding the queue's one running slot.
 */
class EarlyFrameSinkTest {

    private class Recorder : FileFrameSink {
        val offsets = ArrayList<Long>()
        var closed = 0

        override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
            synchronized(offsets) { offsets += envelope.offset }
        }

        override fun onChannelClosed() {
            closed++
        }
    }

    private fun frame(offset: Long) =
        FileFrameEnvelope(kind = FileFrameKinds.DATA, transferId = "t", offset = offset, length = 1)

    @Test
    fun framesThatArriveBeforeArming_areReplayedInOrder_thenLaterFramesFollow() {
        val early = EarlyFrameSink(maxBufferedFrames = 8)
        early.onFrame(frame(0), byteArrayOf(1))
        early.onFrame(frame(1), byteArrayOf(2))
        assertEquals(2, early.heldFrames)

        val sink = Recorder()
        early.arm(sink)
        early.onFrame(frame(2), byteArrayOf(3))

        assertEquals(listOf(0L, 1L, 2L), sink.offsets)
        assertEquals(0, early.heldFrames)
    }

    @Test
    fun aFrameThatArrivesWhileTheReplayRuns_landsAfterTheBufferedOnes() {
        val early = EarlyFrameSink(maxBufferedFrames = 8)
        early.onFrame(frame(0), byteArrayOf(1))
        early.onFrame(frame(1), byteArrayOf(2))

        val replaying = CountDownLatch(1)
        val release = CountDownLatch(1)
        val seen = ArrayList<Long>()
        val slow =
            object : FileFrameSink {
                override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
                    synchronized(seen) { seen += envelope.offset }
                    // Hold the replay open on its first frame so the reader thread can race it.
                    if (envelope.offset == 0L) {
                        replaying.countDown()
                        release.await(5, TimeUnit.SECONDS)
                    }
                }

                override fun onChannelClosed() = Unit
            }

        val armer = thread { early.arm(slow) }
        assertTrue(replaying.await(5, TimeUnit.SECONDS))
        val reader = thread { early.onFrame(frame(2), byteArrayOf(3)) }
        Thread.sleep(50)
        release.countDown()
        armer.join(5_000)
        reader.join(5_000)

        assertEquals(listOf(0L, 1L, 2L), synchronized(seen) { seen.toList() })
    }

    @Test
    fun aChannelThatDroppedBeforeArming_isReportedAfterTheReplay() {
        val early = EarlyFrameSink(maxBufferedFrames = 8)
        early.onFrame(frame(0), byteArrayOf(1))
        early.onChannelClosed()

        val sink = Recorder()
        early.arm(sink)

        assertEquals(listOf(0L), sink.offsets)
        assertEquals(1, sink.closed)
    }

    @Test
    fun overflowingTheBuffer_failsTheTransferInsteadOfKeepingAHalfFile() {
        val early = EarlyFrameSink(maxBufferedFrames = 2)
        early.onFrame(frame(0), byteArrayOf(1))
        early.onFrame(frame(1), byteArrayOf(2))
        early.onFrame(frame(2), byteArrayOf(3))

        val sink = Recorder()
        early.arm(sink)

        assertEquals(listOf(0L, 1L), sink.offsets)
        assertEquals("a lost frame must reach the receiver as a closed channel", 1, sink.closed)
    }

    @Test
    fun afterArming_aClosedChannelGoesStraightToTheSink() {
        val early = EarlyFrameSink(maxBufferedFrames = 2)
        val sink = Recorder()
        early.arm(sink)

        early.onChannelClosed()

        assertEquals(1, sink.closed)
        assertFalse(sink.offsets.isNotEmpty())
    }

    /**
     * The class above only helps if the engine puts it in front of the offer. Source-scanned, the
     * precedent here, because the engine is a singleton over a JNI client this module cannot drive.
     */
    private val source: String
        get() {
            val root =
                System.getProperty("remex.repoRoot")?.let(::File)
                    ?: generateSequence(File(".").absoluteFile) { it.parentFile }
                        .first { File(it, "remex.android").isDirectory }
            return File(root, "remex.android/app/src/main/java/com/clindsay94/remex/service/FileTransferEngine.kt")
                .readText()
        }

    @Test
    fun theEngineHonoursACancelThePcSends_andStopsWaitingOnSilence() {
        val control = source.substring(source.indexOf("\"file_transfer_control\" ->")).take(1_000)
        assertTrue(
            "a cancel from the PC must end the running download's wait",
            control.contains("downloadAborts[") && control.contains("CANCEL"),
        )
        assertTrue(
            "the stream wait must be bounded by silence, not only by the six-hour ceiling",
            source.contains("awaitDownloadStream(") && source.contains("stallMs = DOWNLOAD_STALL_MS"),
        )
    }

    @Test
    fun theEngineRegistersTheEarlySink_beforeItSendsTheOffer() {

        val wrapper = source.substring(source.indexOf("private suspend fun runDownload("))
        val registered = wrapper.indexOf("FileTransferChannelClient.registerSink(t.id, early)")
        val streamCall = wrapper.indexOf("runDownloadStream(t, early, done)")
        assertTrue("runDownload must register the early sink", registered >= 0)
        assertTrue("the early sink must be registered before the offer is sent", registered < streamCall)

        val stream = wrapper.substring(wrapper.indexOf("private suspend fun runDownloadStream("))
        val body = stream.substring(0, stream.indexOf("private fun discardEmptyDownloadTarget"))
        assertTrue("the stream must arm the early sink", body.contains("early.arm(sink)"))
        assertFalse(
            "registering the sink after the ready is the race this fixes",
            body.contains("registerSink(t.id, sink)"),
        )
    }
}
