package com.clindsay94.remex.data

/**
 * The colours a phone starts on before the user has chosen anything: the RemEx default scheme.
 *
 * THE ONE PLACE THESE LIVE (RemEx-wqo7a.3). [SettingsManager.PersonalizationPreferences]'s defaults,
 * every DataStore read fallback, `RemExTheme`'s parameter defaults, the Personalization preview and
 * the theme-sync fallback all read from here, so a fresh install, an empty DataStore read and the
 * pre-load frame cannot disagree about what "no choice yet" looks like.
 *
 * The values are the PC's default preset, `BaseDarkGlass` (`remex.desktop/Models/SeedPreset.cs`):
 * seed `#6C4CFF`, Tonal Spot, dark, contrast 0. They paint through the custom-seed path
 * (`colorSchemeFromSeed`), never wallpaper colours, so the phone and the PC start out looking like
 * the same app. Anything the user has saved is read back as saved; these only fill the gaps.
 */
object ThemeDefaults {
        /** Dark, like the PC's default preset. */
        const val THEME_MODE = "dark"

        /** "custom" is what sends `RemExTheme` down the `colorSchemeFromSeed` branch. */
        const val THEME_PALETTE = "custom"

        const val THEME_STYLE = "tonal_spot"

        /** `BaseDarkGlass`'s seed, the PC's default accent. */
        const val THEME_SEED_COLOR = "#6C4CFF"

        /**
         * The seed's own HCT chroma (78.89, rounded). The custom path rebuilds the seed as
         * `Hct.from(hue, chroma, tone)`, so any other value would paint (and sync to the PC) a
         * different colour from [THEME_SEED_COLOR]. 78.9 round-trips to exactly `#6C4CFF`.
         */
        const val THEME_SEED_CHROMA = 78.9f

        const val THEME_CONTRAST = 0.0f

        /** Off: wallpaper colours are something the user turns on, not where the app starts. */
        const val DYNAMIC_COLOR = false

        /** [THEME_SEED_COLOR] as an ARGB int, for the fallbacks that need a colour rather than a string. */
        val THEME_SEED_ARGB: Int = 0xFF6C4CFF.toInt()

        /**
         * The defaults before 3.0, for a phone that has ALREADY saved its personalization once.
         *
         * `savePersonalization` stores the palette but not every colour key (dynamic colour is only
         * written when its own switch is flipped), so a user who saved a font, a card radius or a splash
         * style has a stored palette and gaps elsewhere. Those gaps used to mean wallpaper colours,
         * system mode and the M3 purple seed; filling them with the new RemEx defaults would silently
         * change a theme the user already lives with, or worse, mix the two into the static fallback
         * scheme. A stored palette is the marker: with it, gaps keep these values; without it, the
         * phone has never chosen colours and gets the RemEx defaults above.
         */
        object Legacy {
                const val THEME_MODE = "system"
                const val THEME_STYLE = "tonal_spot"
                const val THEME_SEED_COLOR = "#6750A4"
                const val THEME_SEED_CHROMA = 48.0f
                const val THEME_CONTRAST = 0.0f
                const val DYNAMIC_COLOR = true
        }
}
