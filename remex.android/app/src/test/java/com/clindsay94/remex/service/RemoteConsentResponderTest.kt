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
 * Unit tests for [RemoteConsentResponder] (RemEx-vyhm): the phone half of the routed consent flow.
 *
 * The rules under test are the ones a device test cannot show cheaply — a dismissed sheet and an
 * unanswered one must both leave the wire in a DENY, a deadline from another device's clock must not
 * be rendered when it comes back absurd, and one request must produce exactly one response. The JSON
 * and the notification wiring live in [FileConsentManager]; both are seams here.
 */
class RemoteConsentResponderTest {

    private class RecordingPrompter : ConsentPrompter {
        val shown = CompletableDeferred<FileConsentPrompt>()
        val dismissed = mutableListOf<String>()
        var showCount = 0
        var lastPrompt: FileConsentPrompt? = null

        override fun show(prompt: FileConsentPrompt) {
            showCount++
            lastPrompt = prompt
            if (!shown.isCompleted) shown.complete(prompt)
        }

        override fun dismiss(consentId: String) {
            dismissed.add(consentId)
        }

        suspend fun awaitShown(): FileConsentPrompt = shown.await()
    }

    private class RecordingSender : ConsentResponseSender {
        val sent = mutableListOf<Triple<String, Boolean, Boolean>>()

        override fun send(consentId: String, granted: Boolean, remember: Boolean) {
            sent.add(Triple(consentId, granted, remember))
        }
    }

    // ── Fail-closed: every way of not saying yes ends as a deny on the wire ──

