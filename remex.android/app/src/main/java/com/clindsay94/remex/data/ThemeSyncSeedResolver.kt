package com.clindsay94.remex.data

import android.annotation.SuppressLint
import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.util.Log
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.ui.graphics.toArgb
import com.google.android.material.color.utilities.Hct

private const val TAG = "ThemeSyncSeedResolver"

/** Stored-seed fallback when a "#RRGGBB" cannot be parsed — the same literal Theme.kt falls back to. */
private const val FallbackSeedHex = "#6750A4"

/**
 * `Theme.kt`'s `BrandSeed` (`Color(0xFFFFB63D)`), masked to `#RRGGBB`. Not imported directly:
 * `Theme.kt`'s `BrandSeed` is `private`, and duplicating the literal here — rather than widening
 * that visibility for one caller — keeps `theme_sync` from becoming a reason to touch the theming
 * file itself. If the brand seed ever changes, `FallbackSchemeBrandHueTest` (Theme.kt's own guard)
 * and this constant both need updating; there is no way to make the compiler enforce that link.
 */
private const val BrandSeedHex = "#FFB63D"

/**
 * Resolves the seed [ThemeSync] actually sends: the seed [com.clindsay94.remex.ui.theme.RemExTheme]
 * is ACTUALLY PAINTING FROM right now, not merely what is stored (RemEx-y06a0.1).
 *
 * **MUST MATCH WHAT THE PHONE PAINTS WITH**, because the PC applies this verbatim as the new seed
 * when the user presses "Match my phone" (RemEx-sudp8). `RemExTheme`'s own `colorScheme` `when`
 * (Theme.kt:525-544) has three arms, and this mirrors all three — sending the raw stored seed for
 * every non-dynamic case was wrong for two of them, caught in review:
 *
 * 1. Dynamic color active → the wallpaper-derived scheme's primary ([dynamicPrimaryHex]).
 * 2. `themePalette == "custom"` → the stored seed after the SAME Hct chroma substitution
 *    `RemExTheme`'s `seedColor` `remember` block applies (Theme.kt:511-519) — a user-adjusted
 *    [ThemeSnapshot.themeSeedChroma] changes what is actually painted even though
 *    [ThemeSnapshot.themeSeedColor] itself did not, so skipping this step sent a color the phone
 *    was not showing. Carried inside the resolved seed rather than as a new wire field, so the
 *    payload shape stays exactly what the contract already specifies.
 * 3. Otherwise (`"default"` palette, dynamic off) → `Theme.kt`'s static `BrandSeed`
 *    (Theme.kt:68,75-77), NOT the stored seed — a fresh install or a device with dynamic color
 *    turned off paints `DarkColorScheme`/`LightColorScheme`, both seeded from `BrandSeed`, and the
 *    stored seed value in that state describes a custom palette the phone is not using.
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
                        return staticSeedHex(snapshot)
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
                return resolved ?: staticSeedHex(snapshot)
        }

        /**
         * The two non-dynamic arms — pure and JVM-testable without a `Context`, matching
         * [MediaSeekReconciler]'s split. Mirrors `RemExTheme`'s `seedColor` `remember` block
         * exactly (Theme.kt:511-519): a custom palette applies the chroma substitution; anything
         * else paints the app's `BrandSeed`, never the raw stored seed.
         */
        internal fun staticSeedHex(snapshot: ThemeSnapshot): String =
                if (snapshot.themePalette.equals("custom", ignoreCase = true)) {
                        customSeedHex(snapshot.themeSeedColor, snapshot.themeSeedChroma)
                } else {
                        BrandSeedHex
                }

        /**
         * `android.graphics.Color.parseColor` is the ONLY non-JVM-testable step here — it is an
         * Android framework stub in a plain unit test (no Robolectric in this project; see
         * `CertRepairPromptControllerTest`'s KDoc) and silently returns 0 there instead of parsing
         * or throwing, so a bad hex string cannot be distinguished from black in that environment.
         * [applyChroma] below is deliberately split out of this function so the Hct substitution
         * itself — the part the review asked to see tested with a chroma-adjusted seed — can be
         * exercised with a hand-built ARGB int, the same way [FallbackSchemeBrandHueTest] tests
         * Hct-derived colour without Robolectric.
         */
        private fun customSeedHex(themeSeedColor: String, themeSeedChroma: Float): String =
                runCatching { ThemeSync.toHexRgb(applyChroma(Color.parseColor(themeSeedColor), themeSeedChroma)) }
                        .getOrDefault(FallbackSeedHex)

        /**
         * `Hct.from(baseHct.hue, chroma, baseHct.tone)` — the EXACT substitution `RemExTheme`
         * applies (Theme.kt:511-519): hue and tone come from the stored seed color, chroma comes
         * from the separate slider value. `@SuppressLint("RestrictedApi")` for the same reason as
         * Theme.kt's own Hct calls (RemEx-cljx) — no public equivalent.
         */
        @SuppressLint("RestrictedApi")
        internal fun applyChroma(baseArgb: Int, chroma: Float): Int {
                val baseHct = Hct.fromInt(baseArgb)
                return Hct.from(baseHct.hue, chroma.toDouble(), baseHct.tone).toInt()
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
}
