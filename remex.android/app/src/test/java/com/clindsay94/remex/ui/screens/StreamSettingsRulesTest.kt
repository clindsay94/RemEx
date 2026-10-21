package com.clindsay94.remex.ui.screens

import com.clindsay94.remex.data.SettingsManager
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The stream settings' one set of rules (RemEx-172ba), shared by the stream's sheet and the Desktop
 * tab's pre-stream sheet so the two cannot drift.
 */
class StreamSettingsRulesTest {

    @Test
    fun `saved defaults are clamped to what the sliders allow`() {
        val state =
                StreamSettingsRules.fromPreferences(
                        SettingsManager.RemoteDesktopPreferences(quality = 500, targetFps = 0, scale = 9f, preset = "balanced")
                )
        assertEquals(100, state.quality)
        assertEquals(1, state.targetFps)
        assertEquals(1.0f, state.scale)
        assertEquals(DesktopPreset.BALANCED, state.preset)
    }

    @Test
    fun `a preset sets quality, frame rate and scale together`() {
        val before = RemoteDesktopConfigState(quality = 10, targetFps = 10, scale = 0.25f, preset = DesktopPreset.CUSTOM)
        val after = StreamSettingsRules.withPreset(before, DesktopPreset.DATA_SAVER, 60, 30, 0.5f)
        assertEquals(RemoteDesktopConfigState(quality = 60, targetFps = 30, scale = 0.5f, preset = DesktopPreset.DATA_SAVER), after)
    }

    @Test
    fun `Custom keeps the numbers and the single-value edits clamp`() {
        val base = RemoteDesktopConfigState(quality = 70, targetFps = 90, scale = 0.75f, preset = DesktopPreset.BALANCED)
        assertEquals(base.copy(preset = DesktopPreset.CUSTOM), StreamSettingsRules.asCustom(base))
        assertEquals(1, StreamSettingsRules.withQuality(base, -4).quality)
        assertEquals(DESKTOP_MAX_FPS, StreamSettingsRules.withTargetFps(base, 10_000).targetFps)
        assertEquals(0.25f, StreamSettingsRules.withScale(base, 0f).scale)
    }

    @Test
    fun `both sheets render the same sections, and the stream keeps display and window controls`() {
        val stream = File("src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt").readText()
        val tab = File("src/main/java/com/clindsay94/remex/ui/screens/DesktopTabScreen.kt").readText()
        assertTrue(stream.contains("StreamSettingsSections("))
        assertTrue(tab.contains("StreamSettingsSections("))
        // Before a stream there is no display catalogue and no window list to offer.
        assertTrue(stream.contains("remote_desktop_display_label"))
        assertTrue(stream.contains("remote_desktop_window_controls_header"))
        assertFalse(tab.contains("remote_desktop_display_label"))
        assertFalse(tab.contains("remote_desktop_window_controls_header"))
        // The pre-stream sheet never builds the stream's own view model.
        assertFalse(tab.contains("RemoteDesktopViewModel"))
    }
}
