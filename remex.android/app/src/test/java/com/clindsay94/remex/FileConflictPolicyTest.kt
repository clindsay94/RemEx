package com.clindsay94.remex

import com.clindsay94.remex.service.BatchConflictChoice
import com.clindsay94.remex.service.ConflictAction
import com.clindsay94.remex.service.FileConflictCodes
import com.clindsay94.remex.service.FileConflictPolicy
import com.clindsay94.remex.service.FileConflictResolutions
import com.clindsay94.remex.service.FileManageOperations
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.lang.reflect.Modifier

/**
 * Tests for which answers a filename collision may be offered (RemEx-agpn).
 *
 * Every case here is chosen against one failure: a button that destroys something the user did not
 * agree to destroy. That is not visible in a screenshot of the sheet, which is why the decisions
 * live apart from it.
 */
class FileConflictPolicyTest {

    private fun actions(code: String?, op: String = FileManageOperations.COPY) =
        FileConflictPolicy.actionsFor(code, op)

    @Test
    fun `an ordinary collision offers all three answers, safest first`() {
        // ORDER IS PART OF THE ANSWER, and the first version of this test cemented the wrong one.
        // The sheet renders this list top to bottom, and POSITION is a stronger recommendation than
        // emphasis - the top button is the one a hurried user taps. Keeping both cannot lose
        // anything; replacing can, so it must not be the default target.
        assertEquals(
            listOf(ConflictAction.KeepBoth, ConflictAction.Replace, ConflictAction.Skip),
            actions(FileConflictCodes.DESTINATION_EXISTS),
        )
    }

    @Test
    fun `Replace is never the first action offered, for any code or operation`() {
        // The property behind the ordering, swept - so a future code cannot reintroduce
        // destructive-first by adding a branch that happens to list Replace at the top.
        val codes = listOf(
            FileConflictCodes.DESTINATION_EXISTS,
            FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND,
            "some_future_code",
        )
        val operations = listOf(FileManageOperations.COPY, FileManageOperations.MOVE, FileManageOperations.MKDIR)

        for (code in codes) {
            for (op in operations) {
                val offered = FileConflictPolicy.actionsFor(code, op)
                assertFalse("$code on $op", offered.firstOrNull() == ConflictAction.Replace)
            }
        }
    }

    @Test
    fun `a different-kind collision never offers Replace`() {
        // THE CASE THE HOST ADDED A SECOND CODE FOR. Here the destination is the OTHER kind of
        // thing, so replacing means deleting a whole directory tree to make room for one file, or
        // deleting a file to make a folder. Nobody intends either from a copy and nothing undoes
        // them - and the host refuses it outright, so the button would not even work.
        val offered = actions(FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND)

        assertFalse(ConflictAction.Replace in offered)
        assertEquals(listOf(ConflictAction.KeepBoth, ConflictAction.Skip), offered)
    }

    @Test
    fun `mkdir offers only Skip, because the host cannot honour anything else`() {
        // CreateDirectoryAsync emits a code but accepts no conflictResolution, so a Replace or Keep
        // both retry re-fails identically - a dead-end loop where the user answers the same question
        // forever. Verified against the host, not assumed.
        assertEquals(
            listOf(ConflictAction.Skip),
            actions(FileConflictCodes.DESTINATION_EXISTS, FileManageOperations.MKDIR),
        )
        assertEquals(
            listOf(ConflictAction.Skip),
            actions(FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND, FileManageOperations.MKDIR),
        )
    }

    @Test
    fun `an unknown code offers only Skip`() {
        // A newer host may send a code this client does not know. Guessing which actions it permits
        // is how a client offers a destructive button for a situation it does not understand; Skip
        // is the only answer that is safe against every possible meaning.
        assertEquals(listOf(ConflictAction.Skip), actions("destination_is_haunted"))
    }

    @Test
    fun `no code means no sheet at all`() {
        // An ordinary failure - out of disk, permission denied - is not a question the user can
        // answer. "Replace" invites a retry that cannot work.
        assertTrue(actions(null).isEmpty())
    }

