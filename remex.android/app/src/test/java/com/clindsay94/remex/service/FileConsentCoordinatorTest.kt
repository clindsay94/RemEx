package com.clindsay94.remex.service

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Unit tests for [FileConsentCoordinator] (plan WP9): the serving-side trust-state gating and the
 * 60-second auto-deny timeout. The production DataStore-backed [FileTrustStore] cannot be exercised in
 * a plain JVM unit test (no Robolectric in this module), so the coordinator is driven against an
 * in-memory fake store that mirrors the same `fullBrowse` / `autoAccept` per-device grants — this
 * validates the trust-state logic (remembered grants skip the prompt; "remember" persists) without an
 * Android context. The notification/dialog wiring lives in [FileConsentManager].
 */
class FileConsentCoordinatorTest {

    private class FakeTrustStore : FileTrustStore {
        val fullBrowse = mutableMapOf<String, Boolean>()
        val autoAccept = mutableMapOf<String, Boolean>()

        override suspend fun isFullBrowseGranted(deviceId: String): Boolean = fullBrowse[deviceId] ?: false

        override suspend fun isAutoAcceptIncoming(deviceId: String): Boolean = autoAccept[deviceId] ?: false

        override suspend fun setFullBrowseGranted(deviceId: String, granted: Boolean) {
            fullBrowse[deviceId] = granted
        }

        override suspend fun setAutoAcceptIncoming(deviceId: String, enabled: Boolean) {
            autoAccept[deviceId] = enabled
        }
    }

    private class RecordingPrompter : ConsentPrompter {
        val shown = CompletableDeferred<FileConsentPrompt>()
        val dismissed = mutableListOf<String>()
        var showCount = 0

        override fun show(prompt: FileConsentPrompt) {
            showCount++
            if (!shown.isCompleted) shown.complete(prompt)
        }

        override fun dismiss(consentId: String) {
            dismissed.add(consentId)
        }

        suspend fun awaitShown(): FileConsentPrompt = shown.await()
    }

    // ── Trust state: a remembered grant accepts immediately, without a prompt ──

    @Test
    fun autoAcceptGrant_acceptsIncomingPushImmediately_noPrompt() = runBlocking {
        val store = FakeTrustStore().apply { autoAccept["pc"] = true }
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter)

        val decision = coordinator.requestConsent("pc", FileConsentKinds.INCOMING_PUSH, "a.txt (1 KB)")

        assertTrue(decision.granted)
        assertTrue(decision.remember)
        assertEquals(0, prompter.showCount)
    }

    @Test
    fun fullBrowseGrant_acceptsFullBrowseImmediately_noPrompt() = runBlocking {
        val store = FakeTrustStore().apply { fullBrowse["pc"] = true }
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter)

        val decision = coordinator.requestConsent("pc", FileConsentKinds.FULL_BROWSE, null)

        assertTrue(decision.granted)
        assertEquals(0, prompter.showCount)
    }

    @Test
    fun autoAcceptGrantForOtherDevice_doesNotLeak() = runBlocking {
        val store = FakeTrustStore().apply { autoAccept["other"] = true }
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter, timeoutMs = 40)

        // No grant for "pc" → must prompt, and with no responder it times out into a deny.
        val decision = coordinator.requestConsent("pc", FileConsentKinds.INCOMING_PUSH, null)

        assertFalse(decision.granted)
        assertEquals(1, prompter.showCount)
    }

    // ── Trust state: accepting with "remember" persists the matching grant ──

    @Test
    fun acceptWithRemember_persistsAutoAcceptGrant() = runBlocking {
        val store = FakeTrustStore()
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter, timeoutMs = 5_000)

        val request = async { coordinator.requestConsent("pc", FileConsentKinds.INCOMING_PUSH, "a.txt") }
        val prompt = prompter.awaitShown()
        coordinator.resolve(prompt.consentId, granted = true, remember = true)
        val decision = request.await()

        assertTrue(decision.granted)
        assertTrue(decision.remember)
        assertEquals(true, store.autoAccept["pc"])
    }

    @Test
    fun acceptWithoutRemember_doesNotPersistGrant() = runBlocking {
        val store = FakeTrustStore()
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter, timeoutMs = 5_000)

        val request = async { coordinator.requestConsent("pc", FileConsentKinds.INCOMING_PUSH, "a.txt") }
        val prompt = prompter.awaitShown()
        coordinator.resolve(prompt.consentId, granted = true, remember = false)
        val decision = request.await()

        assertTrue(decision.granted)
        assertFalse(decision.remember)
        assertNull(store.autoAccept["pc"])
    }

    @Test
    fun denyResolution_returnsDenied_andNeverPersists() = runBlocking {
        val store = FakeTrustStore()
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter, timeoutMs = 5_000)

        val request = async { coordinator.requestConsent("pc", FileConsentKinds.FULL_BROWSE, null) }
        val prompt = prompter.awaitShown()
        coordinator.resolve(prompt.consentId, granted = false, remember = true)
        val decision = request.await()

        assertFalse(decision.granted)
        assertNull(store.fullBrowse["pc"])
    }

    // ── Timeout: an unanswered prompt auto-denies and dismisses ──

    @Test
    fun unansweredPrompt_timesOutIntoCleanDeny() = runBlocking {
        val store = FakeTrustStore()
        val prompter = RecordingPrompter()
        val coordinator = FileConsentCoordinator(store, prompter, timeoutMs = 50)

        val decision = coordinator.requestConsent("pc", FileConsentKinds.INCOMING_PUSH, "big.zip (2 GB)")

        assertFalse(decision.granted)
        assertFalse(decision.remember)
        assertEquals(1, prompter.showCount)
        assertTrue(prompter.dismissed.isNotEmpty())
    }

    @Test
    fun prompt_carriesKindDetailAndExpiry() = runBlocking {
        val store = FakeTrustStore()
        val prompter = RecordingPrompter()
        val start = System.currentTimeMillis()
        val coordinator = FileConsentCoordinator(store, prompter, timeoutMs = 60_000, now = { start })

        val request = async { coordinator.requestConsent("pc", FileConsentKinds.INCOMING_PUSH, "a.txt (1 KB)") }
        val prompt = prompter.awaitShown()
        coordinator.resolve(prompt.consentId, granted = false, remember = false)
        request.await()

        assertEquals(FileConsentKinds.INCOMING_PUSH, prompt.kind)
        assertEquals("a.txt (1 KB)", prompt.detail)
        assertEquals("pc", prompt.deviceId)
        assertEquals(start + 60_000, prompt.expiresAtUnixMs)
    }
}
