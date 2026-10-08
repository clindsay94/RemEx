package com.clindsay94.remex.service

import java.io.File
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * "Verified" on the phone's transfer rows (file browser redesign, 2026-10-08): claimed only when a SHA-256 was
 * really compared and matched, the same rule the PC's queue follows.
 */
class TransferVerifiedTest {
    private val abcBase64 = "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0="
    private val abcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

    private fun row(state: TransferState, verified: Boolean, sha: String? = abcBase64) =
        QueuedTransfer("t", "upload", "a.txt", 3, "content://x", state = state, sha256 = sha, verified = verified)

    @Test
    fun aVerifiedFinishedRow_ShowsItsHashAsHex() {
        assertEquals(abcHex, row(TransferState.Done, verified = true).verifiedSha256Hex)
    }

    @Test
    fun aRowThatOnlyComputedAHash_OrIsNotFinished_ClaimsNothing() {
        assertNull("nothing compared", row(TransferState.Done, verified = false).verifiedSha256Hex)
        assertNull("still comparing", row(TransferState.Verifying, verified = false).verifiedSha256Hex)
        assertNull("no hash", row(TransferState.Done, verified = true, sha = null).verifiedSha256Hex)
    }

    @Test
    fun theFlagSurvivesARestart_AndAnOlderRowIsNotVerified() {
        val saved = row(TransferState.Done, verified = true).toJson()
        assertTrue(QueuedTransfer.fromJson(JSONObject(saved.toString())).verified)

        val older = JSONObject(saved.toString()).apply { remove("verified") }
        assertFalse(QueuedTransfer.fromJson(older).verified)
    }

    /**
     * The engine is the only writer of the flag and needs a live channel to drive, so its two writes are pinned
     * here by shape: an upload is verified when the host confirmed the hash, a download only when the host sent
     * one to compare against.
     */
    @Test
    fun theEngine_SetsTheFlagOnlyWhereAHashWasCompared() {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: generateSequence(File(".").absoluteFile) { it.parentFile }.first { File(it, "remex.android").isDirectory }
        val engine = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/service/FileTransferEngine.kt").readText()

        assertTrue(engine.contains("if (result != null && result.verified) {\n                updateState(t.id) { it.copy(state = TransferState.Done, verified = true) }"))
        assertTrue(engine.contains("it.copy(state = TransferState.Done, sha256 = actualSha, verified = expectedSha != null)"))
        assertEquals("no other row write may claim verified", 2, Regex("""copy\([^)]*verified = """).findAll(engine).count())
        // The finish notification follows the same two facts.
        assertTrue(engine.contains("showTransferComplete(appContext, t.fileName, isDownload = false, verified = true)"))
        assertTrue(engine.contains("showDownloadComplete(appContext, t.fileName, t.localUri, verified = expectedSha != null)"))
    }
}
