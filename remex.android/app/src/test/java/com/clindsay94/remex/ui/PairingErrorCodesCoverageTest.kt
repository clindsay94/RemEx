package com.clindsay94.remex.ui

import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.PairingErrors
import com.clindsay94.remex.ui.screens.PairingSurface
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Proves the Kotlin pairing-cause mapping still covers every cause the C# side can emit.
 *
 * Two lists have to agree and nothing connected them mechanically: `PairingErrorCodes.cs` declares
 * the causes, `PairingErrors.messageRes` turns each into a localized sentence. Add a 15th cause on
 * the C# side, forget the Kotlin branch, and it silently renders as "Pairing failed for an
 * unexpected reason" — no crash, no log, no build error. Same shape as the stale allowlist that
 * bricked all of v3 file transfer (RemEx-y6x6): a value the other side did not recognise, dropped
 * with no error anywhere. (RemEx-odkk.)
 *
 * The existing [PairingErrorParseTest] hand-mirrors the code names, so it can only prove that the
 * mappings which exist do not fall through. It cannot notice a cause that was never added to it.
 * This test reads the C# file instead, so the two lists cannot drift apart silently.
 *
 * Reading a file outside the Gradle module is a deliberate call, not an oversight. These two are
 * already coupled by construction — `libRemexCore.so` is built from `remex.core` and packaged into
 * this same APK, which is why `PairingErrorCodes` explicitly documents that adding a code can never
 * desynchronise a paired device. The coupling exists; this only makes it checkable. The repo root
 * arrives as a system property set in `build.gradle.kts` rather than being guessed from a working
 * directory.
 */
class PairingErrorCodesCoverageTest {

    /**
     * Causes that are SUPPOSED to reach the generic message, so the test must not demand a specific
     * one for them.
     *
     * `PairingErrors.messageRes` documents both: `ARG_MISSING` is a caller bug that is not expected
     * to reach a user at all, and `UNEXPECTED` is by definition the cause with nothing specific to
     * say. Keeping them as an explicit set rather than skipping the check is the point — it forces
     * a NEW code to be triaged into one column or the other rather than quietly joining these two.
     */
    private val intentionallyGeneric = setOf("ARG_MISSING", "UNEXPECTED")

    /** Matches `internal const string Foo = "BAR";` and captures BAR. */
    private val codeDeclaration =
            Regex("""const\s+string\s+\w+\s*=\s*"([A-Z][A-Z0-9_]*)"\s*;""")

    private fun nativeCodes(): List<String> {
        val repoRoot =
                System.getProperty("remex.repoRoot")
                        ?: error(
                                "remex.repoRoot system property not set — see the unitTests block " +
                                        "in app/build.gradle.kts")
        val source = File(repoRoot, "remex.core/Native/PairingErrorCodes.cs")

        // Deliberately fails rather than skipping. This module cannot build without remex.core
        // anyway (the NativeAOT step produces libRemexCore.so from it), so a missing file means
        // something is wrong with the checkout, not that the check is inapplicable — and a guard
        // that silently skips is a guard that silently stops guarding.
        assertTrue(
                "PairingErrorCodes.cs not found at ${source.absolutePath}. " +
                        "If it moved, update this test rather than deleting it.",
                source.isFile)

        return codeDeclaration.findAll(source.readText()).map { it.groupValues[1] }.toList()
    }

    @Test
    fun `the C# source still declares codes this test can read`() {
        // Guards the parser itself: if the regex ever stops matching — a reformat, a switch to
        // raw string literals, a rename — every other assertion here would pass vacuously against
        // an empty list.
        val codes = nativeCodes()
        assertTrue("parsed no codes from PairingErrorCodes.cs — the regex has gone stale", codes.size >= 10)
        assertTrue("expected PIN_REJECTED among the parsed codes", codes.contains("PIN_REJECTED"))
    }

    @Test
    fun `every native pairing code is either mapped or explicitly generic`() {
        val fallingBack =
                nativeCodes()
                        .filter { code ->
                            // Checked on both surfaces: a code mapped on one and not the other
                            // would be a real gap, and messageRes takes the surface for exactly
                            // that reason.
                            PairingErrors.messageRes(code, PairingSurface.Dedicated) ==
                                    R.string.pairing_error_unknown &&
                                    PairingErrors.messageRes(code, PairingSurface.InlineConnect) ==
                                            R.string.pairing_error_unknown
                        }
                        .toSet()

        assertEquals(
                "A cause declared in PairingErrorCodes.cs has no branch in PairingErrors.messageRes, " +
                        "so it renders as the generic 'unexpected reason' message with nothing " +
                        "reporting it. Add a branch, or add the code to intentionallyGeneric if that " +
                        "really is the right message for it.",
                intentionallyGeneric,
                fallingBack)
    }
}
