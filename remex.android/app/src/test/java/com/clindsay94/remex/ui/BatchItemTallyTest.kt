package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.FileManagerLogic
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The five ways one batch item can leave the conflict loop, and what each contributes (RemEx-dtbd).
 *
 * The accounting used to be verified only by inspection. `FileConflictWiringTest`'s guards are
 * source-shape assertions — the keyword is `while`, `conflict` derives from `outcome`, the retry
 * result is assigned back — so they would pass unchanged on a loop that double-counted skipped,
 * dropped renamed, or lost the already-counted guard, while a semantically identical rewrite would
 * fail all three while being correct. These read the behaviour instead.
 */
class BatchItemTallyTest {

    @Test
    fun `the first attempt succeeding contributes nothing`() {
        // No conflict, no rename: the item simply worked, and a rename note for it would be a lie.
        val tally = FileManagerLogic.tallyItem(alreadyCounted = false, failed = false, resolvedName = null)

        assertEquals(0, tally.errors)
        assertEquals(null, tally.renamed)
    }

    @Test
    fun `succeeding after a rename reports the name the host actually used`() {
        // The whole point of keep-both: the user is told what it ended up called, and it is the
        // HOST's name rather than the one the client guessed.
        val tally = FileManagerLogic.tallyItem(alreadyCounted = false, failed = false, resolvedName = "report (2).pdf")

        assertEquals(0, tally.errors)
        assertEquals("report (2).pdf", tally.renamed)
    }

    @Test
    fun `a not-a-collision failure is an error`() {
        // The loop breaks without raising a sheet when the action set is empty — "the disk is full"
        // is not a question the user can answer with Replace. It is still a failure.
        val tally = FileManagerLogic.tallyItem(alreadyCounted = false, failed = true, resolvedName = null)

        assertEquals(1, tally.errors)
        assertEquals(null, tally.renamed)
    }

    @Test
    fun `running out of rounds is an ordinary failure`() {
        // Bound exhaustion looks identical here, and should: a squatter re-taking each name the host
        // picks is a failure the user cannot resolve, not a separate category to explain.
        val tally = FileManagerLogic.tallyItem(alreadyCounted = false, failed = true, resolvedName = "report (4).pdf")

        assertEquals(1, tally.errors)
        assertEquals("a failed item contributes no rename, whatever name was in flight", null, tally.renamed)
    }

    @Test
    fun `a skipped item is not counted again and is never an error`() {
        // THE ONE THAT WOULD HURT MOST. Skip is the user resolving the conflict — arguably the most
        // decisive answer available — and reporting it as a failure tells them something went wrong
        // when they chose it. It is already in the skipped tally by the time it reaches here, so
        // adding anything would double-count.
        val failedToo = FileManagerLogic.tallyItem(alreadyCounted = true, failed = true, resolvedName = "x")

        assertEquals(0, failedToo.errors)
        assertEquals(null, failedToo.renamed)
    }
}
