package com.clindsay94.remex.ui.screens

import android.app.LocaleConfig
import android.content.Context
import java.util.Locale

/**
 * The languages the in-app picker offers, read from `res/xml/locales_config.xml` rather than listed
 * here, so a language added by `scripts/import_play_translations.py` shows up without a code change
 * (RemEx-fwhqw). `AppLanguagesTest` keeps that file in step with the `values-*` folders.
 */
internal object AppLanguages {

    // The picker names the first nine languages have always shown (a name, not a translation:
    // Gemini now writes all of them but English). Later languages use the platform's own name.
    private val curatedNames =
            linkedMapOf(
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

    /** Legacy ISO 639 codes, which resource folders and old Locale APIs still report. */
    private val legacyLanguages = mapOf("in" to "id", "iw" to "he", "ji" to "yi")

    fun normalizeTag(tag: String): String {
        val parts = tag.replace('_', '-').split('-')
        val language = parts.first().lowercase(Locale.ROOT)
        return (listOf(legacyLanguages[language] ?: language) + parts.drop(1)).joinToString("-")
    }

    fun nativeName(tag: String): String {
        curatedNames[tag]?.let { return it }
        val locale = Locale.forLanguageTag(tag)
        val name = locale.getDisplayName(locale)
        if (name.isBlank() || name.equals(tag, ignoreCase = true)) return tag
        return name.replaceFirstChar { if (it.isLowerCase()) it.titlecase(locale) else it.toString() }
    }

    /** (tag, native name) for each supported tag: English first, then in locales_config order. */
    fun options(tags: List<String>): List<Pair<String, String>> {
        val normalized = tags.map(::normalizeTag).distinct()
        val ordered = (if ("en" in normalized) listOf("en") else emptyList()) + normalized.filter { it != "en" }
        return ordered.map { it to nativeName(it) }
    }

    /**
     * Which offered tag the app's current locale is. Settings can store a regional form such as
     * `he-IL` for an offered `he`, so a language-only match counts when there is no exact one.
     */
    fun resolve(current: String, offered: List<String>): String? {
        val tag = normalizeTag(current)
        offered.firstOrNull { it.equals(tag, ignoreCase = true) }?.let { return it }
        val language = tag.substringBefore('-')
        return offered.firstOrNull { !it.contains('-') && it.equals(language, ignoreCase = true) }
    }

    fun supportedTags(context: Context): List<String> {
        val locales = LocaleConfig.fromContextIgnoringOverride(context).supportedLocales
        if (locales == null || locales.isEmpty) return curatedNames.keys.toList()
        return (0 until locales.size()).map { locales.get(it).toLanguageTag() }
    }
}
