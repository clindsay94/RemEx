package com.clindsay94.remex.data

/**
 * Decides what may leave the phone in a settings export (RemEx-gtfn).
 *
 * **THIS IS A WHITELIST, AND THAT IS THE ENTIRE SECURITY DESIGN.** A blacklist would export every
 * preference by default and rely on someone remembering to exclude each new sensitive one — and the
 * one they forget is, by definition, the one that mattered. With a whitelist a forgotten preference
 * is merely *missing* from an export, which the user notices and which loses nobody anything. The
 * failure modes are not comparable, so the direction is not a matter of taste.
 *
 * **What is excluded by construction rather than by rule:** the pinned host keys and reconnect
 * secrets live in their own DataStores (`remex_pinned_hosts`, `remex_pinned_host_keyset`,
 * `remex_reconnect_secrets`), not in the settings store this exports from. So they cannot appear
 * here even if the filter were wrong. That is deliberate defence in depth, not an accident worth
 * relying on alone — [ExportableKeys] is still the thing that decides.
 */
object SettingsExport {

    /**
     * Every preference key that may appear in an export.
     *
     * Adding a key here is a decision to put it in a file the user will mail to themselves, sync to
     * a cloud drive, or hand to someone helping them. Ask what it reveals before adding it.
     */
    val ExportableKeys: Set<String> = setOf(
        // Appearance — the bulk of what a user would actually want to carry to a new phone.
        "theme_mode", "theme_palette", "theme_style", "theme_seed_color", "theme_seed_chroma",
        "theme_contrast", "dynamic_color", "font_family", "font_scale", "splash_style",
        "card_corner_radius", "card_opacity",
        "app_launcher_card_shape_preset_v2", "pc_card_shape_preset_v2",
        "remote_control_card_shape_preset_v2", "remote_desktop_card_shape_preset_v2",
        "remote_mouse_card_shape_preset_v2", "task_manager_card_shape_preset_v2",
        "telemetry_card_shape_preset_v2",

        // Layout.
        "home_layout_json", "home_enabled_cards_json",

        // Remote desktop tuning — the settings a user spends real time getting right.
        "desktop_quality", "desktop_preset", "desktop_scale", "desktop_target_fps",
        "desktop_display_target", "desktop_cursor_scale", "desktop_direct_touch",
        "desktop_pointer_speed",
        "horizontal_scroll_sensitivity", "vertical_scroll_sensitivity",
        "mouse_fab_x", "mouse_fab_y",

        // Widget behaviour.
        "widget_telemetry_poll_seconds"
    )

    /**
     * Keys deliberately NOT exported, with the reason, so a future reader does not "fix" an
     * omission that is doing a job.
     */
    val ExcludedWithReason: Map<String, String> = mapOf(
        "client_id" to
            "This phone's identity to the host. Importing it onto a second device would give two " +
            "phones the same identity, which is a pairing problem, not a preference.",
        "mac_address" to
            "A stable hardware identifier for the user's PC. Opt-in only - see includeHostAddress.",
        "host_mac_address" to
            "Same as mac_address.",
        "host" to
            "Network location of the user's PC. Opt-in only - see includeHostAddress.",
        "port" to
            "Same as host.",
        "broadcast_ip" to
            "Reveals the user's subnet. Opt-in only - see includeHostAddress.",
        "subnet_mask" to
            "Same as broadcast_ip.",
        "full_browse_root_uri" to
            "A SAF permission grant tied to this device; it means nothing on another one, and it " +
            "names a directory on the user's PC.",
        "has_completed_onboarding" to
            "Importing this would skip onboarding on a phone that has never been set up.",
        "dashboard_coach_seen" to
            "Same as has_completed_onboarding.",
        "desktop_unlimited_warning_shown" to
            "Suppresses a warning. Importing it would silence a warning the new device never saw.",
        "shape_defaults_migrated_v2" to
            "A migration marker. Importing it would convince the app a migration had already run.",
        "shape_defaults_migrated_v3" to
            "Same as shape_defaults_migrated_v2."
    )

    /**
     * Network identifiers, released only when the user asks for them explicitly.
     *
     * Separated from [ExportableKeys] rather than merged on a flag, so the default set can be read
     * at a glance without tracing a boolean.
     */
    val HostAddressKeys: Set<String> = setOf(
        "host", "port", "mac_address", "host_mac_address", "broadcast_ip", "subnet_mask"
    )

    /**
     * Filters a full preference snapshot down to what may be written to an export file.
     *
     * @param includeHostAddress Whether to also include [HostAddressKeys]. Off by default: an
     *   export is a file that leaves the device, and the user's PC address and MAC are not
     *   something to hand over as a side effect of carrying their theme to a new phone.
     */
    fun filterForExport(
        allPreferences: Map<String, String>,
        includeHostAddress: Boolean = false
    ): Map<String, String> {
        val permitted = if (includeHostAddress) ExportableKeys + HostAddressKeys else ExportableKeys

        // Filtering the INPUT against the whitelist, rather than reading the whitelist out of the
        // input, is what makes an unknown key impossible to export: a preference added tomorrow is
        // simply not in `permitted` and falls out here without anyone having to notice it exists.
        return allPreferences.filterKeys { it in permitted }
    }

    /**
     * True when a key is safe to write to an export file under the given option.
     *
     * Exposed so an importer can reject a file that contains anything it should not, rather than
     * trusting that whatever wrote it used the same rules.
     */
    fun isExportable(key: String, includeHostAddress: Boolean = false): Boolean =
        key in ExportableKeys || (includeHostAddress && key in HostAddressKeys)
}
