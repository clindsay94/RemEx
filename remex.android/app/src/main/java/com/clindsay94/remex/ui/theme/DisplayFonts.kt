package com.clindsay94.remex.ui.theme

import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import com.clindsay94.remex.R
import java.util.Locale

/**
 * The display type for page titles and subtitles, shared with the PC (RemEx-kq10x.4).
 *
 * Both apps default to Bungee Shade for page titles and Orbitron (Medium) for subtitles, so the phone
 * bundles those files byte-for-byte from remex.desktop/Assets/Fonts. Display fonts are for page
 * titles and subtitles only: never body text, controls, numbers or sensor values.
 */
enum class DisplayFont {
    /** Bungee Shade titles with Orbitron subtitles, the shared default. Both are Latin only. */
    LATIN_DISPLAY,

    /** Victor Mono Bold, bundled on both apps, which covers Cyrillic. */
    VICTOR_MONO,

    /** The platform's default sans, which covers Devanagari. */
    SYSTEM,
}

/**
 * Picks the title font for a UI language, once per language rather than glyph by glyph, so one word
 * never mixes two fonts. Bungee Shade and Orbitron are Latin-only: Ukrainian gets Victor Mono, Hindi
 * the system sans.
 * The PC applies the same rule in `PageDisplayFontRule.ForLanguage`.
 *
 * @param languageTag a BCP 47 tag such as `uk`, `uk-UA` or `pt-BR`; null or blank means English.
 */
fun displayFontForLanguage(languageTag: String?): DisplayFont =
    when (languageTag.orEmpty().substringBefore('-').substringBefore('_').lowercase(Locale.ROOT)) {
        "uk" -> DisplayFont.VICTOR_MONO
        "hi" -> DisplayFont.SYSTEM
        else -> DisplayFont.LATIN_DISPLAY
    }

private val BungeeShadeFamily = FontFamily(Font(R.font.bungee_shade_regular, FontWeight.Normal))

private val OrbitronFamily = FontFamily(Font(R.font.orbitron_medium, FontWeight.Medium))

private val VictorMonoFamily = FontFamily(Font(R.font.victor_mono_bold, FontWeight.Bold))

/** The families and weights page titles and subtitles use for one UI language. */
data class DisplayType(
    val titleFamily: FontFamily,
    val titleWeight: FontWeight,
    val subtitleFamily: FontFamily,
    val subtitleWeight: FontWeight,
)

/**
 * The display type for the current UI language. Reads [LocalConfiguration], so it recomposes when the
 * app language changes (per-app locale or the in-app language setting).
 */
@Composable
@ReadOnlyComposable
fun currentDisplayType(): DisplayType {
    val locales = LocalConfiguration.current.locales
    val tag = if (locales.isEmpty) null else locales[0].toLanguageTag()
    return when (displayFontForLanguage(tag)) {
        DisplayFont.LATIN_DISPLAY ->
            DisplayType(BungeeShadeFamily, FontWeight.Normal, OrbitronFamily, FontWeight.Medium)
        DisplayFont.VICTOR_MONO ->
            DisplayType(VictorMonoFamily, FontWeight.Bold, VictorMonoFamily, FontWeight.Bold)
        DisplayFont.SYSTEM ->
            DisplayType(FontFamily.Default, FontWeight.Bold, FontFamily.Default, FontWeight.Medium)
    }
}
