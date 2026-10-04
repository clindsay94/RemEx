package com.clindsay94.remex.ui.theme

import androidx.compose.ui.text.font.FontFamily
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * RemEx-pp4cm.15: plain family names in every language, and the bundled Nerd Fonts really ship with
 * their files, their licences and a small footprint.
 */
class AppFontsTest {
    private val appDir: File = File(System.getProperty("user.dir") ?: ".")
    private val res = File(appDir, "src/main/res")
    private val licenses = File(appDir, "src/main/assets/licenses")

    private val locales = listOf("values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk")

    private fun fontLabels(locale: String): Map<String, String> {
        val xml = File(res, "$locale/strings.xml").readText(Charsets.UTF_8)
        return Regex("""<string name="(font_[a-z0-9_]+)">([^<]*)</string>""")
            .findAll(xml).associate { it.groupValues[1] to it.groupValues[2] }
    }

    @Test
    fun `every offered font resolves to a real family, the default to the system one`() {
        assertEquals(FontFamily.Default, AppFonts.familyFor("default"))
        AppFonts.options.filter { it.key != "default" }.forEach {
            assertNotEquals("${it.key} must not fall back to the system default", FontFamily.Default, AppFonts.familyFor(it.key))
        }
    }

    @Test
    fun `picker keys are unique and the list has the nerd fonts`() {
        val keys = AppFonts.options.map { it.key }
        assertEquals(keys.size, keys.toSet().size)
        assertTrue(keys.containsAll(listOf("caskaydia_cove_nerd", "hack_nerd", "iosevka_nerd")))
    }

    @Test
    fun `a saved choice from an earlier version still resolves and an unknown key is the default`() {
        // Keys that earlier pickers offered and the current one does not list keep their look.
        listOf("oswald", "playfair", "merriweather", "sans", "serif", "mono", "cursive", "jetbrains_mono").forEach {
            assertNotEquals("legacy key $it", FontFamily.Default, AppFonts.familyFor(it))
        }
        assertEquals(FontFamily.Default, AppFonts.familyFor("a_font_that_was_never_offered"))
        assertEquals(AppFonts.familyFor("HACK_NERD"), AppFonts.familyFor("hack_nerd")) // keys are case-insensitive
    }

    @Test
    fun `font labels are plain family names, identical in all nine languages`() {
        val english = fontLabels("values")
        val offered = AppFonts.options.filter { it.key != "default" }
        assertEquals("expected a label per offered font", true, english.size >= offered.size)
        locales.forEach { locale ->
            val labels = fontLabels(locale)
            english.forEach { (name, value) ->
                assertEquals("$locale $name", value, labels[name])
                assertFalse("$locale $name carries a qualifier: $value", value.contains("(") || value.contains(")"))
            }
        }
        // The style the brief asked for.
        assertEquals("CaskaydiaCove Nerd Font", english["font_caskaydia_cove_nerd"])
        assertEquals("Hack Nerd Font", english["font_hack_nerd"])
        assertEquals("Iosevka Nerd Font", english["font_iosevka_nerd"])
        assertEquals("Inter", english["font_inter"])
        assertEquals("Lexend", english["font_lexend"])
    }

    @Test
    fun `every bundled nerd font has regular and bold files that are real fonts`() {
        assertEquals(3, AppFonts.bundled.size) // anti-vacuity
        AppFonts.bundled.keys.forEach { key ->
            listOf("regular", "bold").forEach { weight ->
                val file = File(res, "font/${key}_$weight.ttf")
                assertTrue("missing ${file.path}", file.isFile)
                val head = file.inputStream().use { s -> ByteArray(4).also { s.read(it) } }
                assertTrue("${file.name} is not an sfnt font", head.contentEquals(byteArrayOf(0, 1, 0, 0)) || String(head) == "true")
            }
        }
    }

    @Test
    fun `the bundled nerd fonts stay small`() {
        val bytes = AppFonts.bundled.keys.sumOf { key ->
            listOf("regular", "bold").sumOf { File(res, "font/${key}_$it.ttf").length() }
        }
        assertTrue("nerd fonts take $bytes bytes", bytes <= 3L * 1024 * 1024)
    }

    @Test
    fun `every bundled nerd font ships with its licence text`() {
        val licenceFiles = mapOf(
            "caskaydia_cove_nerd" to "OFL-CaskaydiaCoveNerdFont.txt",
            "hack_nerd" to "LICENSE-HackNerdFont.txt",
            "iosevka_nerd" to "OFL-IosevkaNerdFont.txt",
        )
        assertEquals("a licence is named for every bundled font", AppFonts.bundled.keys, licenceFiles.keys)
        licenceFiles.forEach { (key, name) ->
            val file = File(licenses, name)
            assertTrue("$key needs $name", file.isFile)
            val text = file.readText(Charsets.UTF_8)
            assertTrue("$name should be a licence", text.contains("SIL Open Font License") || text.contains("MIT License"))
        }
    }
}
