package com.clindsay94.remex

import com.clindsay94.remex.service.FileConflictCodes
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.lang.reflect.Modifier

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
    private val policySource get() = sourceOf("service/FileConflictPolicy.kt")

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

    @Test
    fun `the sheet body never asserts a cause the client does not know`() {
        // THE SECOND HALF OF THE P1. The body used to be chosen by testing for ONE code and
        // defaulting to "there is already a file or folder with this name" - so every other code,
        // present or future, arrived carrying a false explanation. An unrecognised code must now say
        // nothing about why, because saying nothing is the only honest option.
        assertTrue(
            "the body must branch on the code, not default to the exists string",
            sheet.contains(Regex("""when\s*\(\s*prompt\.errorCode\s*\)""")),
        )
        assertTrue("unknown codes need their own body", sheet.contains("file_conflict_body_unknown"))
        assertTrue("the unusable-name code needs its own body", sheet.contains("file_conflict_body_name_unusable"))
    }

    @Test
    fun `EVERY code the host can send has its own body, checked against the constants themselves`() {
        // THE GUARD THE TEST ABOVE ONLY GESTURED AT. It asserted the body branches on the code and
        // that two specific strings appear - so a NEW code added host-side, which is precisely the
        // case its comment worries about, would sail through and render the unknown body while
        // looking covered. This reads the constants and demands a branch for each.
        val codes = FileConflictCodes::class.java.declaredFields
            .filter { Modifier.isStatic(it.modifiers) && it.type == String::class.java }
            .map { it.name }

        // SELF-RATCHETING, NOT A FLOOR AT TODAY'S COUNT. A bare ">= 4" is satisfied forever, so a
        // future `val NEW_CODE: String = "..."` written WITHOUT `const` - which compiles to an
        // instance field with a static getter, invisible to reflection - would slip past untested,
        // reintroducing one level down the staleness the reflection was meant to end.
        //
        // COUNTING `val`, NOT `const val`, AND THAT IS THE WHOLE POINT. Review ran the mutation and
        // showed the stricter pattern was close to tautological: it missed a non-const declaration
        // exactly where reflection missed it too, so both sides read 4 and agreed on being wrong.
        // Matching every string-valued val makes the non-const case read 5 against reflection's 4
        // and fail, which is the case this exists for. The optional type annotation is tolerated
        // because writing `const val X: String = "y"` is an ordinary style choice, and the earlier
        // pattern failed on it while blaming a missing code.
        // SCOPED TO THE CODES OBJECT. Counting const vals across the whole file also swept up
        // FileConflictResolutions' two wire tokens, which live beside them - so the count said 6
        // against 4 and the guard failed on a correct design rather than on a missing code.
        val codesBlock = policySource.substringAfter("object FileConflictCodes").substringBefore("\n}")
        val declared = Regex("""\bval\s+\w+\s*(?::\s*String\s*)?=\s*"\w+"""").findAll(codesBlock).count()
        assertEquals("a code was declared in a shape reflection cannot see", declared, codes.size)

        val bodies = codes.associateWith { name ->
            Regex("""FileConflictCodes\.$name\s*->\s*R\.string\.(\w+)""")
                .find(sheet)?.groupValues?.get(1)
        }

        for ((name, body) in bodies) {
            assertTrue("$name has no body of its own in the sheet", body != null)
            assertTrue(
                "$name renders the unknown body, which explains nothing to the user",
                body != "file_conflict_body_unknown",
            )
        }
        val named = bodies.values.filterNotNull()
        assertEquals("two codes share one explanation", named.size, named.toSet().size)
    }

    @Test
    fun `a refused RETRY is examined too, not collapsed into an error count`() {
        // THE DEFECT REVIEW FOUND, and it made a whole feature unreachable. The batch used to run the
        // operation, ask once, retry once, and throw away the retry's outcome with a bare errors++.
        // Any code the host can ONLY send on a retry - resolved_name_taken is exactly that, since the
        // name it names is one the host picks only when a conflictResolution arrives - could never
        // reach actionsFor, so its action set, its body text and all nine translations were dead on
        // arrival while every test stayed green.
        //
        // Asserted on the loop's SHAPE because the alternative is instantiating a ViewModel with a
        // live socket. The shape is the property: the outcome that feeds actionsFor must be the one
        // the loop keeps reassigning, not a value captured before the first retry.
        assertTrue(
            "the conflict cycle must be a loop, so a refused retry is asked about again",
            viewModel.contains(Regex("""while\s*\(\s*outcome\.failed\s*&&\s*rounds\s*<\s*MAX_CONFLICT_ROUNDS\s*\)""")),
        )
        assertTrue(
            "the loop must re-read the code from the LATEST outcome",
            viewModel.contains(Regex("""val\s+conflict\s*=\s*outcome\s+as\?\s+ManageOutcome\.HostRefused""")),
        )
        assertTrue(
            "the retry's result must be assigned back into the loop's outcome",
            viewModel.contains(Regex("""outcome\s*=\s*runManage\([^)]*conflictResolution\s*=\s*resolution""")),
        )
    }

    @Test
    fun `the conflict loop is BOUNDED, because a standing answer needs no user`() {
        // An "apply to all" answer satisfies the sheet without asking, so an unbounded loop against a
        // squatter that keeps re-taking each chosen name would spin round-trips behind a spinner with
        // nobody able to stop it. The host's 10,000-suffix cap does not save this: it fires when
        // 10,000 siblings genuinely exist, not when a racing writer re-takes each new name.
        val bound = Regex("""(?:const\s+)?val\s+MAX_CONFLICT_ROUNDS\s*=\s*(\d+)""").find(viewModel)
        assertTrue("the loop must carry an explicit bound", bound != null)

        val rounds = bound!!.groupValues[1].toInt()
        assertTrue("a bound of $rounds allows no retry at all", rounds >= 2)
        assertTrue("a bound of $rounds is high enough to feel like a hang", rounds <= 5)
    }
}