    @Test
    fun dismissedSheet_answersDeny() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 5_000)

        val handling = async { responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, null) }
        prompter.awaitShown()
        // What AlertDialog.onDismissRequest does: a tap outside is a deny, not a shrug.
        responder.resolve("c1", granted = false, remember = false)
        handling.await()

        assertEquals(listOf(Triple("c1", false, false)), sender.sent)
    }

    @Test
    fun unansweredSheet_timesOutIntoDeny_andDismisses() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 50)

        responder.handle("pc", "c1", FileConsentKinds.INCOMING_PUSH, "a.txt (1 KB)", null)

        assertEquals(listOf(Triple("c1", false, false)), sender.sent)
        assertEquals(1, prompter.showCount)
        assertTrue(prompter.dismissed.contains("c1"))
    }

    @Test
    fun rememberedDenial_isNotUpgradedToAGrant() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 5_000)

        val handling = async { responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, null) }
        prompter.awaitShown()
        responder.resolve("c1", granted = false, remember = true)
        handling.await()

        assertEquals(listOf(Triple("c1", false, true)), sender.sent)
    }

    @Test
    fun allowWithRemember_isSentVerbatim() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 5_000)

        val handling = async { responder.handle("pc", "c1", FileConsentKinds.INCOMING_PUSH, "a.txt", null) }
        prompter.awaitShown()
        responder.resolve("c1", granted = true, remember = true)
        handling.await()

        assertEquals(listOf(Triple("c1", true, true)), sender.sent)
    }

    // ── The prompt itself: whose files, and what deadline ──

    @Test
    fun prompt_saysTheFilesArePcSide_andCarriesTheHostDeadline() = runBlocking {
        val now = 1_700_000_000_000L
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 5_000, now = { now })

        val handling =
            async { responder.handle("pc", "c1", FileConsentKinds.INCOMING_PUSH, "a.txt (1 KB)", now + 60_000L) }
        val prompt = prompter.awaitShown()
        responder.resolve("c1", granted = false, remember = false)
        handling.await()

        // PAIRED_PC is what makes the sheet read "send files to your PC" instead of "your PC wants to
        // send you files" — the same kind string means the opposite thing in the other direction.
        assertEquals(FileConsentOrigin.PAIRED_PC, prompt.origin)
        assertEquals("pc", prompt.deviceId)
        assertEquals(FileConsentKinds.INCOMING_PUSH, prompt.kind)
        assertEquals("a.txt (1 KB)", prompt.detail)
        assertEquals(now + 60_000L, checkNotNull(prompt.expiresAtUnixMs))
    }

    @Test
    fun absentDeadline_rendersNoCountdown_andFallsBackToTheLocalWindow() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 50)

        // A host that predates expiresAtUnixMs. Showing a locally invented deadline would be a claim
        // about the only clock that decides, and it is not this one.
        responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, null)

        assertNull(checkNotNull(prompter.lastPrompt).expiresAtUnixMs)
        assertEquals(listOf(Triple("c1", false, false)), sender.sent)
    }

    @Test
    fun skewedClock_showsNoCountdownRatherThanAnAbsurdOne() = runBlocking {
        val now = 1_700_000_000_000L
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 50, now = { now })

        // Phone's clock an hour ahead of the host's: the real 60 s window reads as long expired.
        responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, now - 3_600_000L)

        assertNull(checkNotNull(prompter.lastPrompt).expiresAtUnixMs)
        // Still asked, and still fail-closed — a wrong clock must not silently deny a live request.
        assertEquals(1, prompter.showCount)
        assertEquals(listOf(Triple("c1", false, false)), sender.sent)
    }

    @Test
    fun plausibleDeadline_believesOnlyAWindowAHostCouldHaveMeant() {
        val now = 1_700_000_000_000L

        assertNull(RemoteConsentResponder.plausibleDeadline(null, now))
        // Already gone, or the phone's clock is ahead.
        assertNull(RemoteConsentResponder.plausibleDeadline(now, now))
        assertNull(RemoteConsentResponder.plausibleDeadline(now - 1L, now))
        // Further out than any consent window the host issues → a clock, not a policy.
        assertNull(
            RemoteConsentResponder.plausibleDeadline(
                now + RemoteConsentResponder.MAX_PLAUSIBLE_WINDOW_MS + 1L,
                now,
            )
        )
        // The real one, and the edge of what is believable.
        assertEquals(now + 60_000L, RemoteConsentResponder.plausibleDeadline(now + 60_000L, now))
        assertEquals(
            now + RemoteConsentResponder.MAX_PLAUSIBLE_WINDOW_MS,
            RemoteConsentResponder.plausibleDeadline(now + RemoteConsentResponder.MAX_PLAUSIBLE_WINDOW_MS, now),
        )
    }

    // ── One request, one sheet, one answer ──

    @Test
    fun duplicateRequest_doesNotRaiseASecondSheetOrSendASecondAnswer() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 5_000)

        val first = async { responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, null) }
        prompter.awaitShown()
        // A host retry, or the same message observed twice. Dismissing a second sheet over the first
        // would answer a question the user is still reading.
        responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, null)

        assertEquals(1, prompter.showCount)
        assertTrue(sender.sent.isEmpty())

        responder.resolve("c1", granted = true, remember = false)
        first.await()

        assertEquals(listOf(Triple("c1", true, false)), sender.sent)
    }

    @Test
    fun blankConsentId_isIgnoredEntirely() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 50)

        responder.handle("pc", "  ", FileConsentKinds.FULL_BROWSE, null, null)

        assertEquals(0, prompter.showCount)
        assertTrue(sender.sent.isEmpty())
    }

    @Test
    fun resolvingAnUnknownId_answersNothing() = runBlocking {
        val prompter = RecordingPrompter()
        val sender = RecordingSender()
        val responder = RemoteConsentResponder(prompter, sender, fallbackTimeoutMs = 5_000)

        val handling = async { responder.handle("pc", "c1", FileConsentKinds.FULL_BROWSE, null, null) }
        prompter.awaitShown()
        // Another prompt's id — answering it must not resolve this one.
        responder.resolve("c2", granted = true, remember = true)
        assertTrue(sender.sent.isEmpty())

        responder.resolve("c1", granted = false, remember = false)
        handling.await()

        assertEquals(listOf(Triple("c1", false, false)), sender.sent)
        assertFalse(sender.sent.any { it.first == "c2" })
    }
}
