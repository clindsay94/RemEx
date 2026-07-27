package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Proves every "end process" failure code the host can send is mapped to a localized string here.
 *
 * The host declares these codes in C# (`remex.core/Models/ProcessKillErrorCodes.cs`) and
 * `TaskManagerViewModel.localizeKillFailure` turns each into a sentence in the user's language.
 * Nothing connects the two lists mechanically, so adding a code on the host and forgetting the
 * Kotlin branch degrades silently: the phone falls through to the host's English fallback, which
 * still reads plausibly and produces no crash, no log and no build error — so nobody notices that a
 * whole failure case stopped being translated.
 *
 * This is the same guard shape as `PairingErrorCodesCoverageTest`, and it is written this way for
 * the same reason: a test that hand-mirrors the code names can only prove the mappings that EXIST
 * do not fall through. It can never notice a code that was never added to it. Parsing the
 * declaration file is what makes the check exhaustive rather than merely indicative. (RemEx-r37a.)
 *
 * Reading a file outside this Gradle module is deliberate — `remex.core` is compiled into this same
 * APK as `libRemexCore.so`, so the coupling already exists and this only makes it checkable. The
 * repo root arrives as an injected system property rather than a guessed working directory, and the
 * C# file is a declared task input, without which Gradle would skip this test as up-to-date exactly
 * when the file it guards had changed.
 */
class ProcessKillErrorCodesCoverageTest {

    @Test
    fun everyHostKillErrorCodeIsMappedToALocalizedString() {
        val repoRoot = System.getProperty("remex.repoRoot")
        assumeTrue("remex.repoRoot was not injected; skipping", repoRoot != null)

        val codesFile = File(repoRoot, "remex.core/Models/ProcessKillErrorCodes.cs")
        assumeTrue("Cannot see ${codesFile.path}; skipping", codesFile.isFile)

        // public const string AccessDenied = "kill_access_denied";
        val declared =
                Regex("""public\s+const\s+string\s+\w+\s*=\s*"([^"]+)"""")
                        .findAll(codesFile.readText())
                        .map { it.groupValues[1] }
                        .toList()

        assertTrue(
                "Parsed no codes from ${codesFile.path} — the regex has drifted from the file, " +
                        "which would make this test pass vacuously",
                declared.isNotEmpty()
        )

        val viewModel =
                File(
                        repoRoot,
                        "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/TaskManagerViewModel.kt"
                )
        assertTrue("Cannot see ${viewModel.path}", viewModel.isFile)
        val mapping = viewModel.readText()

        val unmapped = declared.filterNot { mapping.contains("\"$it\"") }

        assertTrue(
                "These host kill-failure codes are not handled in localizeKillFailure, so they " +
                        "reach the user as the host's English instead of a translated sentence: " +
                        "$unmapped",
                unmapped.isEmpty()
        )
    }
}
