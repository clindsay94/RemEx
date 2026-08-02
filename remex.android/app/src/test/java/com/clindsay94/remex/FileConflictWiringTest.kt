package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Source-reading guards for the conflict wiring no unit test can reach (RemEx-agpn).
 *
 * **THIS MODULE'S LAST-RESORT PATTERN, USED HERE FOR THE SAME REASON AS ITS SIBLINGS** — the rules
 * below live in a `@Composable` and in a coroutine that awaits a UI answer, so exercising them needs
 * an instrumented device this environment does not have. Review found that everything outside the
 * pure policy object was unguarded, including three rules the changelog states as guarantees, and an
 * unverified guarantee is the same class of gap as a wire field with no sender.
 *
 * A source scan cannot prove behaviour. It proves the specific line that implements a decision is
 * still there and still says what it said — which is enough to catch the edit that quietly removes
 * it, and is strictly better than the alternative of no test at all.
 *
 * Comments are stripped before matching, so a guard can never be satisfied by prose describing the
 * rule instead of code implementing it.
 */
class FileConflictWiringTest {

    private fun sourceOf(relativePath: String): String {
        val file = File("src/main/java/com/clindsay94/remex/$relativePath")
        assertTrue("missing source: ${file.absolutePath}", file.exists())

        return file.readText()
            .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
            .replace(Regex("""(?m)//.*$"""), "")
    }

    private val sheet get() = sourceOf("ui/components/FileConflictSheet.kt")
    private val viewModel get() = sourceOf("ui/screens/FileTransferViewModel.kt")

    @Test
    fun `dismissing the sheet answers Skip, and never for the whole batch`() {
        // FAIL-CLOSED. A swipe-down, a back press and a tap outside all arrive at onDismissRequest;
        // if that answered anything but Skip, a gesture made to get rid of a dialog would overwrite
        // a file. Passing applyToAll would let one stray swipe skip every remaining item.
        assertTrue(
            "onDismissRequest must answer (Skip, false)",
            sheet.contains(Regex("""onDismissRequest\s*=\s*\{\s*onResolved\(prompt\.token,\s*ConflictAction\.Skip,\s*false\)\s*\}""")),
        )
    }

    @Test
    fun `the sheet renders the actions the policy chose, in the policy's order`() {
        // The policy decides which buttons exist AND their order - Replace must never be first. A
        // sheet that hardcoded its own list would silently reintroduce both decisions.
        assertTrue("must iterate prompt.actions", sheet.contains(Regex("""for\s*\(\s*action\s+in\s+prompt\.actions\s*\)""")))
        assertFalse(
            "the sheet must not decide which actions exist",
            sheet.contains("listOf(ConflictAction."),
        )
    }

    @Test
    fun `a first attempt carries no conflict resolution`() {
        // "Fail loudly instead of guessing": the host only refuses a collision when the client has
        // NOT pre-answered it, so a resolution sent on the first try would silently overwrite or
        // rename without ever asking the user.
        assertTrue(
            "conflictResolution must be conditional on being non-null",
            viewModel.contains(Regex("""if\s*\(v3Op\s*&&\s*conflictResolution\s*!=\s*null\)\s*put\("conflictResolution""")),
        )
    }

    @Test
    fun `the standing answer is checked against the real operation`() {
        // Review caught a hardcoded COPY here. It was harmless only because copy and move share a
        // rule today, which is exactly why it would have survived until they stopped sharing one.
        assertTrue(
            "canApply must receive the operation in scope",
            viewModel.contains(Regex("""canApply\(standing,\s*errorCode,\s*operation\)""")),
        )
        assertFalse(
            "canApply must not hardcode an operation",
            viewModel.contains("canApply(standing, errorCode, FileManageOperations."),
        )
    }

    @Test
    fun `the apply-to-all choice is created inside the batch loop`() {
        // Scoped by construction. A BatchConflictChoice held as a ViewModel field would outlive the
        // operation it was given for and overwrite a file in some later, unrelated copy.
        assertTrue(
            "BatchConflictChoice must be a local, not a property",
            viewModel.contains(Regex("""val\s+batchChoice\s*=\s*BatchConflictChoice\(\)""")),
        )
        assertFalse(
            "BatchConflictChoice must not be a ViewModel field",
            viewModel.contains(Regex("""private\s+val\s+\w*[Bb]atchChoice""")),
        )
    }

    @Test
    fun `the batch summary is the only status write in the copy-move batch`() {
        // REVIEW PROVED THE FIRST VERSION OF THIS GUARD VACUOUS: it asserted only that the file
        // CONTAINED "batchSummary(", which reinstating a mid-loop _statusText write left passing.
        // The rule is about POSITION and UNIQUENESS, so the guard has to be too.
        //
        // _statusText is a conflated StateFlow rendered into one label, so any write inside the loop
        // is overwritten before it can be collected - and the summary itself must come AFTER
        // browseRemote(), which clears _statusText synchronously.
        val body = viewModel.substringAfter("private fun manageSelectedTo(").substringBefore("\n    private ")

        val statusWrites = Regex("""_statusText\.value\s*=""").findAll(body).count()
        assertEquals("one status write before the loop, one summary after it", 2, statusWrites)

        val browseAt = body.indexOf("browseRemote()")
        val summaryAt = body.indexOf("batchSummary(")
        assertTrue("browseRemote() must run BEFORE the summary is written", browseAt in 0 until summaryAt)
    }

    @Test
    fun `the summary uses copy-move wording, never the delete strings`() {
        // An all-skipped copy used to read "Deleted 0 items." - a file manager that also deletes
        // telling the user it deleted something it did not touch.
        assertTrue(viewModel.contains("R.plurals.file_conflict_copied_count"))
        assertTrue(viewModel.contains("R.plurals.file_conflict_moved_count"))
        assertFalse(
            "batchSummary must not borrow the delete-specific wording",
            viewModel.substringAfter("private fun batchSummary(").substringBefore("\n    private ")
                .contains("multiResultText"),
        )
    }
}
