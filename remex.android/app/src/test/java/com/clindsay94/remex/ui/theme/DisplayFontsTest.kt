package com.clindsay94.remex.ui.theme

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Page titles pick a font that covers the UI language's script (RemEx-kq10x.4). Bungee Shade and
 * Orbitron are Latin-only, so Ukrainian gets Victor Mono and Hindi the system sans. Mirrors the PC's
 * PageDisplayFontRuleTests.
 */
class DisplayFontsTest {

    @Test
    fun latinScriptLanguagesUseTheLatinDisplayFonts() {
        listOf("en", "en-US", "es", "fr", "in", "id", "pl", "pt-BR", "pt_BR", "tr").forEach { tag ->
            assertEquals(tag, DisplayFont.LATIN_DISPLAY, displayFontForLanguage(tag))
        }
    }

    @Test
    fun ukrainianUsesVictorMono() {
        listOf("uk", "uk-UA", "UK").forEach { tag ->
            assertEquals(tag, DisplayFont.VICTOR_MONO, displayFontForLanguage(tag))
        }
    }

    @Test
    fun hindiUsesTheSystemSans() {
        listOf("hi", "hi-IN").forEach { tag ->
            assertEquals(tag, DisplayFont.SYSTEM, displayFontForLanguage(tag))
        }
    }

    @Test
    fun missingLanguageFallsBackToTheLatinDisplayFonts() {
        assertEquals(DisplayFont.LATIN_DISPLAY, displayFontForLanguage(null))
        assertEquals(DisplayFont.LATIN_DISPLAY, displayFontForLanguage(""))
    }
}