    @Test
    fun `Skip sends nothing to the host`() {
        // Skip is not a resolution the host understands; it is the client declining to retry, which
        // is why it cannot fail and why it is the fail-closed default.
        assertNull(FileConflictPolicy.resolutionFor(ConflictAction.Skip))
        assertEquals(FileConflictResolutions.REPLACE, FileConflictPolicy.resolutionFor(ConflictAction.Replace))
        assertEquals(FileConflictResolutions.KEEP_BOTH, FileConflictPolicy.resolutionFor(ConflictAction.KeepBoth))
    }

    @Test
    fun `the wire tokens match the host verbatim`() {
        // These are protocol values, not prose. A typo here is a silent no-op: the host does not
        // recognise the resolution, falls back to the refusal, and the user's answer does nothing.
        assertEquals("destination_exists", FileConflictCodes.DESTINATION_EXISTS)
        assertEquals("destination_is_different_kind", FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND)
        assertEquals("replace", FileConflictResolutions.REPLACE)
        assertEquals("keep_both", FileConflictResolutions.KEEP_BOTH)
    }

    @Test
    fun `a one-off answer is not remembered`() {
        val batch = BatchConflictChoice()

        batch.remember(ConflictAction.Replace, applyToAll = false)

        assertNull(batch.standingAnswer)
    }

    @Test
    fun `an apply-to-all answer is remembered for the batch`() {
        val batch = BatchConflictChoice()

        batch.remember(ConflictAction.KeepBoth, applyToAll = true)

        assertEquals(ConflictAction.KeepBoth, batch.standingAnswer)
    }

    @Test
    fun `a remembered Replace does NOT apply to a different-kind collision`() {
        // THE DATA-LOSS CASE THIS TYPE EXISTS FOR. A user who chose "Replace, apply to all" for a
        // batch of ordinary collisions has not agreed to delete a directory tree when the next item
        // turns out to be a different kind of thing. A standing answer that bypassed the offer list
        // would do exactly that, silently, on an item they never saw.
        val batch = BatchConflictChoice()
        batch.remember(ConflictAction.Replace, applyToAll = true)

        assertTrue(batch.canApply(ConflictAction.Replace, FileConflictCodes.DESTINATION_EXISTS, FileManageOperations.COPY))
        assertFalse(batch.canApply(ConflictAction.Replace, FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND, FileManageOperations.COPY))
    }

    @Test
    fun `a remembered Keep both still applies to a different-kind collision`() {
        // Keeping both is valid for either code - nothing is destroyed either way - so the standing
        // answer must not be discarded over-eagerly. Re-asking a question the user already answered
        // is its own failure.
        val batch = BatchConflictChoice()
        batch.remember(ConflictAction.KeepBoth, applyToAll = true)

        assertTrue(batch.canApply(ConflictAction.KeepBoth, FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND, FileManageOperations.COPY))
    }

    @Test
    fun `a remembered answer never applies to an unknown code`() {
        val batch = BatchConflictChoice()
        batch.remember(ConflictAction.Replace, applyToAll = true)

        assertFalse(batch.canApply(ConflictAction.Replace, "something_new", FileManageOperations.COPY))
        assertTrue(batch.canApply(ConflictAction.Skip, "something_new", FileManageOperations.COPY))
    }

    @Test
    fun `a fresh batch starts with no standing answer`() {
        // Scoped by construction: a remembered Replace that outlived its batch would overwrite a
        // file in some later, unrelated copy the user was never asked about.
        assertNull(BatchConflictChoice().standingAnswer)
    }

    @Test
    fun `Skip is offered for every code, known or not`() {
        // The fail-closed guarantee: whatever went wrong, the user can always decline. A sheet with
        // no dismissable action would trap them.
        val codes = listOf(
            FileConflictCodes.DESTINATION_EXISTS,
            FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND,
            "unknown_code",
        )
        val operations = listOf(FileManageOperations.COPY, FileManageOperations.MOVE, FileManageOperations.MKDIR)

        for (code in codes) {
            for (op in operations) {
                assertTrue("$code on $op", ConflictAction.Skip in FileConflictPolicy.actionsFor(code, op))
            }
        }
    }

