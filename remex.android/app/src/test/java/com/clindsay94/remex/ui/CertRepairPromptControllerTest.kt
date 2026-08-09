package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.CertRepairPromptController
import com.clindsay94.remex.ui.screens.CertRepairState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Cancel is final (RemEx-ivkq, pinning RemEx-vnps' acceptance criterion 4).
 *
 * The dialog that offers to discard a certificate pin runs a probe while it is open. If that probe
 * can write its answer back after the user has dismissed the dialog, two things go wrong that are
 * both worse than they sound: a dialog the user closed reappears, and it reappears already carrying
 * an answer to the question they declined — with the button that discards their pin on it.
 *
 * Two mechanisms hold that line, and the note above the second test says which does what — the bead
 * attributes it all to the requestId comparison, which is not quite right. Both live in the same two
 * update blocks, so a refactor that drops one almost certainly drops both, and these tests between
 * them catch either mutation. It was all unreachable while it lived inside an AndroidViewModel;
 * [CertRepairPromptController] is the seam that makes it reachable, and these are the tests that seam
 * exists for.
 *
 * No dispatcher, no runTest, no Robolectric: the controller applies each change before returning, so
 * a test can interleave "the user cancels" and "the probe lands" in whatever order it likes.
 */
class CertRepairPromptControllerTest {

    private val pinned = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="
    private val presented = "Zq3WdE5rT7yU9iO1pA2sD4fG6hJ8kL0zX3cV5bN7mQ4="

    @Test
    fun `a probe that lands after dismiss does not resurrect the dialog`() {
        val controller = CertRepairPromptController()

        val requestId = controller.begin("192.168.1.50")!!
        controller.dismiss()

        // The probe was already in flight when the user cancelled. Both writes must be dropped.
        controller.applyPinnedPin(requestId, pinned)
        controller.applyProbeResult(requestId, presented)

        assertNull("cancel must be final — the dialog must not come back", controller.prompt.value)
    }

    // WHICH MECHANISM PROTECTS WHICH CASE, measured rather than assumed: deleting the requestId
    // comparison from both update blocks fails ONLY the next test, not the one above. After a dismiss
    // the state is already null and `update { it?.copy(...) }` leaves it null, so NULL-SAFETY is what
    // stops resurrection and the requestId guard is what stops a cross-prompt write. The test above is
    // still worth having — it fails if someone replaces the guarded update with an unconditional
    // assignment, which is a plausible "simplification" — but it does not exercise the comparison, and
    // a reader should not believe it does.

    @Test
    fun `a probe from a previous opening cannot write into the current one`() {
        val controller = CertRepairPromptController()

        val first = controller.begin("192.168.1.50")!!
        val second = controller.begin("192.168.1.99")!!

        // The first host's probe finishes late, while the second host's dialog is on screen. Writing
        // it through would put one PC's fingerprint under another PC's name — and mark the second
        // dialog finished when its own probe has not returned.
        controller.applyProbeResult(first, presented)

        val current = controller.prompt.value
        assertNotNull(current)
        assertEquals("the newer opening must still own the dialog", second, current!!.requestId)
        assertEquals("192.168.1.99", current.hostId)
        assertNull("the stale probe's answer must not appear", current.presentedPin)
        assertEquals(CertRepairState.Checking, current.state)
    }

    @Test
    fun `the owning probe does write through`() {
        // The over-broad direction. A guard that dropped everything would pass both tests above and
        // leave the dialog stuck on "Checking" forever.
        val controller = CertRepairPromptController()

        val requestId = controller.begin("192.168.1.50")!!
        controller.applyPinnedPin(requestId, pinned)
        controller.applyProbeResult(requestId, presented)

        val current = controller.prompt.value
        assertNotNull(current)
        assertEquals(pinned, current!!.pinnedPin)
        assertEquals(presented, current.presentedPin)
        assertTrue(current.probeFinished)
        assertEquals(CertRepairState.Changed, current.state)
    }

    @Test
    fun `request ids are never reused`() {
        // Reuse is the one thing that would defeat the guard while every other test still passed: an
        // id coming round again while an old probe is in flight makes a stale write look current.
        // HONEST LIMIT: this proves strictly increasing over five openings, which catches the
        // realistic mutation (resetting the counter per open). It would NOT catch a deliberately
        // wrapping counter, and the constructor does not let a test seed one to check (review).
        val controller = CertRepairPromptController()

        val ids = (1..5).map { controller.begin("192.168.1.$it")!! }

        assertEquals("every opening must get its own id", ids.size, ids.toSet().size)
        assertEquals("ids must increase, not cycle", ids.sorted(), ids)
    }

    @Test
    fun `a blank host opens nothing`() {
        val controller = CertRepairPromptController()

        assertNull(controller.begin("   "))
        assertNull(controller.prompt.value)
    }

    @Test
    fun `take for confirm clears the dialog and hands back what it showed`() {
        // Clearing before the caller acts is deliberate: the repair is asynchronous, and leaving the
        // dialog up while it runs invites a second confirm on the same prompt.
        val controller = CertRepairPromptController()

        val requestId = controller.begin("192.168.1.50")!!
        controller.applyPinnedPin(requestId, pinned)
        controller.applyProbeResult(requestId, presented)

        val taken = controller.takeForConfirm()

        assertNotNull(taken)
        assertEquals("192.168.1.50", taken!!.hostId)
        assertTrue(taken.canRepair)
        assertNull(controller.prompt.value)
        assertNull("a second confirm must find nothing", controller.takeForConfirm())
    }
}
