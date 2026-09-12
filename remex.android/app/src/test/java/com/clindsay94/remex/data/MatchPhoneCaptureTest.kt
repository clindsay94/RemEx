package com.clindsay94.remex.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

/**
 * The other end of remex.desktop.tests' MatchPhoneParityTests (RemEx-4kv0g.13): the phone's real sender path,
 * for the settings Connor's phone was on when the tuple was captured, produces exactly the payload the PC test embeds.
 * Change one side and the other fails.
 *
 * Resolves the seed through [ThemeSyncSeedResolver.applyChroma] + [ThemeSync.toHexRgb] rather than
 * [ThemeSyncSeedResolver.staticSeedHex]/`customSeedHex` directly: those go through
 * `android.graphics.Color.parseColor`, which `ThemeSyncSeedResolverTest`'s own class doc documents as an
 * Android-framework stub in this plain JVM unit test (no Robolectric) that silently returns 0 instead of
 * parsing — so a direct call cannot tell "#0061A4" from a parse failure. `applyChroma` is the pure half of
 * that same substitution and is what every other test in this package exercises for exactly this reason.
 */
class MatchPhoneCaptureTest {
    @Test
    fun `the captured tuple is what the phone sends for Fidelity, dark, contrast -0,5, custom #0061A4`() {
        // 48.33836572079725 is Hct.fromInt(0xFF0061A4).chroma exactly (remex.core's Hct, same MCU port) - the
        // stored seed's OWN chroma, so applyChroma's hue/tone-preserving substitution is a no-op and the
        // resolved seed re-solves to #0061A4 bit-for-bit rather than merely close to it.
        val snapshot = ThemeSnapshot(
            themeMode = "dark", themePalette = "custom", themeStyle = "fidelity",
            themeSeedColor = "#0061A4", themeSeedChroma = 48.33836572079725f, themeContrast = -0.5f, dynamicColor = false,
        )
        val baseArgb = 0xFF0061A4.toInt()
        val seedHex = ThemeSync.toHexRgb(ThemeSyncSeedResolver.applyChroma(baseArgb, snapshot.themeSeedChroma))
        val payload = JSONObject(ThemeSync.buildEnvelope(snapshot, seedHex, 1_757_700_000_000L)).getJSONObject("themeSync")

        assertEquals("#0061A4", payload.getString("seed"))
        assertEquals("fidelity", payload.getString("style"))
        assertEquals("dark", payload.getString("mode"))
        assertEquals(-0.5, payload.getDouble("contrast"), 0.0)
        assertFalse(payload.getBoolean("dynamic"))
        assertEquals(1_757_700_000_000L, payload.getLong("sentAtUnixMs"))
    }
}
