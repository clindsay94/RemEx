package com.clindsay94.remex.ui.screens

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The language picker reads res/xml/locales_config.xml, and the build keeps whatever values-* folders
 * exist (RemEx-fwhqw). This keeps the three in step: a folder missing from locales_config ships strings
 * nobody can pick, and an entry with no folder offers a language that shows English.
 */
class AppLanguagesTest {

    // Unit tests run with the module root (remex.android/app) as the working directory.
    private val res = File("src/main/res")

    private val localeDir = Regex("""values-([a-z]{2,3})(?:-r([A-Z]{2}))?|values-b\+([A-Za-z0-9+]+)""")

    private fun folderTags(): Set<String> {
        assertTrue("Could not locate ${res.absolutePath} - test setup is broken.", res.isDirectory)
        return res.listFiles().orEmpty()
                .filter { it.isDirectory && File(it, "strings.xml").isFile }
                .mapNotNull { dir ->
                    val m = localeDir.matchEntire(dir.name) ?: return@mapNotNull null
                    val subtags = if (m.groupValues[3].isNotEmpty()) m.groupValues[3].split('+')
                    else listOfNotNull(m.groupValues[1], m.groupValues[2].ifEmpty { null })
                    AppLanguages.normalizeTag(subtags.joinToString("-"))
                }
                .toSet() + "en"
    }

    private fun configTags(): List<String> =
            Regex("""<locale\s+android:name="([^"]+)"\s*/>""")
                    .findAll(File(res, "xml/locales_config.xml").readText(Charsets.UTF_8))
                    .map { it.groupValues[1] }
                    .toList()

    @Test
    fun `locales_config lists exactly the shipped values folders, English first`() {
        val config = configTags()
        val folders = folderTags()
        assertTrue("Found only ${folders.size} locale folders - the scan is broken.", folders.size >= 9)
        assertEquals("English must come first, the picker keeps that order.", "en", config.firstOrNull())
        assertEquals("Duplicate entries in locales_config.xml: $config", config.size, config.toSet().size)
        assertEquals(
                "Run scripts/import_play_translations.py (or fix locales_config.xml by hand) so the picker " +
                        "offers exactly the languages that ship.",
                folders.sorted(),
                config.sorted()
        )
    }

    @Test
    fun `locales_config uses modern tags, not the legacy folder codes`() {
        configTags().forEach { tag -> assertEquals(tag, AppLanguages.normalizeTag(tag), tag) }
    }

    @Test
    fun `the first nine languages keep the picker names they have always shown`() {
        val expected = listOf(
                "en" to "English",
                "es" to "Español",
                "fr" to "Français",
                "hi" to "हिन्दी",
                "id" to "Bahasa Indonesia",
                "pl" to "Polski",
                "pt-BR" to "Português (BR)",
                "tr" to "Türkçe",
                "uk" to "Українська",
        )
        assertEquals(expected, AppLanguages.options(listOf("en", "es", "fr", "hi", "id", "pl", "pt-BR", "tr", "uk")))
    }

    @Test
    fun `the other languages show their own name for themselves`() {
        mapOf("de" to "Deutsch", "ja" to "日本語", "ar" to "العربية", "he" to "עברית", "nb" to "Norsk bokmål")
                .forEach { (tag, name) -> assertEquals(tag, name, AppLanguages.nativeName(tag)) }
        listOf("bn", "bg", "ca", "zh-CN", "hr", "cs", "da", "nl", "et", "fi", "el", "gu", "hu", "is", "it",
                "kn", "ko", "lv", "lt", "ml", "mr", "fa", "ro", "ru", "sk", "sl", "sw", "sv", "ta", "te", "th",
                "ur", "vi", "zu", "pa").forEach { tag ->
            val name = AppLanguages.nativeName(tag)
            assertTrue(tag, name.isNotBlank())
            assertNotEquals("$tag has no display name", tag, name)
        }
    }

    @Test
    fun `English comes first and legacy codes are folded`() {
        assertEquals(listOf("en", "he", "id"), AppLanguages.options(listOf("iw", "in", "en", "he")).map { it.first })
    }

    @Test
    fun `the current locale resolves to an offered tag`() {
        val offered = listOf("en", "he", "id", "pt-BR", "zh-CN")
        assertEquals("he", AppLanguages.resolve("he-IL", offered))
        assertEquals("he", AppLanguages.resolve("iw", offered))
        assertEquals("id", AppLanguages.resolve("in", offered))
        assertEquals("pt-BR", AppLanguages.resolve("pt-BR", offered))
        assertEquals("zh-CN", AppLanguages.resolve("zh-CN", offered))
        assertNull("pt-PT is not pt-BR", AppLanguages.resolve("pt-PT", offered))
        assertNull(AppLanguages.resolve("de", offered))
    }
}
