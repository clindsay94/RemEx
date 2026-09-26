package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.RemoteDesktopConfigState
import com.clindsay94.remex.ui.screens.putDesktopStreamSettings
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The stream-setting half of `desktop_config` (live-check D3). The key names must match the core's
 * `DesktopConfig` `[JsonPropertyName]`s; `adaptiveScale` in particular was never sent before, so the
 * host's adaptive scale (P3-38) could not be reached from the phone at all.
 */
class DesktopStreamSettingsJsonTest {

    private fun settings(state: RemoteDesktopConfigState) =
            JSONObject().also { putDesktopStreamSettings(it, state) }

    @Test
    fun `adaptive scale defaults off and is sent explicitly`() {
        val json = settings(RemoteDesktopConfigState())
        assertTrue(json.has("adaptiveScale"))
        assertFalse(json.getBoolean("adaptiveScale"))
    }

    @Test
    fun `adaptive scale on is sent as true`() {
        assertTrue(settings(RemoteDesktopConfigState(adaptiveScale = true)).getBoolean("adaptiveScale"))
    }

    @Test
    fun `quality scale fps and codec keep their wire names`() {
        val json =
                settings(
                        RemoteDesktopConfigState(
                                quality = 70,
                                targetFps = 60,
                                scale = 0.75f,
                                codec = "H264"
                        )
                )
        assertEquals(70, json.getInt("quality"))
        assertEquals(60, json.getInt("targetFps"))
        assertEquals(0.75, json.getDouble("scale"), 1e-6)
        assertEquals("H264", json.getString("codec"))
    }
}
