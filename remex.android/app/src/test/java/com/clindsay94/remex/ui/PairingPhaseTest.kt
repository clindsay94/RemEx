package com.clindsay94.remex.ui

import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.PairingErrors
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the pairing phase tokens and their mapping (RemEx-g87x).
 *
 * The wait these describe can be ninety seconds against a PC that accepts TCP and TLS and then goes
 * quiet, and before this the screen showed a bare spinner for all of it — so the user could not tell
 * a slow network from a wedged host, or judge whether cancelling cost them anything.
 *
 * **THE TOKENS CROSS A LANGUAGE BOUNDARY, WHICH IS WHERE THEY CAN BREAK SILENTLY.** They are emitted
 * as string constants by `AndroidNativeExports.PairingPhases` and matched as string literals here.
 * Nothing connects the two: rename one side and the phase simply stops rendering — no crash, no log,
 * no build error. That is the same shape as the stale allowlist that bricked v3 file transfer
 * (RemEx-y6x6), and the reason `PairingErrorCodesCoverageTest` next door reads a C# file of its own too.
 */
class PairingPhaseTest {

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    /**
     * The phase constants declared natively, as (C# identifier, wire token) pairs.
     *
     * Parsed rather than mirrored, and shared by both guards so they cannot disagree about what the
     * set is. The token pattern allows digits: a future PROBE_2 or TLS_1_3 would otherwise slip past
     * a `[A-Z_]+` pattern and be silently unguarded, since the floor below would still pass.
     */
    private fun declaredPhases(): List<Pair<String, String>> {
        val exports = File(repoRoot(), "remex.core/Native/AndroidNativeExports.cs").readText()
        val body =
            exports.substringAfter("internal static class PairingPhases")
                .substringBefore("\n    }")

        val declared =
            Regex("""internal const string (\w+) = "([A-Z0-9_]+)";""")
                .findAll(body)
                .map { it.groupValues[1] to it.groupValues[2] }
                .toList()

        assertTrue(
            "expected to find the PairingPhases constants in AndroidNativeExports.cs — if that " +
                "class moved or was renamed, both guards here are testing nothing",
            declared.size >= 3,
        )
        return declared
    }

    @Test
    fun `each phase maps to its own sentence`() {
        assertEquals(R.string.pairing_phase_probe, PairingErrors.phaseRes("PROBE"))
        assertEquals(R.string.pairing_phase_securing, PairingErrors.phaseRes("SECURING"))
        assertEquals(R.string.pairing_phase_awaiting_host, PairingErrors.phaseRes("AWAITING_HOST"))
    }

    @Test
    fun `the three phases are distinct`() {
        // A copy-paste that pointed two phases at one string would leave the screen claiming the
        // same thing through two different waits, which is worse than saying nothing.
        val mapped = listOf("PROBE", "SECURING", "AWAITING_HOST").map { PairingErrors.phaseRes(it) }
        assertEquals("phases must map to three different strings", 3, mapped.toSet().size)
    }

    @Test
    fun `an unknown or absent phase shows nothing rather than crashing`() {
        // The degradation contract. A phase added natively and not yet mapped here must render
        // nothing — not a raw token at the user, and not an exception. That is what lets the native
        // side add a phase without a coordinated release.
        assertNull(PairingErrors.phaseRes(null))
        assertNull(PairingErrors.phaseRes(""))
        assertNull(PairingErrors.phaseRes("SOME_FUTURE_PHASE"))
        assertNull(PairingErrors.phaseRes("probe"))
    }

    @Test
    fun `every token the native side emits is mapped here`() {
        // READS THE C# RATHER THAN MIRRORING IT. A hand-copied list can only prove that the mappings
        // which exist do not fall through; it cannot notice a token that was never added to it. The
        // two files are already coupled by construction — libRemexCore.so is built from remex.core
        // and packaged into this same APK — so this only makes the coupling checkable.
        val declared = declaredPhases()

        for ((_, token) in declared) {
            assertNotNull(
                "the native side emits the phase token \"$token\" and PairingErrors.phaseRes does " +
                    "not map it, so that phase renders as nothing. Add it, or if the phase really " +
                    "should be silent, say so here.",
                PairingErrors.phaseRes(token),
            )
        }
    }

    @Test
    fun `the native side actually emits every phase it declares`() {
        // A token that is declared and never sent is a phase the user will never see, and reads as
        // coverage that is not there.
        // DRIVEN FROM THE PARSED SET, not a hand-copied list. A list written here can only prove
        // that the phases somebody remembered are emitted; it cannot notice a fourth constant added
        // natively and never sent — which is the failure this test is named for.
        val exports = File(repoRoot(), "remex.core/Native/AndroidNativeExports.cs").readText()

        for ((name, _) in declaredPhases()) {
            assertTrue(
                "PairingPhases.$name is declared but never passed to OnNativePairingProgress",
                "OnNativePairingProgress(PairingPhases.$name)" in exports,
            )
        }
    }

    @Test
    fun `every phase string is translated in every locale`() {
        // The localization script checks this repo-wide, but these three are new and a missing one
        // degrades to English mid-sentence rather than failing anything.
        val locales =
            listOf("values", "values-es", "values-fr", "values-hi", "values-in", "values-pl",
                "values-pt-rBR", "values-tr", "values-uk")
        val keys = listOf("pairing_phase_probe", "pairing_phase_securing", "pairing_phase_awaiting_host")

        for (locale in locales) {
            val file = File(repoRoot(), "remex.android/app/src/main/res/$locale/strings.xml")
            assertTrue("expected strings.xml for $locale", file.isFile)
            val xml = file.readText()
            for (key in keys) {
                assertTrue("$locale is missing $key", "name=\"$key\"" in xml)
            }
        }
    }
}
