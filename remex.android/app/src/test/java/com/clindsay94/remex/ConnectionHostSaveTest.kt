package com.clindsay94.remex

import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.mutablePreferencesOf
import com.clindsay94.remex.data.SettingsManager
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Connecting to a Known PC, from Home, or from a QR code writes the host and port and NOTHING that
 * belongs to Wake-on-LAN (3.0 comb, mac-leak; RemEx-pp4cm.3).
 *
 * THE BUG: those paths re-saved the address form's MAC, broadcast and subnet next to the new host.
 * That stamped PC A's manual MAC as PC B's (mac_manual_host = B), which undid RemEx-263f so Wake on PC
 * B woke PC A; and on a fresh process, where the form had not loaded, it wiped a manual MAC and reset
 * the broadcast and subnet to defaults.
 */
class ConnectionHostSaveTest {

    private fun seeded() =
        mutablePreferencesOf(
            SettingsManager.HOST_KEY to "pc-a",
            SettingsManager.PORT_KEY to 5005,
            SettingsManager.MAC_KEY to "AA:AA:AA:AA:AA:AA",
            SettingsManager.MAC_MANUAL_HOST_KEY to "pc-a",
            SettingsManager.BROADCAST_IP_KEY to "192.168.1.255",
            SettingsManager.SUBNET_MASK_KEY to "255.255.255.0",
        )

    @Test
    fun `a host-only save moves the host and leaves the wake fields alone`() {
        val prefs = seeded()
        SettingsManager.writeConnectionHost(prefs, "pc-b", 6006)

        assertEquals("pc-b", prefs[SettingsManager.HOST_KEY])
        assertEquals(6006, prefs[SettingsManager.PORT_KEY])
        assertEquals("AA:AA:AA:AA:AA:AA", prefs[SettingsManager.MAC_KEY])
        assertEquals("pc-a", prefs[SettingsManager.MAC_MANUAL_HOST_KEY])
        assertEquals("192.168.1.255", prefs[SettingsManager.BROADCAST_IP_KEY])
        assertEquals("255.255.255.0", prefs[SettingsManager.SUBNET_MASK_KEY])
    }

    @Test
    fun `after a host-only save the manual mac no longer applies to the new pc`() {
        val prefs = seeded()
        SettingsManager.writeConnectionHost(prefs, "pc-b", 5005)
        assertEquals(
            "BB:BB:BB:BB:BB:BB",
            SettingsManager.resolveMacAddress(
                manual = prefs[SettingsManager.MAC_KEY].orEmpty(),
                manualHost = prefs[SettingsManager.MAC_MANUAL_HOST_KEY].orEmpty(),
                hostReported = "BB:BB:BB:BB:BB:BB",
                currentHost = prefs[SettingsManager.HOST_KEY].orEmpty()))
    }

    @Test
    fun `the address form save still records every field with the host it belongs to`() {
        val prefs: Preferences =
            mutablePreferencesOf().also {
                SettingsManager.writeConnectionSettings(
                    it, "pc-c", 5005, "CC:CC:CC:CC:CC:CC", "10.0.0.255", "255.0.0.0")
            }
        assertEquals("pc-c", prefs[SettingsManager.HOST_KEY])
        assertEquals("CC:CC:CC:CC:CC:CC", prefs[SettingsManager.MAC_KEY])
        assertEquals("pc-c", prefs[SettingsManager.MAC_MANUAL_HOST_KEY])
        assertEquals("10.0.0.255", prefs[SettingsManager.BROADCAST_IP_KEY])
        assertEquals("255.0.0.0", prefs[SettingsManager.SUBNET_MASK_KEY])
    }
}
