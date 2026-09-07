package com.clindsay94.remex.data

import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.util.Log
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.ui.graphics.toArgb

private const val TAG = "ThemeSyncSeedResolver"

/** Stored-seed fallback when a "#RRGGBB" cannot be parsed — the same literal Theme.kt falls back to. */
private const val FallbackSeedHex = "#6750A4"

/**
 * Resolves the seed [ThemeSync] actually sends: the wallpaper-derived dynamic scheme's primary
 * when dynamic color is active, otherwise the stored seed (RemEx-y06a0.1).
 *
 * **MUST MATCH WHAT THE PHONE PAINTS WITH.** The PC applies this verbatim as the new seed when the
 * user presses "Match my phone" (RemEx-sudp8), so sending the toggle's raw seed while the screen
 * is actually showing wallpaper colors would make "match" repaint the desktop into a palette that
 * looks nothing like the phone.
 *
 * No `@RequiresApi`/SDK_INT guard: minSdk is 34, so
 * [androidx.compose.material3.dynamicLightColorScheme] and its dark counterpart — API 31+ — are
 * unconditionally available, exactly as [com.clindsay94.remex.ui.theme.RemExTheme] already assumes
 * with no guard of its own. `DeadSdkGuardTest` fails the build on any `SDK_INT >=` check naming a
 * level at or below minSdk, so do not add one here.
 */
object ThemeSyncSeedResolver {

        fun resolve(context: Context, snapshot: ThemeSnapshot): String {
                if (!ThemeSync.isDynamicActive(snapshot)) {
                        return storedSeedHex(snapshot)
                }
                val resolved =
                        runCatching { dynamicPrimaryHex(context, snapshot) }
                                .onFailure {
                                        Log.w(
                                                TAG,
                                                "Reading the dynamic scheme's primary failed; falling back to the stored seed",
                                                it
                                        )
                                }
                                .getOrNull()
                return resolved ?: storedSeedHex(snapshot)
        }

        /**
         * Reads the SAME dynamic scheme [com.clindsay94.remex.ui.theme.RemExTheme] would build for
         * the current mode — not a cached Compose value, since this runs outside composition
         * wherever a connect or a settings change fires, not on a recomposition.
         */
        private fun dynamicPrimaryHex(context: Context, snapshot: ThemeSnapshot): String {
                val scheme =
                        if (isDarkTheme(context, snapshot.themeMode)) {
                                dynamicDarkColorScheme(context)
                        } else {
                                dynamicLightColorScheme(context)
                        }
                return ThemeSync.toHexRgb(scheme.primary.toArgb())
        }

        /**
         * Mirrors [com.clindsay94.remex.ui.theme.RemExTheme]'s own `darkTheme` branch exactly
         * (`"dark"` / `"light"` / else system), substituting a `Configuration` read for
         * `isSystemInDarkTheme()` since there is no composition here to ask.
         */
        private fun isDarkTheme(context: Context, themeMode: String): Boolean =
                when (themeMode.lowercase()) {
                        "dark" -> true
                        "light" -> false
                        else -> {
                                val nightBits =
                                        context.resources.configuration.uiMode and
                                                Configuration.UI_MODE_NIGHT_MASK
                                nightBits == Configuration.UI_MODE_NIGHT_YES
                        }
                }

        /** The raw stored seed, reformatted to the wire's canonical `#RRGGBB` upper-case shape. */
        private fun storedSeedHex(snapshot: ThemeSnapshot): String =
                runCatching { ThemeSync.toHexRgb(Color.parseColor(snapshot.themeSeedColor)) }
                        .getOrDefault(FallbackSeedHex)
}
