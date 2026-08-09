package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.FileManagerLogic.VolumesOutcome
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The phone half of RemEx-l580's deny reason (RemEx-3qmd).
 *
 * The PC now says WHY it refused a full-device browse when nobody was asked: `denyReason` is
 * `client_unreachable` when the host could not put the question to this phone, and null when a person
 * genuinely decided. Those two need different words on screen, because only one of them is something
 * the person holding the phone can fix.
 *
 * WHAT THIS REPLACED WAS SILENCE, not a flat no. The old path cleared the "Loading drives…" status to
 * an empty string on every non-granted response, so tapping "browse everything" and being refused
 * looked exactly like tapping it and having the screen finish loading. That is why the classification
 * is worth a test rather than being obvious: three of its four outcomes used to render identically.
 *
 * Tested here rather than through [com.clindsay94.remex.ui.screens.FileTransferViewModel] because that
 * is an AndroidViewModel and reaches for Application on every branch — the same wall RemEx-ivkq hit.
 * The decision is pure, so it lives in FileManagerLogic where the JVM can drive it, and the view model
 * keeps only the `when` that turns an outcome into a localized string.
 */
class VolumesOutcomeTest {

    @Test
    fun `a grant is not a refusal`() {
        assertEquals(
            VolumesOutcome.GRANTED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = true, denyReason = null, errorMessage = null,
            ),
        )
    }

    @Test
    fun `an unreachable phone is told to reconnect, not that somebody said no`() {
        assertEquals(
            VolumesOutcome.PHONE_UNREACHABLE,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false,
                denyReason = FileManagerLogic.DENY_REASON_CLIENT_UNREACHABLE,
                errorMessage = null,
            ),
        )
    }

    @Test
    fun `no reason means a person decided`() {
        // THE CONTRACT RemEx-l580 ESTABLISHED, and the direction that costs most to get wrong:
        // reading an absent reason as "unreachable" would tell people to reconnect a phone that is
        // working perfectly, and send them looking for a network fault instead of accepting an answer.
        assertEquals(
            VolumesOutcome.REFUSED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false, denyReason = null, errorMessage = null,
            ),
        )
    }

    @Test
    fun `a blank reason is the same as none`() {
        // A host that spells "no reason" as "" rather than by omitting the field must not read as one
        // that sent a code. org.json hands back "" for a JSON null in some shapes, so this is reachable
        // without anybody writing a bug.
        for (blank in listOf("", "   ", "\t")) {
            assertEquals(
                "denyReason=<$blank> must not look like a code",
                VolumesOutcome.REFUSED,
                FileManagerLogic.classifyVolumesResponse(
                    fullBrowseGranted = false, denyReason = blank, errorMessage = null,
                ),
            )
        }
    }

    @Test
    fun `an unknown reason falls back to a plain refusal rather than guessing`() {
        // Forward compatibility: a host newer than this phone can send a code this build has never
        // heard of. Saying "somebody declined" is wrong-but-harmless; inventing reconnection advice
        // for an unknown code sends the user to fix something that is not broken.
        assertEquals(
            VolumesOutcome.REFUSED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false, denyReason = "some_future_reason", errorMessage = null,
            ),
        )
    }

    @Test
    fun `the reason code is matched exactly, not loosely`() {
        // These are protocol tokens. Folding case or trimming inside the token would make the phone
        // agree with a host that is not actually speaking the same protocol.
        assertEquals(
            VolumesOutcome.REFUSED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false, denyReason = "CLIENT_UNREACHABLE", errorMessage = null,
            ),
        )
    }

    @Test
    fun `surrounding whitespace on a real code still resolves`() {
        // The counterpart to the test above, and the reason trimming happens BEFORE the comparison
        // rather than not at all: whitespace is transport noise, case is a different token.
        assertEquals(
            VolumesOutcome.PHONE_UNREACHABLE,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false, denyReason = "  client_unreachable  ", errorMessage = null,
            ),
        )
    }

    @Test
    fun `a host fault is not reported as a refusal`() {
        // The host sets errorMessage when the request never got as far as asking anybody. Calling that
        // a refusal would blame the user's PC for a fault, and would offer them a decision to revisit
        // that nobody ever made.
        assertEquals(
            VolumesOutcome.FAILED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false, denyReason = null, errorMessage = "boom",
            ),
        )
    }

    @Test
    fun `an error wins over a deny reason`() {
        // Both fields arriving at once is not something the host does today. Pinning the precedence
        // anyway, because the alternative is that a future host filling both changes what the user
        // sees depending on which branch happens to be checked first.
        assertEquals(
            VolumesOutcome.FAILED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = false,
                denyReason = FileManagerLogic.DENY_REASON_CLIENT_UNREACHABLE,
                errorMessage = "boom",
            ),
        )
    }

    @Test
    fun `a granted response carrying a stale reason is still granted`() {
        // The over-tightening control. A grant already held never denies anything, so a reason on a
        // granted response is meaningless — and treating it as a refusal would hide the drives the
        // user just successfully asked for.
        assertEquals(
            VolumesOutcome.GRANTED,
            FileManagerLogic.classifyVolumesResponse(
                fullBrowseGranted = true,
                denyReason = FileManagerLogic.DENY_REASON_CLIENT_UNREACHABLE,
                errorMessage = null,
            ),
        )
    }

    @Test
    fun `the code this phone matches is the one the PC sends`() {
        // Read out of remex.core rather than trusted as a literal: the two ends agreeing is the whole
        // feature, and a typo on either side would silently downgrade every unreachable refusal to a
        // plain "declined" — the exact message RemEx-l580 existed to stop showing.
        assertEquals(
            "remex.core changed the wire value for ClientUnreachable",
            "client_unreachable",
            declaredDenyReasons()["ClientUnreachable"],
        )
        assertEquals(
            FileManagerLogic.DENY_REASON_CLIENT_UNREACHABLE,
            declaredDenyReasons()["ClientUnreachable"],
        )
    }

    @Test
    fun `every deny reason the host declares is one this screen has words for`() {
        // NOT THE SAME TEST AS THE ONE ABOVE, and the difference is the whole point (review). Pinning
        // one code by name can only prove that the mapping which EXISTS is right. It can never notice
        // a code that was never added to it — and FileConsentDenyReasons is shared with
        // FilePushResponse.DenyReason, so a second constant is a plausible next bead.
        //
        // WHAT WOULD GO WRONG WITHOUT THIS: the host starts sending a new reason, classifyVolumesResponse
        // falls through to REFUSED, and the phone tells the user somebody declined when nobody did. No
        // build error, no test failure, no log line. The forward-compatibility fallback is right for a
        // host NEWER than this build; it is not a licence to ignore a code our own repo just added.
        val handled = mapOf(
            "ClientUnreachable" to VolumesOutcome.PHONE_UNREACHABLE,
        )

        val unhandled = declaredDenyReasons().keys - handled.keys
        assertEquals(
            "remex.core declares deny reasons this screen renders as a plain refusal: $unhandled. "
                + "Give each one words in FileManagerLogic.classifyVolumesResponse and add it here, or "
                + "add it to this map pointing at REFUSED if a plain refusal really is the right words.",
            emptySet<String>(),
            unhandled,
        )

        // And each one still classifies the way this map says it does, so the map cannot drift into
        // being a list of names that satisfies the check above while mapping to nothing.
        //
        // HONEST LIMIT (review): REFUSED is also the fallback, so a future `"X" to REFUSED` entry
        // would satisfy both checks without anybody writing a branch. That is a waiver, not a
        // mapping, and it is left possible on purpose — a plain refusal genuinely IS the right words
        // for some codes. Anything mapped to a non-REFUSED outcome is load-bearing here.
        for ((name, expected) in handled) {
            // AssertionError rather than Assert.fail: JUnit 4's fail returns void, so an elvis onto it
            // types the expression as Any and will not compile against a String? — caught in build.
            val wireValue = declaredDenyReasons()[name]
                ?: throw AssertionError(
                    "this test maps $name, which remex.core no longer declares - it was renamed or removed"
                )
            assertEquals(
                "deny reason $name",
                expected,
                FileManagerLogic.classifyVolumesResponse(
                    fullBrowseGranted = false,
                    denyReason = wireValue,
                    errorMessage = null,
                ),
            )
        }
    }

    /**
     * Every `public const string` on `FileConsentDenyReasons`, as declared C# name to wire value.
     *
     * Uses the `remex.repoRoot` system property the build injects rather than guessing from the
     * working directory (RemEx-odkk), and fails loudly if either the property or the file is missing —
     * a test that quietly skips is the same as one that was never written, and this is the one whose
     * job is to notice a quiet divergence. `remex.core/Models/FileTransferMessages.cs` is declared as
     * a task input in app/build.gradle.kts, without which editing ONLY the C# would leave this test
     * skipped as up-to-date.
     */
    private fun declaredDenyReasons(): Map<String, String> {
        val repoRoot = requireNotNull(System.getProperty("remex.repoRoot")) {
            "The remex.repoRoot system property is not set; see the testOptions block in app/build.gradle.kts"
        }
        val source = java.io.File(repoRoot, "remex.core/Models/FileTransferMessages.cs")
        require(source.isFile) { "Not found: ${source.absolutePath}" }

        val body = requireNotNull(
            Regex("""class\s+FileConsentDenyReasons\s*\{(.*?)\n\}""", RegexOption.DOT_MATCHES_ALL)
                .find(source.readText())
        ) { "FileConsentDenyReasons is no longer a class in ${source.name}" }.groupValues[1]

        val declared = Regex("""public\s+(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"([^"]+)"""")
            .findAll(body)
            .associate { it.groupValues[1] to it.groupValues[2] }

        require(declared.isNotEmpty()) { "Parsed no constants out of FileConsentDenyReasons" }

        // AND PROVE THE PARSE SAW EVERYTHING (review). A member declared in some shape this regex does
        // not cover — a computed value, an expression-bodied property — would otherwise be skipped in
        // silence, and the exhaustiveness check above would pass while the phone had no words for it.
        // A skipped member is the exact failure this whole test exists to prevent, so the parser
        // refuses to guess rather than under-reporting.
        val publicMembers = Regex("""^\s*public\s+""", RegexOption.MULTILINE).findAll(body).count()
        require(publicMembers == declared.size) {
            "FileConsentDenyReasons declares $publicMembers public members but only ${declared.size} " +
                "parsed as string constants. Widen the regex in declaredDenyReasons() — a member this " +
                "parser cannot see is a deny reason the phone silently renders as a plain refusal."
        }
        return declared
    }
}
