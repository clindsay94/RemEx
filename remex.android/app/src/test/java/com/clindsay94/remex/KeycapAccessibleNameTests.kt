package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Every on-screen key whose cap draws a Latin legend must have an accessible name that STARTS with
 * that legend.
 *
 * This is WCAG 2.5.3 Label in Name: a speech-input user says what they see, so "PgUp" has to be the
 * first thing the name contains. RemEx-qykh established it across all 108 pairs (12 keys x 9
 * locales) and a reviewer checked them by hand - but nothing re-checked it afterwards, and
 * RemEx-jcbw then rewrote 25 of those values. A property verified once by a person is a property
 * that silently decays; this is that check, automated.
 */
class KeycapAccessibleNameTests {

    /**
     * The 12 keys that DRAW a Latin legend, and what each cap actually shows.
     *
     * The seven glyph-only keys - backspace, enter, windows and the four arrows - are excluded on
     * purpose. They draw no text, so 2.5.3 does not apply and a fully localized name is the correct
     * answer for them. `cd_key_enter` in particular must never gain a Latin prefix.
     */
    private val legends = mapOf(
            "cd_key_ctrl" to "Ctrl",
            "cd_key_alt" to "Alt",
            "cd_key_shift" to "Shift",
            "cd_key_altgr" to "AltGr",
            "cd_key_escape" to "Esc",
            "cd_key_tab" to "Tab",
            "cd_key_delete" to "Del",
            "cd_key_home" to "Home",
            "cd_key_end" to "End",
            "cd_key_pageup" to "PgUp",
            "cd_key_pagedown" to "PgDn",
            "cd_key_insert" to "Ins",
    )

    // Unit tests run with the module root (remex.android/app) as the working directory.
    private val localeFiles: List<File> by lazy {
        val res = File("src/main/res")
        assertTrue("Could not locate ${res.absolutePath} - test setup is broken.", res.isDirectory)
        res.listFiles { f -> f.isDirectory && (f.name == "values" || f.name.startsWith("values-")) }
                .orEmpty()
                .map { File(it, "strings.xml") }
                .filter { it.exists() }
                .sortedBy { it.parentFile.name }
    }

    private fun value(source: String, key: String): String? =
            Regex("""name="${Regex.escape(key)}">(.*?)</string>""", RegexOption.DOT_MATCHES_ALL)
                    .find(source)
                    ?.groupValues
                    ?.get(1)

    @Test
    fun `every keycap name starts with the legend drawn on the cap`() {
        assertEquals("Expected all nine locale files.", 9, localeFiles.size)

        val offenders = mutableListOf<String>()
        var checked = 0

        for (file in localeFiles) {
            val locale = file.parentFile.name
            val source = file.readText()

            for ((key, legend) in legends) {
                val name = value(source, key)
                if (name == null) {
                    offenders.add("$locale/$key is missing")
                    continue
                }
                checked++

                // Either the bare legend, or the legend followed by a parenthetical gloss. A bare
                // legend is legitimate and deliberate: es carries "Esc" with no gloss because
                // Spanish simply calls the key Esc, and pl/pt-BR/tr/uk do the same after
                // RemEx-jcbw. A gloss that says nothing in the reader's language is worse than none.
                if (name != legend && !name.startsWith("$legend (")) {
                    offenders.add("$locale/$key = \"$name\" does not start with \"$legend\"")
                }
            }
        }

        assertEquals("Expected 12 keys x 9 locales.", 108, checked)
        assertEquals(
                "A speech-input user says the legend printed on the cap, so the accessible name must " +
                        "start with it (WCAG 2.5.3). Offenders: $offenders",
                emptyList<String>(),
                offenders
        )
    }

    /**
     * NO MECHANICAL "the gloss must not be the English word" CHECK, deliberately.
     *
     * That was written and then removed, because it cannot be right without an exemption list.
     * Spanish carries "Ctrl (Control)" and Indonesian "Ins (Insert)" - byte-identical to English
     * and CORRECT, because those are the words those languages use. RemEx-jcbw fixed the five
     * locales where the gloss really was untranslated English, but the property "this gloss is
     * English" is not decidable from the string; only a reader of the language can say. A test
     * needing a hand-maintained allowlist of exceptions is a test that goes stale silently, which
     * is the failure mode this file exists to avoid.
     */
}