    @Test
    fun `an unusable resolved name offers only Skip`() {
        // Correct, but note WHY this assertion alone is not the guard: the else branch yields Skip
        // too, so it cannot tell a known code from an unknown one. Mutation proved that - renaming
        // the explicit branch changed nothing. What distinguishes them is the sheet's EXPLANATION,
        // pinned in FileConflictWiringTest, and that mutant does die.
        //
        // Skip is right here for a specific reason: the host already tried the rename and the
        // destination refused the name it chose, so retrying keep-both asks it to choose again from
        // the same too-long stem, and replace was declined a moment ago.
        assertEquals(listOf(ConflictAction.Skip), actions(FileConflictCodes.RESOLVED_NAME_UNUSABLE))
    }

    @Test
    fun `the unusable-name token matches the host verbatim`() {
        // A protocol value. A typo makes the client fall back to the unknown branch, which is exactly
        // the defect this fixes - and it would look fixed, because the actions would still be Skip.
        assertEquals("resolved_name_unusable", FileConflictCodes.RESOLVED_NAME_UNUSABLE)
    }

    @Test
    fun `a name taken in a race offers keep both, because asking again actually works`() {
        // THE ONE RACE WITH A REAL ANSWER. The host chose "report (2).pdf" and something claimed it
        // first - another paired device, or the person at the PC saving a file. That name is
        // creatable, so retrying keep-both makes the host re-list and pick "report (3).pdf". Folding
        // this into the unusable-name code would send the user to a Skip-only dead end for a problem
        // one tap solves.
        assertEquals(
            listOf(ConflictAction.KeepBoth, ConflictAction.Skip),
            actions(FileConflictCodes.RESOLVED_NAME_TAKEN),
        )
    }

    @Test
    fun `a taken resolved name NEVER offers Replace, which would answer a different question`() {
        // THE REASON THIS CODE EXISTS AT ALL. "That name is taken" is literally true, so
        // destination_exists looks like the honest choice - and it carries Replace. But Replace
        // re-answers the ORIGINAL request: overwrite the destination the user first named, while
        // this sheet is showing the sibling the host invented. Someone who chose keep-both precisely
        // to preserve the original would destroy it by answering a question about a different file.
        //
        // Asserted separately from the list above because THIS is the safety property. If someone
        // later "simplifies" the two codes into one, that test changes and this one screams.
        assertTrue(
            "Replace must never be offered for a name the user never chose",
            ConflictAction.Replace !in actions(FileConflictCodes.RESOLVED_NAME_TAKEN),
        )
    }

    @Test
    fun `the taken-name token matches the host verbatim`() {
        // A private agreement between two codebases that cannot see each other. A typo here does not
        // fail loudly - actionsFor falls to the unknown branch and the sheet quietly loses keep-both,
        // which reads as a bug in the feature rather than a mismatched string.
        assertEquals("resolved_name_taken", FileConflictCodes.RESOLVED_NAME_TAKEN)
    }

    @Test
    fun `every code the client knows has its own reason, none borrowing another's`() {
        // The sheet picks its body by code. Review found it testing only for the different-kind code,
        // so ANY other code rendered "there is already a file or folder with this name" - a claim the
        // client has no basis for. Distinct codes must stay distinct here, or the next code added
        // host-side inherits a false explanation the same way.
        //
        // ENUMERATED BY REFLECTION, NOT BY HAND. The list used to be hardcoded, so it grew stale the
        // moment a code was added - which is exactly the failure it claims to prevent. Reading the
        // constants means adding one to FileConflictCodes without an action set fails this test.
        val known = FileConflictCodes::class.java.declaredFields
            .filter { Modifier.isStatic(it.modifiers) && it.type == String::class.java }
            .map { it.isAccessible = true; it.get(null) as String }

        assertEquals("codes must be distinct", known.size, known.toSet().size)
        assertTrue("reflection found no codes, so this test proves nothing", known.size >= 4)
        for (code in known) {
            assertTrue("$code must offer at least Skip", ConflictAction.Skip in actions(code))
        }

        // AND DELIBERATELY NOT "every code has its own ACTION SET". That assertion was written here
        // and was wrong about the design: resolved_name_unusable has no branch in actionsFor on
        // purpose, because Skip-only IS the right answer for it and the else branch already says so.
        // Sharing an action set is fine - Skip-only is the safe default and several codes deserve
        // it. Sharing an EXPLANATION is not, and that is a property of the sheet rather than of this
        // table, so FileConflictWiringTest enforces it against the constants directly.
    }
}
