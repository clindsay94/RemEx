package com.clindsay94.remex

import com.clindsay94.remex.data.SettingsExport
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins what may leave the phone in a settings export (RemEx-gtfn).
 *
 * An export is a file the user mails to themselves, syncs to a cloud drive, or hands to whoever is
 * helping them. The same shape of accident as the diagnostics bundle: the user is being helpful,
 * and nobody reads a JSON file before sending it.
 */
class SettingsExportTest {

    @Test
    fun `a key nobody whitelisted cannot be exported`() {
        // THE TEST THIS CLASS EXISTS FOR, and the reason the filter is a whitelist. A blacklist
        // would export this by default and rely on someone having remembered to exclude it - which
        // is precisely the memory that fails. Here an unknown key falls out for free.
        val snapshot = mapOf(
            "theme_mode" to "dark",
            "some_preference_added_next_year" to "whatever it turns out to hold"
        )

        val exported = SettingsExport.filterForExport(snapshot)

        assertEquals(mapOf("theme_mode" to "dark"), exported)
    }

    @Test
    fun `a secret smuggled into the snapshot is not exported`() {
        // The structural guarantee stated as an attack. Even if security material somehow reached
        // the settings store - it does not, it lives in separate DataStores - the filter would
        // still refuse it, because refusal is the default rather than the exception.
        val snapshot = mapOf(
            "theme_mode" to "dark",
            "reconnect_secret" to "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M=",
            "remex_pinned_hosts" to "sha256/abc123",
            "spki_pin" to "sha256/def456"
        )

        val exported = SettingsExport.filterForExport(snapshot, includeHostAddress = true)

        assertEquals(setOf("theme_mode"), exported.keys)
    }

    @Test
    fun `no security-sounding key is on the whitelist at all`() {
        // Guards the list itself rather than the filter. A future edit that adds a key with any of
        // these words in it is almost certainly a mistake, and this is where it gets caught.
        val forbidden = listOf("secret", "spki", "pin", "token", "cert", "credential", "client_id")

        for (key in SettingsExport.ExportableKeys + SettingsExport.HostAddressKeys) {
            for (word in forbidden) {
                assertFalse("'$key' contains '$word' and should not be exportable", key.contains(word))
            }
        }
    }

    @Test
    fun `the PC's address and MAC are withheld unless the user asks`() {
        // An export carries a theme to a new phone. Handing over where the user's PC lives, and its
        // hardware identifier, should not be a side effect of that.
        val snapshot = mapOf(
            "theme_mode" to "dark",
            "host" to "192.168.1.50",
            "port" to "5005",
            "mac_address" to "9C:6B:00:9B:1B:D2",
            "broadcast_ip" to "192.168.1.255"
        )

        val default = SettingsExport.filterForExport(snapshot)
        assertEquals(setOf("theme_mode"), default.keys)

        val optedIn = SettingsExport.filterForExport(snapshot, includeHostAddress = true)
        assertTrue(optedIn.containsKey("host"))
        assertTrue(optedIn.containsKey("mac_address"))
    }

    @Test
    fun `the settings a user actually spent time on do survive`() {
        // The counterpart that keeps the feature worth having. An export that carries nothing is as
        // useless as one that carries too much, and remote-desktop tuning is the part people fiddle
        // with for real.
        val snapshot = mapOf(
            "theme_seed_color" to "#00F3FF",
            "font_scale" to "1.15",
            "home_layout_json" to "[{\"id\":\"telemetry\"}]",
            "desktop_quality" to "high",
            "desktop_target_fps" to "60",
            "vertical_scroll_sensitivity" to "1.4"
        )

        val exported = SettingsExport.filterForExport(snapshot)

        assertEquals(snapshot, exported)
    }

    @Test
    fun `device-scoped state is withheld so an import cannot lie to a fresh install`() {
        // These are not secrets, and that is why they are easy to export by accident. Importing
        // them would tell a phone that has never been set up that it already has been - skipping
        // onboarding, silencing a warning it never saw, and marking a migration that never ran.
        val snapshot = mapOf(
            "has_completed_onboarding" to "true",
            "dashboard_coach_seen" to "true",
            "desktop_unlimited_warning_shown" to "true",
            "shape_defaults_migrated_v3" to "true",
            "client_id" to "device-identity-guid",
            "full_browse_root_uri" to "content://com.android.externalstorage/tree/primary%3A"
        )

        assertTrue(SettingsExport.filterForExport(snapshot, includeHostAddress = true).isEmpty())
    }

    @Test
    fun `every excluded key carries a written reason`() {
        // So the next reader does not "fix" an omission that is doing a job. An undocumented
        // exclusion looks exactly like an oversight.
        for ((key, reason) in SettingsExport.ExcludedWithReason) {
            assertFalse("$key is documented as excluded but is on the whitelist",
                key in SettingsExport.ExportableKeys)
            assertTrue("$key has an empty reason", reason.isNotBlank())
        }
    }

    @Test
    fun `isExportable agrees with the filter`() {
        // Two entry points, one rule. An importer validating key-by-key must reach the same verdict
        // as the exporter that wrote the file, or a round trip drops data on one side only.
        val everyKey = SettingsExport.ExportableKeys + SettingsExport.HostAddressKeys +
            SettingsExport.ExcludedWithReason.keys + setOf("unknown_future_key")

        for (opt in listOf(false, true)) {
            val snapshot = everyKey.associateWith { "v" }
            val filtered = SettingsExport.filterForExport(snapshot, includeHostAddress = opt).keys

            for (key in everyKey) {
                assertEquals("disagreement on '$key' with includeHostAddress=$opt",
                    SettingsExport.isExportable(key, opt), key in filtered)
            }
        }
    }

    @Test
    fun `an empty snapshot exports nothing rather than failing`() {
        assertTrue(SettingsExport.filterForExport(emptyMap()).isEmpty())
    }
}
