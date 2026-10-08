package com.clindsay94.remex.ui.files

import java.io.IOException
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** The phone's range-read and hash requests to the PC (file browser redesign, 2026-10-08). */
class PcFileRequestsTest {
    private val sent = CopyOnWriteArrayList<JSONObject>()
    private val requests = PcFileRequests { sent += it; true }

    private suspend fun awaitSent(count: Int): JSONObject {
        while (sent.size < count) yield()
        return sent[count - 1]
    }

    private fun reply(type: String, key: String, body: JSONObject) =
        requests.onMessage(JSONObject().put("type", type).put(key, body))

    @Test
    fun aRangeRead_SendsAV3RequestForThatFile_AndReturnsTheBytesOfItsReply() = runBlocking {
        val pending = async { requests.readRange("docs", "Logs/agent.log", 1024, 4096, fromEnd = false) }
        val envelope = awaitSent(1)

        assertEquals("file_read_range_request", envelope.getString("type"))
        assertEquals(3, envelope.getInt("protocolVersion"))
        val body = envelope.getJSONObject("fileReadRangeRequest")
        assertEquals("docs", body.getString("rootId"))
        assertEquals("Logs/agent.log", body.getString("relativePath"))
        assertEquals(1024L, body.getLong("offset"))
        assertEquals(4096, body.getInt("length"))
        assertFalse(body.getBoolean("fromEnd"))

        val consumed = reply(
            "file_read_range_response", "fileReadRangeResponse",
            JSONObject().put("requestId", body.getString("requestId")).put("offset", 1024)
                .put("dataBase64", Base64.getEncoder().encodeToString("hello".toByteArray()))
                .put("fileSize", 1029).put("eof", true),
        )
        val chunk = pending.await()

        assertTrue(consumed)
        assertArrayEquals("hello".toByteArray(), chunk.data)
        assertEquals(1024L, chunk.offset)
        assertEquals(1029L, chunk.fileSize)
        assertTrue(chunk.eof)
    }

    @Test
    fun aReplyForSomeoneElse_IsLeftAlone_AndTheRightOneStillArrives() = runBlocking {
        val pending = async { requests.hash("docs", "a.bin") }
        val id = awaitSent(1).getJSONObject("fileHashRequest").getString("requestId")

        assertFalse(reply("file_hash_response", "fileHashResponse", JSONObject().put("requestId", "not-mine").put("sha256", "x")))
        assertFalse(requests.onMessage(JSONObject().put("type", "file_browse_response")))
        assertTrue(reply("file_hash_response", "fileHashResponse", JSONObject().put("requestId", id).put("sha256", "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=")))

        assertEquals("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=", pending.await())
    }

    @Test
    fun aRefusal_CarriesThePcsReason() = runBlocking {
        val pending = async { runCatching { requests.readRange("docs", "gone.txt", 0, 10, fromEnd = false) } }
        val id = awaitSent(1).getJSONObject("fileReadRangeRequest").getString("requestId")
        reply("file_read_range_response", "fileReadRangeResponse", JSONObject().put("requestId", id).put("errorMessage", "File not found."))

        val error = pending.await().exceptionOrNull()
        assertTrue(error is PcRequestFailedException)
        assertEquals("File not found.", (error as PcRequestFailedException).hostMessage)
    }

    @Test
    fun anAnswerForADifferentPartOfTheFile_IsRefused() = runBlocking {
        val pending = async { runCatching { requests.readRange("docs", "a.txt", 100, 10, fromEnd = false) } }
        val id = awaitSent(1).getJSONObject("fileReadRangeRequest").getString("requestId")
        reply("file_read_range_response", "fileReadRangeResponse", JSONObject().put("requestId", id).put("offset", 0).put("dataBase64", "AAAA").put("fileSize", 3))

        assertTrue(pending.await().exceptionOrNull() is IOException)
    }

    @Test
    fun noAnswer_TimesOut_AndForgetsTheRequest() = runBlocking {
        try {
            requests.readRange("docs", "a.txt", 0, 10, fromEnd = false, timeoutMs = 50)
            fail("an unanswered request must time out")
        } catch (_: PcRequestTimeoutException) {
        }
        val id = sent.single().getJSONObject("fileReadRangeRequest").getString("requestId")
        assertFalse("a late reply finds no waiter", reply("file_read_range_response", "fileReadRangeResponse", JSONObject().put("requestId", id)))
    }

    @Test
    fun aRequestThatCannotBeSent_FailsAtOnce() = runBlocking {
        val offline = PcFileRequests { false }
        try {
            offline.hash("docs", "a.bin", timeoutMs = 60_000)
            fail("an unsendable request must fail, not wait")
        } catch (e: IOException) {
            assertFalse(e is PcRequestTimeoutException)
        }
    }

    @Test
    fun failAll_ReleasesEveryWaiter() = runBlocking {
        val pending = async { runCatching { requests.hash("docs", "a.bin") } }
        awaitSent(1)
        requests.failAll("Disconnected")

        assertEquals("Disconnected", pending.await().exceptionOrNull()?.message)
    }
}
