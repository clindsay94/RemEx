package com.clindsay94.remex.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the `theme_sync` wire shape (RemEx-y06a0.1) so the PC join side (RemEx-sudp8), built
 * against the same contract in parallel, can rely on field names and value shapes not drifting.
 */
class ThemeSyncPayloadTest {

        private fun snapshot(
                themeMode: String = "system",
                themePalette: String = "default",
                themeStyle: String = "tonal_spot",
                themeSeedColor: String = "#6750A4",
                themeContrast: Float = 0.0f,
                dynamicColor: Boolean = true
        ) = ThemeSnapshot(
                themeMode = themeMode,
                themePalette = themePalette,
                themeStyle = themeStyle,
                themeSeedColor = themeSeedColor,
                themeContrast = themeContrast,
                dynamicColor = dynamicColor
        )

        @Test
        fun `envelope carries type theme_sync and a nested themeSync object`() {
                val envelope = JSONObject(ThemeSync.buildEnvelope(snapshot(), "#AABBCC", 1_700_000_000_000L))

                assertEquals("theme_sync", envelope.getString("type"))
                assertTrue("expected a themeSync payload object", envelope.has("themeSync"))
        }

        @Test
        fun `payload has exactly the six contract fields, all present`() {
                val payload =
                        JSONObject(ThemeSync.buildEnvelope(snapshot(), "#AABBCC", 42L))
                                .getJSONObject("themeSync")

                val expectedKeys = setOf("seed", "style", "mode", "contrast", "dynamic", "sentAtUnixMs")
                assertEquals(expectedKeys, payload.keys().asSequence().toSet())
        }

        @Test
        fun `style and mode travel verbatim, never translated`() {
                val payload =
                        JSONObject(
                                        ThemeSync.buildEnvelope(
                                                snapshot(themeMode = "dark", themeStyle = "fruit_salad"),
                                                "#AABBCC",
                                                0L
                                        )
                                )
                                .getJSONObject("themeSync")

                assertEquals("fruit_salad", payload.getString("style"))
                assertEquals("dark", payload.getString("mode"))
        }

        @Test
        fun `seed is the resolved hex handed in, unmodified`() {
                val payload =
                        JSONObject(ThemeSync.buildEnvelope(snapshot(), "#123ABC", 0L)).getJSONObject("themeSync")
                assertEquals("#123ABC", payload.getString("seed"))
        }

        @Test
        fun `contrast round-trips the full -1 to 1 range unclamped`() {
                for (value in listOf(-1.0f, -0.5f, 0.0f, 0.5f, 1.0f)) {
                        val payload =
                                JSONObject(
                                                ThemeSync.buildEnvelope(
                                                        snapshot(themeContrast = value),
                                                        "#AABBCC",
                                                        0L
                                                )
                                        )
                                        .getJSONObject("themeSync")
                        assertEquals(value.toDouble(), payload.getDouble("contrast"), 1e-6)
                }
        }

        @Test
        fun `sentAtUnixMs is exactly the clock value supplied`() {
                val payload =
                        JSONObject(ThemeSync.buildEnvelope(snapshot(), "#AABBCC", 1_735_689_600_123L))
                                .getJSONObject("themeSync")
                assertEquals(1_735_689_600_123L, payload.getLong("sentAtUnixMs"))
        }

        @Test
        fun `toHexRgb formats upper-case RRGGBB and drops alpha`() {
                assertEquals("#6750A4", ThemeSync.toHexRgb(0xFF6750A4.toInt()))
                assertEquals("#00FF00", ThemeSync.toHexRgb(0x8000FF00.toInt()))
        }

        @Test
        fun `dynamic is true only on the default palette with the toggle on`() {
                assertTrue(ThemeSync.isDynamicActive(snapshot(themePalette = "default", dynamicColor = true)))
        }

        @Test
        fun `dynamic is false when the toggle is off`() {
                assertFalse(ThemeSync.isDynamicActive(snapshot(themePalette = "default", dynamicColor = false)))
        }

        @Test
        fun `dynamic is false on a custom palette even if the toggle is still on`() {
                // Mirrors RemExTheme's branch order (Theme.kt): a custom seed always wins over the
                // dynamic-color flag, which the UI leaves disabled-but-not-cleared while custom is active.
                assertFalse(ThemeSync.isDynamicActive(snapshot(themePalette = "custom", dynamicColor = true)))
        }

        @Test
        fun `toThemeSnapshot carries only the six theme fields off PersonalizationPreferences`() {
                val prefs =
                        SettingsManager.PersonalizationPreferences(
                                themeMode = "light",
                                themePalette = "custom",
                                themeStyle = "vibrant",
                                themeSeedColor = "#112233",
                                themeContrast = 0.5f,
                                dynamicColor = false
                        )

                val snapshot = prefs.toThemeSnapshot()

                assertEquals("light", snapshot.themeMode)
                assertEquals("custom", snapshot.themePalette)
                assertEquals("vibrant", snapshot.themeStyle)
                assertEquals("#112233", snapshot.themeSeedColor)
                assertEquals(0.5f, snapshot.themeContrast)
                assertFalse(snapshot.dynamicColor)
        }
}
