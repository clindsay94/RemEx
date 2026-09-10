package com.clindsay94.remex.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * The two non-dynamic arms of [ThemeSyncSeedResolver] — pure and JVM-testable without a
 * `Context`, per review on RemEx-y06a0.1. The dynamic-active arm needs the real
 * `dynamicLightColorScheme`/`dynamicDarkColorScheme` APIs and is not exercised here.
 *
 * [ThemeSyncSeedResolver.applyChroma], not `staticSeedHex`/`customSeedHex` directly, is what the
 * chroma-substitution assertions below call: `customSeedHex` also calls
 * `android.graphics.Color.parseColor`, an Android framework stub in a plain unit test (no
 * Robolectric here — see `CertRepairPromptControllerTest`'s KDoc) that silently returns 0 instead
 * of parsing, so a test going through it cannot tell a real color from a parse failure. `applyChroma`
 * takes an already-parsed ARGB int, the same way [FallbackSchemeBrandHueTest] exercises
 * Hct-derived colour without Robolectric.
 */
class ThemeSyncSeedResolverTest {

        private fun snapshot(
                themePalette: String,
                themeSeedColor: String = "#6750A4",
                themeSeedChroma: Float = 48.0f
        ) = ThemeSnapshot(
                themeMode = "system",
                themePalette = themePalette,
                themeStyle = "tonal_spot",
                themeSeedColor = themeSeedColor,
                themeSeedChroma = themeSeedChroma,
                themeContrast = 0.0f,
                dynamicColor = false
        )

        @Test
        fun `default palette resolves to the app's BrandSeed, never the stored seed`() {
                // RemExTheme's static fallback (Theme.kt DarkColorScheme/LightColorScheme) is seeded
                // from BrandSeed, not from whatever happens to be stored under a "default" palette —
                // review finding: the resolver used to send the stored seed here, which the phone was
                // not actually painting with.
                val seed = ThemeSyncSeedResolver.staticSeedHex(snapshot(themePalette = "default", themeSeedColor = "#123456"))
                assertEquals("#FFB63D", seed)
        }

        @Test
        fun `default palette ignores the seed color entirely`() {
                val withOneStoredSeed =
                        ThemeSyncSeedResolver.staticSeedHex(snapshot(themePalette = "default", themeSeedColor = "#000000"))
                val withAnotherStoredSeed =
                        ThemeSyncSeedResolver.staticSeedHex(snapshot(themePalette = "default", themeSeedColor = "#FFFFFF"))

                assertEquals(withOneStoredSeed, withAnotherStoredSeed)
        }

        @Test
        fun `a chroma-adjusted custom seed differs from a different chroma on the same base color`() {
                // A saturated, mid-tone red so neither chroma value collapses at a tone extreme
                // (pure black/white cannot show chroma at all, which is exactly the trap
                // Color.parseColor's unit-test stub — see class doc — would otherwise hide).
                val baseArgb = 0xFFB33A3A.toInt()

                val lowChroma = ThemeSyncSeedResolver.applyChroma(baseArgb, chroma = 10.0f)
                val highChroma = ThemeSyncSeedResolver.applyChroma(baseArgb, chroma = 100.0f)

                // The point of the fix: a user-adjusted chroma slider changes what the phone paints
                // even though themeSeedColor itself did not change, so it must change what is sent.
                assertNotEquals(lowChroma, highChroma)
        }

        @Test
        fun `applyChroma preserves hue and tone, only chroma moves`() {
                val baseArgb = 0xFF2A6EBB.toInt()
                val baseHct = com.google.android.material.color.utilities.Hct.fromInt(baseArgb)

                val resultHct =
                        com.google.android.material.color.utilities.Hct.fromInt(
                                ThemeSyncSeedResolver.applyChroma(baseArgb, chroma = 30.0f)
                        )

                assertEquals(baseHct.hue, resultHct.hue, 0.5)
                assertEquals(baseHct.tone, resultHct.tone, 0.5)
        }
}
