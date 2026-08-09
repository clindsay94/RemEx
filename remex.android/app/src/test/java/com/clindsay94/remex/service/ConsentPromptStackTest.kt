package com.clindsay94.remex.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Two consent prompts can be outstanding at once, and answering one must not retire the other
 * (RemEx-hncl).
 *
 * A single slot was enough while every prompt came from one place. RemEx-vyhm added the routed
 * direction, so the PC pushing files to this phone and this phone asking to browse the PC can be
 * asked together — and the second `show()` overwrote the first, then `dismiss()` of the second
 * cleared the slot to null rather than restoring it.
 *
 * UI fidelity rather than consent safety: nothing was ever answered for the user, the overwritten
 * prompt stayed reachable through its notification, and both still fail closed on their own
 * timeouts. What was lost was the dialog — the surface the user is actually looking at.
 */
class ConsentPromptStackTest {

    private fun prompt(id: String) =
        FileConsentPrompt(
            consentId = id,
            deviceId = "pc-1",
            kind = "full_browse",
            detail = null,
            expiresAtUnixMs = null,
        )

    @Test
    fun `dismissing the newer prompt brings the older one back`() {
        // THE BEAD. The first prompt used to vanish for good the moment a second arrived.
        val stack = ConsentPromptStack()
        stack.push(prompt("first"))
        stack.push(prompt("second"))

        assertEquals("second", stack.top()?.consentId)
        assertEquals("first", stack.remove("second")?.consentId)
        assertEquals(1, stack.size())
    }

    @Test
    fun `dismissing the older one leaves the newer on screen`() {
        // Ordinary: a routed prompt can be answered on the PC, or time out, while a local one sits
        // above it. That must not disturb what the user is currently looking at.
        val stack = ConsentPromptStack()
        stack.push(prompt("first"))
        stack.push(prompt("second"))

        assertEquals("second", stack.remove("first")?.consentId)
        assertEquals("second", stack.top()?.consentId)
    }

    @Test
    fun `re-raising the same consent id replaces rather than stacks`() {
        // A re-raise is the same question asked again. Stacking it would leave a duplicate behind
        // that dismissing the top could never clear, so the dialog would show a prompt already
        // answered.
        val stack = ConsentPromptStack()
        stack.push(prompt("same"))
        stack.push(prompt("same"))

        assertEquals(1, stack.size())
        assertNull(stack.remove("same"))
    }

    @Test
    fun `the last dismissal clears the dialog`() {
        val stack = ConsentPromptStack()
        stack.push(prompt("only"))

        assertNull(stack.remove("only"))
        assertEquals(0, stack.size())
    }

    @Test
    fun `dismissing something that was never raised changes nothing`() {
        // Reachable: a notification action for a prompt that already timed out.
        val stack = ConsentPromptStack()
        stack.push(prompt("live"))

        assertEquals("live", stack.remove("stale")?.consentId)
        assertEquals(1, stack.size())
    }
}
