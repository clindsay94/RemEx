package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the substitution introduced by RemEx-9vcw, where tutorial and FAQ bodies stopped naming
 * other screens with hand-copied text and started taking the screen's own title as `%1$s`.
 *
 * The failure this prevents is loud for the user and silent for everyone else: `stringResource(id)`
 * with no arguments returns the raw string, so a body that declares `%1$s` but whose page forgets
 * `bodyArgRes` renders a literal `%1$s` on screen. Nothing else in the build reports that -- it
 * compiles, it lints clean, and check-localization.ps1 is happy because the placeholder IS present
 * in every locale, which is the very thing making it visible.
 *
 * Checked in BOTH directions. The reverse case -- an argument supplied for a string with no
 * placeholder -- is quieter but still wrong: the extra argument is discarded without complaint, so
 * the screen name silently disappears from the sentence.
 */
class ScreenNameSubstitutionTests {

    // A plain string, not a raw one: inside """...""" Kotlin does not process backslash escapes,
    // so "%1\$s" there parses as the template reference $s and fails to compile.
    private val placeholder = "%1\$s"

    // Unit tests run with the module root (remex.android/app) as the working directory, matching
    // RemexConnectionServiceContractTests. Only the ENGLISH file is read: per-locale placeholder
    // agreement is check-localization.ps1's axis 4, and duplicating it here would report the same
    // defect twice under two names.
    private fun read(relative: String): String {
        val file = File(relative)
        assertTrue(
                "Could not locate $relative at ${file.absolutePath} - test setup is broken, " +
                        "not the code under test.",
                file.exists()
        )
        return file.readText()
    }

    private val baseStrings by lazy { read("src/main/res/values/strings.xml") }

    private fun stringValue(key: String): String {
        val match =
                Regex("""name="${Regex.escape(key)}">(.*?)</string>""", RegexOption.DOT_MATCHES_ALL)
                        .find(baseStrings)
        assertTrue("values/strings.xml declares no key named '$key'.", match != null)
        return match!!.groupValues[1]
    }

    /**
     * Each declaration of [constructor] is one chunk of source. Splitting is enough because the
     * argument pair always sits inside the same call, and the data-class DECLARATION chunk is
     * skipped naturally: it contains `val bodyRes: Int,` rather than `bodyRes = R.string.x`.
     */
    private fun assertPairing(
            sourcePath: String,
            constructor: String,
            resProperty: String,
            argProperty: String
    ) {
        val source = read(sourcePath)
        val resPattern = Regex("""$resProperty\s*=\s*R\.string\.(\w+)""")
        var examined = 0

        for (chunk in source.split("$constructor(").drop(1)) {
            val key = resPattern.find(chunk)?.groupValues?.get(1) ?: continue
            examined++

            val declaresPlaceholder = stringValue(key).contains(placeholder)
            val suppliesArgument = Regex("""$argProperty\s*=\s*R\.string\.\w+""").containsMatchIn(chunk)

            if (declaresPlaceholder && !suppliesArgument) {
                throw AssertionError(
                        "$key contains %1\$s but its $constructor does not set $argProperty, so the " +
                                "user is shown a literal %1\$s. Add $argProperty, or remove the " +
                                "placeholder from all nine strings.xml files."
                )
            }
            if (!declaresPlaceholder && suppliesArgument) {
                throw AssertionError(
                        "$constructor sets $argProperty for $key, but $key has no %1\$s. The " +
                                "argument is discarded silently, so the screen name never appears."
                )
            }
        }

        assertTrue(
                "Found no $resProperty declarations in $sourcePath - the split/regex no longer " +
                        "matches this file, so this test would pass vacuously.",
                examined > 0
        )
    }

    @Test
    fun `every tutorial body declaring a placeholder supplies its argument`() {
        assertPairing(
                sourcePath = "src/main/java/com/clindsay94/remex/ui/screens/TutorialScreen.kt",
                constructor = "TutorialPage",
                resProperty = "bodyRes",
                argProperty = "bodyArgRes"
        )
    }

    @Test
    fun `every FAQ answer declaring a placeholder supplies its argument`() {
        assertPairing(
                sourcePath = "src/main/java/com/clindsay94/remex/ui/screens/FaqScreen.kt",
                constructor = "FaqItem",
                resProperty = "answerRes",
                argProperty = "answerArgRes"
        )
    }

    /**
     * The two strings RemEx-9vcw actually converted. Kept explicit so that deleting the placeholder
     * from both the string and its call site -- which the pairing tests above would accept as
     * consistent -- still fails, because that silently restores the hand-copied screen name this
     * whole change exists to remove.
     */
    @Test
    fun `the tutorial and FAQ still name the dashboard by its own title resource`() {
        for (key in listOf("tutorial_page6_body", "faq_a8")) {
            assertEquals(
                    "$key must reference the dashboard through %1\$s so it tracks " +
                            "screen_dashboard_title. Naming that screen literally is the defect " +
                            "RemEx-7gwa and RemEx-9vcw each had to fix.",
                    1,
                    stringValue(key).split(placeholder).size - 1
            )
        }
    }
}
