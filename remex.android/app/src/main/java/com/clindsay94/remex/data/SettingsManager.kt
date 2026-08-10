package com.clindsay94.remex.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.floatPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.clindsay94.remex.ui.screens.DashboardShapes
import java.util.UUID
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsManager(val context: Context) {

        companion object {
                /**
                 * Which MAC Wake-on-LAN should use for the PC currently selected (RemEx-263f).
                 *
                 * **A MANUAL MAC IS ONLY PREFERRED FOR THE PC IT WAS ENTERED FOR.** The MAC,
                 * broadcast address and subnet are stored once, globally, while tap-to-connect on a
                 * Known PC changes only the host - so a MAC typed for PC A was still broadcast after
                 * switching to PC B. That wakes the wrong machine, and it does so silently: nothing on
                 * the phone looks wrong, and the machine that woke is not the one being looked at. The
                 * typed-address path at least showed a MAC field to notice; tap-to-connect shows none.
                 *
                 * **A BLANK [manualHost] STILL PREFERS THE MANUAL MAC, DELIBERATELY.** That is the
                 * pre-RemEx-263f data of anyone upgrading, and the alternative - treating unknown as
                 * mismatched - would silently discard a MAC someone set on purpose for a different NIC
                 * or a router quirk, which the manual slot exists to respect. Those installs heal the
                 * next time settings are saved, since the host is recorded in the same edit.
                 */
                internal fun resolveMacAddress(
                        manual: String,
                        manualHost: String,
                        hostReported: String,
                        currentHost: String,
                ): String {
                        val manualAppliesHere =
                                manual.isNotBlank() &&
                                        (manualHost.isBlank() || manualHost == currentHost)
                        return if (manualAppliesHere) manual else hostReported
                }

                val HOST_KEY = stringPreferencesKey("host")
                val PORT_KEY = intPreferencesKey("port")
                val MAC_KEY = stringPreferencesKey("mac_address")
                // The MAC the HOST reported in host_info, kept apart from the one the user typed
                // so auto-discovery can never overwrite a manual entry (RemEx-izuj).
                val HOST_MAC_KEY = stringPreferencesKey("host_mac_address")
                // The host the MANUAL mac was entered for (RemEx-263f). MAC, broadcast and subnet
                // are stored globally, but tap-to-connect on a Known PC changes only the host - so a
                // manual MAC entered for PC A was still being broadcast after switching to PC B,
                // silently waking the wrong machine with nothing on screen to notice.
                val MAC_MANUAL_HOST_KEY = stringPreferencesKey("mac_manual_host")
                val BROADCAST_IP_KEY = stringPreferencesKey("broadcast_ip")
                val SUBNET_MASK_KEY = stringPreferencesKey("subnet_mask")

                val DESKTOP_QUALITY_KEY = intPreferencesKey("desktop_quality")
                val DESKTOP_TARGET_FPS_KEY = intPreferencesKey("desktop_target_fps")
                val DESKTOP_SCALE_KEY = floatPreferencesKey("desktop_scale")
                // Which named quality preset (Unlimited/Smooth & Sharp/Balanced/Data Saver/Custom)
                // last produced the quality/fps/scale trio above. See DesktopPreset (RemEx-vj31).
                val DESKTOP_PRESET_KEY = stringPreferencesKey("desktop_preset")
                val DESKTOP_UNLIMITED_WARNING_SHOWN_KEY =
                        booleanPreferencesKey("desktop_unlimited_warning_shown")
                val DESKTOP_DIRECT_TOUCH_KEY = booleanPreferencesKey("desktop_direct_touch")
                val DESKTOP_POINTER_SPEED_KEY = floatPreferencesKey("desktop_pointer_speed")
                val DESKTOP_CURSOR_SCALE_KEY = floatPreferencesKey("desktop_cursor_scale")
                // Persisted display-target selection token: "" (primary/default), "virtual"
                // (both screens combined), or "monitorkey:<persistentDisplayKey>" for a specific
                // monitor. NOT "monitor:<displayId>" - that was the pre-RemEx-ynur format and is
                // session-scoped, so it silently resolved to a different physical screen after a
                // replug. Old values are deliberately left unrecognised so they fall through to the
                // primary display rather than being reinterpreted.
                val DESKTOP_DISPLAY_TARGET_KEY = stringPreferencesKey("desktop_display_target")
                val VERTICAL_SCROLL_SENSITIVITY_KEY =
                        floatPreferencesKey("vertical_scroll_sensitivity")
                val HORIZONTAL_SCROLL_SENSITIVITY_KEY =
                        floatPreferencesKey("horizontal_scroll_sensitivity")
                val HAS_COMPLETED_ONBOARDING_KEY = booleanPreferencesKey("has_completed_onboarding")
                val SPLASH_STYLE_KEY = stringPreferencesKey("splash_style")
                val CLIENT_ID_KEY = stringPreferencesKey("client_id")

                // How often (seconds) the home-screen widgets actively poll the PC for fresh
                // telemetry while connected. Drives the in-process poll loop in WidgetDataCache.
                val WIDGET_TELEMETRY_POLL_SECONDS_KEY =
                        intPreferencesKey("widget_telemetry_poll_seconds")
                const val WIDGET_TELEMETRY_POLL_DEFAULT = 30
                const val WIDGET_TELEMETRY_POLL_MIN = 10
                const val WIDGET_TELEMETRY_POLL_MAX = 600

                val HOME_LAYOUT_JSON_KEY = stringPreferencesKey("home_layout_json")
                val HOME_ENABLED_CARDS_JSON_KEY = stringPreferencesKey("home_enabled_cards_json")

                val THEME_MODE_KEY = stringPreferencesKey("theme_mode")
                val THEME_PALETTE_KEY = stringPreferencesKey("theme_palette")
                val THEME_STYLE_KEY = stringPreferencesKey("theme_style")
                val FONT_FAMILY_KEY = stringPreferencesKey("font_family")
                val FONT_SCALE_KEY = floatPreferencesKey("font_scale")
                val CARD_CORNER_RADIUS_KEY = intPreferencesKey("card_corner_radius")
                val CARD_OPACITY_KEY = floatPreferencesKey("card_opacity")
                val PC_CARD_SHAPE_PRESET_KEY = floatPreferencesKey("pc_card_shape_preset_v2")
                val TELEMETRY_CARD_SHAPE_PRESET_KEY =
                        floatPreferencesKey("telemetry_card_shape_preset_v2")
                val APP_LAUNCHER_CARD_SHAPE_PRESET_KEY =
                        floatPreferencesKey("app_launcher_card_shape_preset_v2")
                val TASK_MANAGER_CARD_SHAPE_PRESET_KEY =
                        floatPreferencesKey("task_manager_card_shape_preset_v2")
                val REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY =
                        floatPreferencesKey("remote_desktop_card_shape_preset_v2")
                val REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY =
                        floatPreferencesKey("remote_control_card_shape_preset_v2")
                val REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY =
                        floatPreferencesKey("remote_mouse_card_shape_preset_v2")
                // One-time non-destructive migration off the universal clover default (decision #5).
                val SHAPE_DEFAULTS_MIGRATED_V2_KEY = booleanPreferencesKey("shape_defaults_migrated_v2")
                // A SEPARATE key, deliberately not a reuse of V2. V2 has already run on every
                // existing install, so adding the five non-home buckets to its list would
                // migrate new installs only and leave everyone else on the clover - the two
                // populations would silently disagree about the default (RemEx-nkdv).
                val SHAPE_DEFAULTS_MIGRATED_V3_KEY = booleanPreferencesKey("shape_defaults_migrated_v3")
                // Per-CATEGORY shape overrides (RemEx-mycn). One key per DashboardShapes
                // .CardCategory, all defaulting to INHERIT so an untouched install resolves
                // exactly as before and no migration is needed.
                val CATEGORY_SHAPE_PRESET_KEYS: Map<DashboardShapes.CardCategory, androidx.datastore.preferences.core.Preferences.Key<Float>> =
                        DashboardShapes.CardCategory.entries.associateWith { category ->
                                floatPreferencesKey("category_shape_preset_" + category.name.lowercase())
                        }
                // First-run Home Base coach marks: true once the user has seen or dismissed the
                // dashboard coaching overlay. Reset re-arms it as first-run (RemEx-km0i.10).
                val DASHBOARD_COACH_SEEN_KEY = booleanPreferencesKey("dashboard_coach_seen")
                val THEME_SEED_COLOR_KEY = stringPreferencesKey("theme_seed_color")
                val THEME_SEED_CHROMA_KEY = floatPreferencesKey("theme_seed_chroma")
                val THEME_CONTRAST_KEY = floatPreferencesKey("theme_contrast")
                // Material You wallpaper-based color (API 31+); on by default (RemEx-9429).
                val DYNAMIC_COLOR_KEY = booleanPreferencesKey("dynamic_color")

                val MOUSE_FAB_X_KEY = floatPreferencesKey("mouse_fab_x")
                val MOUSE_FAB_Y_KEY = floatPreferencesKey("mouse_fab_y")

                /**
                 * Shared Android folder URIs designated to be exposed when Android hosting is
                 * available.
                 */
                val SHARED_FOLDER_URIS_KEY = stringSetPreferencesKey("shared_folder_uris")

                /** Single SAF tree URI granting full-device browse to consenting paired PCs (2.1). */
                val FULL_BROWSE_ROOT_URI_KEY = stringPreferencesKey("full_browse_root_uri")

                /**
                 * Sentinel value indicating the host address has not been configured by the user.
                 */
                const val DEFAULT_HOST_PLACEHOLDER = ""
        }

        data class ConnectionPreferences(
                val host: String = DEFAULT_HOST_PLACEHOLDER,
                val port: Int = 5005,
                val macAddress: String = "",
                val broadcastIp: String = "255.255.255.255",
                val subnetMask: String = "255.255.255.0"
        )

        data class RemoteDesktopPreferences(
                val quality: Int = 95,
                val targetFps: Int = 120,
                val scale: Float = 0.5f,
                val directTouch: Boolean = false,
                val pointerSpeed: Float = 1.0f,
                val verticalScrollSensitivity: Float = 1.0f,
                val horizontalScrollSensitivity: Float = 1.0f,
                val cursorScale: Float = 1.0f,
                val displayTarget: String = "",
                val preset: String = "smooth_sharp"
        )

        data class PersonalizationPreferences(
                val themeMode: String = "system",
                val themePalette: String = "default",
                val themeStyle: String = "tonal_spot",
                val themeSeedColor: String = "#6750A4", // Default M3 Purple
                val themeSeedChroma: Float = 48.0f,
                val themeContrast: Float = 0.0f,
                val dynamicColor: Boolean = true,
                val fontFamily: String = "default",
                val fontScale: Float = 1.0f,
                val cardCornerRadius: Int = 20,
                val cardOpacity: Float = 1.0f,
                val pcCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val telemetryCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val appLauncherCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val taskManagerCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val remoteDesktopCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val remoteControlCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val remoteMouseCardShapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
                val splashStyle: String = "RemexCommand"
        )

        val appLauncherCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val taskManagerCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val remoteDesktopCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val remoteControlCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val remoteMouseCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val mouseFabXFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[MOUSE_FAB_X_KEY] ?: Float.NaN
                }

        val mouseFabYFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[MOUSE_FAB_Y_KEY] ?: Float.NaN
                }

        // Typed as Flow<Boolean?> so that collectAsStateWithLifecycle(initialValue = null) can distinguish
        // "DataStore hasn't loaded yet" (null initial) from the actual persisted value.
        // The flow itself always emits non-null Boolean.
        val hasCompletedOnboardingFlow: Flow<Boolean?> =
                context.dataStore.data.map { preferences ->
                        preferences[HAS_COMPLETED_ONBOARDING_KEY] ?: false
                }

        // First-run Home Base coach marks; false (unseen) until markDashboardCoachSeen (RemEx-km0i.10).
        val dashboardCoachSeenFlow: Flow<Boolean> =
                context.dataStore.data.map { preferences ->
                        preferences[DASHBOARD_COACH_SEEN_KEY] ?: false
                }

        val hostFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[HOST_KEY] ?: DEFAULT_HOST_PLACEHOLDER
                }

        val portFlow: Flow<Int> =
                context.dataStore.data.map { preferences -> preferences[PORT_KEY] ?: 5005 }

        /**
         * The MAC to wake: what the user typed if they typed one, otherwise what the host told us.
         *
         * MANUAL WINS BY DESIGN (RemEx-izuj). Wake-on-LAN was already built end to end and still
         * needed the one setup step a non-technical user cannot do - reading their PC's MAC off a
         * screen and typing it in. The host now reports it, so the common case needs no entry at
         * all; but a user who has deliberately set one (a different NIC, a router quirk) keeps it.
         *
         * Existing consumers - the Wake-on-LAN card and the quick-settings tile - read this flow, so
         * they inherit the prefill without a UI change.
         */
        val macAddressFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        resolveMacAddress(
                                manual = preferences[MAC_KEY] ?: "",
                                manualHost = preferences[MAC_MANUAL_HOST_KEY] ?: "",
                                hostReported = preferences[HOST_MAC_KEY] ?: "",
                                currentHost = preferences[HOST_KEY] ?: "",
                        )
                }

        val broadcastIpFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[BROADCAST_IP_KEY] ?: "255.255.255.255"
                }

        // Removed individual unused flows (subnetMaskFlow, desktopQualityFlow, etc.)
        // as they are covered by connectionPreferencesFlow and remoteDesktopPreferencesFlow.

        val widgetTelemetryPollSecondsFlow: Flow<Int> =
                context.dataStore.data.map { preferences ->
                        (preferences[WIDGET_TELEMETRY_POLL_SECONDS_KEY]
                                        ?: WIDGET_TELEMETRY_POLL_DEFAULT)
                                .coerceIn(WIDGET_TELEMETRY_POLL_MIN, WIDGET_TELEMETRY_POLL_MAX)
                }

        val homeLayoutJsonFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[HOME_LAYOUT_JSON_KEY] ?: ""
                }

        val homeEnabledCardsJsonFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[HOME_ENABLED_CARDS_JSON_KEY] ?: "[]"
                }

        val themeModeFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[THEME_MODE_KEY] ?: "system"
                }

        val themePaletteFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[THEME_PALETTE_KEY] ?: "default"
                }

        val themeSeedColorFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[THEME_SEED_COLOR_KEY] ?: "#6750A4"
                }

        val fontScaleFlow: Flow<Float> =
                context.dataStore.data.map { preferences -> preferences[FONT_SCALE_KEY] ?: 1.0f }

        val fontFamilyFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[FONT_FAMILY_KEY] ?: "default"
                }

        val cardCornerRadiusFlow: Flow<Int> =
                context.dataStore.data.map { preferences ->
                        preferences[CARD_CORNER_RADIUS_KEY] ?: 20
                }

        val cardOpacityFlow: Flow<Float> =
                context.dataStore.data.map { preferences -> preferences[CARD_OPACITY_KEY] ?: 1.0f }

        val pcCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[PC_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val telemetryCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT
                }

        val connectionPreferencesFlow: Flow<ConnectionPreferences> =
                context.dataStore.data.map { preferences ->
                        ConnectionPreferences(
                                host = preferences[HOST_KEY] ?: DEFAULT_HOST_PLACEHOLDER,
                                port = preferences[PORT_KEY] ?: 5005,
                                macAddress = preferences[MAC_KEY] ?: "",
                                broadcastIp = preferences[BROADCAST_IP_KEY] ?: "255.255.255.255",
                                subnetMask = preferences[SUBNET_MASK_KEY] ?: "255.255.255.0"
                        )
                }

        val remoteDesktopPreferencesFlow: Flow<RemoteDesktopPreferences> =
                context.dataStore.data.map { preferences ->
                        RemoteDesktopPreferences(
                                quality = preferences[DESKTOP_QUALITY_KEY] ?: 95,
                                targetFps = preferences[DESKTOP_TARGET_FPS_KEY] ?: 120,
                                scale = preferences[DESKTOP_SCALE_KEY] ?: 0.5f,
                                directTouch = preferences[DESKTOP_DIRECT_TOUCH_KEY] ?: false,
                                pointerSpeed = preferences[DESKTOP_POINTER_SPEED_KEY] ?: 1.0f,
                                verticalScrollSensitivity =
                                        preferences[VERTICAL_SCROLL_SENSITIVITY_KEY] ?: 1.0f,
                                horizontalScrollSensitivity =
                                        preferences[HORIZONTAL_SCROLL_SENSITIVITY_KEY] ?: 1.0f,
                                cursorScale = preferences[DESKTOP_CURSOR_SCALE_KEY] ?: 1.0f,
                                displayTarget = preferences[DESKTOP_DISPLAY_TARGET_KEY] ?: "",
                                // DESKTOP_PRESET_KEY didn't exist before RemEx-vj31. An install that
                                // already has quality/fps/scale persisted (upgrading from the old
                                // 3-preset system) gets "custom" — those leftover values don't match
                                // the new named bundles, so labeling them "Smooth & Sharp" would lie
                                // about what's actually streaming. Only a truly fresh install (nothing
                                // ever persisted) gets the new default.
                                preset =
                                        preferences[DESKTOP_PRESET_KEY]
                                                ?: if (preferences[DESKTOP_QUALITY_KEY] == null &&
                                                        preferences[DESKTOP_TARGET_FPS_KEY] ==
                                                                null &&
                                                        preferences[DESKTOP_SCALE_KEY] == null
                                                ) {
                                                        "smooth_sharp"
                                                } else {
                                                        "custom"
                                                }
                        )
                }

        /** See hasCompletedOnboardingFlow for the nullable-type rationale — not needed here since
         *  showing the one-time overflow warning an extra time on a cold DataStore load is harmless. */
        val unlimitedWarningShownFlow: Flow<Boolean> =
                context.dataStore.data.map { preferences ->
                        preferences[DESKTOP_UNLIMITED_WARNING_SHOWN_KEY] ?: false
                }

        val personalizationPreferencesFlow: Flow<PersonalizationPreferences> =
                context.dataStore.data.map { preferences ->
                        PersonalizationPreferences(
                                themeMode = preferences[THEME_MODE_KEY] ?: "system",
                                themePalette = preferences[THEME_PALETTE_KEY] ?: "default",
                                themeStyle = preferences[THEME_STYLE_KEY] ?: "tonal_spot",
                                themeSeedColor = preferences[THEME_SEED_COLOR_KEY] ?: "#6750A4",
                                themeSeedChroma = preferences[THEME_SEED_CHROMA_KEY] ?: 48.0f,
                                themeContrast = preferences[THEME_CONTRAST_KEY] ?: 0.0f,
                                dynamicColor = preferences[DYNAMIC_COLOR_KEY] ?: true,
                                fontFamily = preferences[FONT_FAMILY_KEY] ?: "default",
                                fontScale = preferences[FONT_SCALE_KEY] ?: 1.0f,
                                cardCornerRadius = preferences[CARD_CORNER_RADIUS_KEY] ?: 20,
                                cardOpacity = preferences[CARD_OPACITY_KEY] ?: 1.0f,
                                pcCardShapePreset = preferences[PC_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                telemetryCardShapePreset =
                                        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                appLauncherCardShapePreset =
                                        preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                taskManagerCardShapePreset =
                                        preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                remoteDesktopCardShapePreset = preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                remoteControlCardShapePreset = preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                remoteMouseCardShapePreset = preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] ?: DashboardShapes.SHAPE_PRESET_INHERIT,
                                splashStyle = preferences[SPLASH_STYLE_KEY] ?: "RemexCommand"
                        )
                }

        suspend fun saveConnectionSettings(
                host: String,
                port: Int,
                mac: String,
                broadcast: String,
                subnetMask: String
        ) {
                context.dataStore.edit { preferences ->
                        preferences[HOST_KEY] = host
                        preferences[PORT_KEY] = port
                        preferences[MAC_KEY] = mac
                        // Recorded in the same edit as the host it belongs to (RemEx-263f).
                        preferences[MAC_MANUAL_HOST_KEY] = host
                        preferences[BROADCAST_IP_KEY] = broadcast
                        preferences[SUBNET_MASK_KEY] = subnetMask
                }
        }

        // preset defaults to "custom": callers that save raw quality/fps/scale without going through
        // a named DesktopPreset (e.g. ConnectionViewModel's advanced connect form) are, by definition,
        // not applying one of the fixed bundles.
        suspend fun saveRemoteDesktopDefaults(
                quality: Int,
                targetFps: Int,
                scale: Float,
                preset: String = "custom"
        ) {
                context.dataStore.edit { preferences ->
                        preferences[DESKTOP_QUALITY_KEY] = quality.coerceIn(1, 100)
                        preferences[DESKTOP_TARGET_FPS_KEY] = targetFps.coerceIn(1, 360)
                        preferences[DESKTOP_SCALE_KEY] = scale.coerceIn(0.25f, 1.0f)
                        preferences[DESKTOP_PRESET_KEY] = preset
                }
        }

        suspend fun markUnlimitedWarningShown() {
                context.dataStore.edit { it[DESKTOP_UNLIMITED_WARNING_SHOWN_KEY] = true }
        }

        suspend fun saveRemoteDesktopDisplayTarget(target: String) {
                context.dataStore.edit { preferences ->
                        preferences[DESKTOP_DISPLAY_TARGET_KEY] = target
                }
        }

        /**
         * Records the MAC the host reported. Ignores a blank, which is how a host with no suitable
         * adapter says "ask the user" (RemEx-izuj).
         */
        suspend fun saveHostReportedMacAddress(mac: String) {
                if (mac.isBlank()) return
                context.dataStore.edit { preferences -> preferences[HOST_MAC_KEY] = mac }
        }

        suspend fun markOnboardingCompleted() {
                context.dataStore.edit { it[HAS_COMPLETED_ONBOARDING_KEY] = true }
        }

        suspend fun resetOnboarding() {
                context.dataStore.edit { it[HAS_COMPLETED_ONBOARDING_KEY] = false }
        }

        suspend fun markDashboardCoachSeen() {
                context.dataStore.edit { it[DASHBOARD_COACH_SEEN_KEY] = true }
        }

        suspend fun resetDashboardCoach() {
                context.dataStore.edit { it[DASHBOARD_COACH_SEEN_KEY] = false }
        }

        suspend fun getOrCreateClientId(): String {
                val existing = context.dataStore.data.first()[CLIENT_ID_KEY]
                if (!existing.isNullOrBlank()) {
                        return existing
                }

                val generated = UUID.randomUUID().toString().replace("-", "")
                context.dataStore.edit { preferences ->
                        if (preferences[CLIENT_ID_KEY].isNullOrBlank()) {
                                preferences[CLIENT_ID_KEY] = generated
                        }
                }

                return context.dataStore.data.first()[CLIENT_ID_KEY] ?: generated
        }

        suspend fun saveRemoteDesktopDirectTouch(enabled: Boolean) {
                context.dataStore.edit { preferences ->
                        preferences[DESKTOP_DIRECT_TOUCH_KEY] = enabled
                }
        }

        suspend fun saveRemoteDesktopPointerSpeed(speed: Float) {
                context.dataStore.edit { preferences ->
                        preferences[DESKTOP_POINTER_SPEED_KEY] = speed.coerceIn(0.25f, 3.0f)
                }
        }

        suspend fun saveRemoteDesktopCursorScale(scale: Float) {
                context.dataStore.edit { preferences ->
                        preferences[DESKTOP_CURSOR_SCALE_KEY] = scale.coerceIn(0.5f, 2.5f)
                }
        }

        suspend fun saveRemoteDesktopScrollSensitivity(vertical: Float, horizontal: Float) {
                context.dataStore.edit { preferences ->
                        preferences[VERTICAL_SCROLL_SENSITIVITY_KEY] = vertical.coerceIn(0.1f, 5.0f)
                        preferences[HORIZONTAL_SCROLL_SENSITIVITY_KEY] =
                                horizontal.coerceIn(0.1f, 5.0f)
                }
        }

        suspend fun saveWidgetTelemetryPollSeconds(seconds: Int) {
                context.dataStore.edit { preferences ->
                        preferences[WIDGET_TELEMETRY_POLL_SECONDS_KEY] =
                                seconds.coerceIn(
                                        WIDGET_TELEMETRY_POLL_MIN,
                                        WIDGET_TELEMETRY_POLL_MAX
                                )
                }
        }

        suspend fun saveHomeLayout(layoutJson: String) {
                context.dataStore.edit { preferences ->
                        preferences[HOME_LAYOUT_JSON_KEY] = layoutJson
                }
        }

        suspend fun saveHomeEnabledCards(enabledCardsJson: String) {
                context.dataStore.edit { preferences ->
                        preferences[HOME_ENABLED_CARDS_JSON_KEY] = enabledCardsJson
                }
        }

        /**
         * One-time, idempotent migration off the universal clover default (decision #5) for the
         * pc/telemetry buckets only. Rewrites ONLY an absent or legacy-clover (18.0f) stored value
         * to INHERIT so existing cards adopt the new per-category shapes; a deliberately-chosen
         * non-clover shape is preserved. Never touches home_layout_json and must not be called from
         * inside an undo-tracked interaction (no beginInteraction()).
         */
        suspend fun migrateShapeDefaultsV2() {
                val prefs = context.dataStore.data.first()
                if (prefs[SHAPE_DEFAULTS_MIGRATED_V2_KEY] == true) return
                context.dataStore.edit { p ->
                        listOf(PC_CARD_SHAPE_PRESET_KEY, TELEMETRY_CARD_SHAPE_PRESET_KEY).forEach { key ->
                                val stored = p[key]
                                if (stored == null || stored == DashboardShapes.LEGACY_CLOVER_INDEX) {
                                        p[key] = DashboardShapes.SHAPE_PRESET_INHERIT
                                }
                        }
                        p[SHAPE_DEFAULTS_MIGRATED_V2_KEY] = true
                }
        }

        /**
         * Extends the clover-kill migration to the five non-home buckets, which decision #5
         * deliberately left at 18.0f when V2 shipped (RemEx-km0i.7, spec section 3.6).
         *
         * Non-destructive in the same way V2 is: it rewrites only a value that is ABSENT or still
         * the legacy clover. A user who deliberately picked 18.0f for one of these screens keeps
         * it - the two cases are indistinguishable in storage, and preserving a real choice matters
         * more than migrating every last default.
         */
        /**
         * The user's per-category shape choices. Absent entries mean INHERIT, so the map is sparse
         * and an untouched install yields an empty map rather than eight explicit sentinels.
         */
        val categoryShapePresetsFlow: Flow<Map<DashboardShapes.CardCategory, Float>> =
                context.dataStore.data.map { preferences ->
                        CATEGORY_SHAPE_PRESET_KEYS.mapNotNull { (category, key) ->
                                preferences[key]?.let { category to it }
                        }
                                .toMap()
                }

        suspend fun saveCategoryShapePreset(
                category: DashboardShapes.CardCategory,
                preset: Float,
        ) {
                val key = CATEGORY_SHAPE_PRESET_KEYS.getValue(category)
                context.dataStore.edit { it[key] = preset }
        }

        suspend fun migrateShapeDefaultsV3() {
                val prefs = context.dataStore.data.first()
                if (prefs[SHAPE_DEFAULTS_MIGRATED_V3_KEY] == true) return
                context.dataStore.edit { p ->
                        listOf(
                                        APP_LAUNCHER_CARD_SHAPE_PRESET_KEY,
                                        TASK_MANAGER_CARD_SHAPE_PRESET_KEY,
                                        REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY,
                                        REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY,
                                        REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY,
                                )
                                .forEach { key ->
                                        val stored = p[key]
                                        if (stored == null || stored == DashboardShapes.LEGACY_CLOVER_INDEX) {
                                                p[key] = DashboardShapes.SHAPE_PRESET_INHERIT
                                        }
                                }
                        p[SHAPE_DEFAULTS_MIGRATED_V3_KEY] = true
                }
        }

        suspend fun saveMouseFabPosition(x: Float, y: Float) {
                context.dataStore.edit { preferences ->
                        preferences[MOUSE_FAB_X_KEY] = x
                        preferences[MOUSE_FAB_Y_KEY] = y
                }
        }

        suspend fun savePersonalization(
                themeMode: String,
                themePalette: String,
                themeStyle: String,
                themeSeedColor: String,
                themeSeedChroma: Float,
                themeContrast: Float,
                fontFamily: String,
                fontScale: Float,
                cardCornerRadius: Int,
                cardOpacity: Float,
                pcCardShapePreset: Float,
                telemetryCardShapePreset: Float,
                appLauncherCardShapePreset: Float,
                taskManagerCardShapePreset: Float,
                remoteDesktopCardShapePreset: Float,
                remoteControlCardShapePreset: Float,
                remoteMouseCardShapePreset: Float,
                splashStyle: String
        ) {
                context.dataStore.edit { preferences ->
                        preferences[THEME_MODE_KEY] = themeMode
                        preferences[THEME_PALETTE_KEY] = themePalette
                        preferences[THEME_STYLE_KEY] = themeStyle
                        preferences[THEME_SEED_COLOR_KEY] = themeSeedColor
                        preferences[THEME_SEED_CHROMA_KEY] = themeSeedChroma
                        preferences[THEME_CONTRAST_KEY] = themeContrast
                        preferences[FONT_FAMILY_KEY] = fontFamily
                        preferences[FONT_SCALE_KEY] = fontScale.coerceIn(0.85f, 1.4f)
                        preferences[CARD_CORNER_RADIUS_KEY] = cardCornerRadius.coerceIn(4, 36)
                        preferences[CARD_OPACITY_KEY] = cardOpacity.coerceIn(0.1f, 1.0f)
                        preferences[PC_CARD_SHAPE_PRESET_KEY] = pcCardShapePreset
                        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] = telemetryCardShapePreset
                        preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] = appLauncherCardShapePreset
                        preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] = taskManagerCardShapePreset
                        preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] =
                                remoteDesktopCardShapePreset
                        preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] =
                                remoteControlCardShapePreset
                        preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] = remoteMouseCardShapePreset
                        preferences[SPLASH_STYLE_KEY] = splashStyle
                }
        }

        /**
         * Persists the Material You dynamic-color toggle immediately — a switch flip should
         * not ride the debounced personalization save path (RemEx-9429).
         */
        suspend fun setDynamicColor(enabled: Boolean) {
                context.dataStore.edit { preferences ->
                        preferences[DYNAMIC_COLOR_KEY] = enabled
                }
        }

        // ── File transfer shared folders ──────────────────────────────────────────

        val sharedFolderUrisFlow: Flow<Set<String>> =
                context.dataStore.data.map { prefs -> prefs[SHARED_FOLDER_URIS_KEY] ?: emptySet() }

        suspend fun addSharedFolderUri(uri: String) {
                context.dataStore.edit { prefs ->
                        prefs[SHARED_FOLDER_URIS_KEY] =
                                (prefs[SHARED_FOLDER_URIS_KEY] ?: emptySet()) + uri
                }
        }

        suspend fun removeSharedFolderUri(uri: String) {
                context.dataStore.edit { prefs ->
                        prefs[SHARED_FOLDER_URIS_KEY] =
                                (prefs[SHARED_FOLDER_URIS_KEY] ?: emptySet()) - uri
                }
        }

        // ── File-sharing 2.1: full-browse SAF root + per-device trust ─────────────
        // (plan §2). Full-browse is a single SAF ACTION_OPEN_DOCUMENT_TREE grant of a storage root,
        // exposed as a "volume" to a consenting paired PC. Per-device trust keys follow the plan's
        // fileTrust_<deviceId>_fullBrowse / _autoAccept scheme. The full SAF picker + consent UI land
        // in WP9; these accessors are the persistence plumbing WP6 relies on.

        val fullBrowseRootUriFlow: Flow<String?> =
                context.dataStore.data.map { prefs -> prefs[FULL_BROWSE_ROOT_URI_KEY] }

        suspend fun setFullBrowseRootUri(uri: String) {
                context.dataStore.edit { prefs -> prefs[FULL_BROWSE_ROOT_URI_KEY] = uri }
        }

        suspend fun clearFullBrowseRootUri() {
                context.dataStore.edit { prefs -> prefs.remove(FULL_BROWSE_ROOT_URI_KEY) }
        }

        private fun fileTrustFullBrowseKey(deviceId: String) =
                booleanPreferencesKey("fileTrust_${deviceId}_fullBrowse")

        private fun fileTrustAutoAcceptKey(deviceId: String) =
                booleanPreferencesKey("fileTrust_${deviceId}_autoAccept")

        fun fileTrustFullBrowseFlow(deviceId: String): Flow<Boolean> =
                context.dataStore.data.map { prefs -> prefs[fileTrustFullBrowseKey(deviceId)] ?: false }

        fun fileTrustAutoAcceptFlow(deviceId: String): Flow<Boolean> =
                context.dataStore.data.map { prefs -> prefs[fileTrustAutoAcceptKey(deviceId)] ?: false }

        suspend fun setFileTrustFullBrowse(deviceId: String, granted: Boolean) {
                context.dataStore.edit { prefs -> prefs[fileTrustFullBrowseKey(deviceId)] = granted }
        }

        suspend fun setFileTrustAutoAccept(deviceId: String, enabled: Boolean) {
                context.dataStore.edit { prefs -> prefs[fileTrustAutoAcceptKey(deviceId)] = enabled }
        }

        // ── Known PCs: per-PC nickname and last-connected record (RemEx-k62t) ─────
        // Keyed by HostIdentity, NEVER by address: one PC answers at a LAN IP, a Tailscale address
        // and a hostname, so an address-keyed nickname would show one machine three times with a
        // third of the user's settings each. Same prefixed-key shape as fileTrust_ above; the
        // scheme itself lives in KnownHosts so it can be tested without DataStore.

        private fun knownHostNicknameKey(identity: String) =
                stringPreferencesKey(KnownHosts.nicknameKeyName(identity))

        private fun knownHostLastAddressKey(identity: String) =
                stringPreferencesKey(KnownHosts.lastAddressKeyName(identity))

        private fun knownHostLastPortKey(identity: String) =
                intPreferencesKey(KnownHosts.lastPortKeyName(identity))

        private fun knownHostLastConnectedKey(identity: String) =
                longPreferencesKey(KnownHosts.lastConnectedKeyName(identity))

        val knownHostRecordsFlow: Flow<Map<String, KnownHostRecord>> =
                context.dataStore.data.map { prefs ->
                        KnownHosts.parseRecords(prefs.asMap().mapKeys { it.key.name })
                }

        /** Blank clears the nickname rather than storing one, so the row falls back to its address. */
        suspend fun setKnownHostNickname(identity: String, nickname: String) {
                if (identity.isBlank()) return
                context.dataStore.edit { prefs ->
                        val trimmed = nickname.trim()
                        if (trimmed.isEmpty()) prefs.remove(knownHostNicknameKey(identity))
                        else prefs[knownHostNicknameKey(identity)] = trimmed
                }
        }

        /**
         * Records a connection that actually succeeded.
         *
         * Attempts are deliberately not recorded: "last connected" ordering the user can trust has
         * to mean connected, or a PC that is powered off climbs to the top of the list every time
         * they try it and fail.
         */
        suspend fun recordKnownHostConnection(
                identity: String,
                address: String,
                port: Int,
                atMillis: Long
        ) {
                if (identity.isBlank() || address.isBlank()) return
                context.dataStore.edit { prefs ->
                        prefs[knownHostLastAddressKey(identity)] = address
                        prefs[knownHostLastPortKey(identity)] = port
                        prefs[knownHostLastConnectedKey(identity)] = atMillis
                }
        }

        /**
         * Moves a PC's remembered details onto the identity it has after a certificate change
         * (RemEx-bye7).
         *
         * ONLY THE NICKNAME IS CARRIED, and the omissions are the point. The address, port and
         * timestamp are all being written by the connection that triggered this, so copying the old
         * ones would either be redundant or actively wrong — an older "last connected" would sort
         * the PC the user is sitting on below one they have not touched in a week. The nickname is
         * the only thing here that a person chose and that no reconnection can reproduce.
         *
         * A NICKNAME ALREADY ON THE DESTINATION WINS, and the case is DHCP address reuse rather than
         * a contrived one. PC A, "Studio", was paired at 192.168.1.50; the router later gives .50 to
         * PC B, which is separately paired under its own hostname and named "Laptop". Connecting to
         * .50 shows a certificate change, the user believes the reinstall story and confirms — and
         * the re-pair derives PC B's identity, which already has a live row. The recorded decision
         * covers a NEW row inheriting a name; it does not cover silently relabelling a different PC
         * the user is still paired to. Skipping the write also makes this idempotent under replay.
         *
         * A blank source nickname is not written either, so a PC that never had one does not gain an
         * empty entry.
         *
         * The source's remaining keys are then dropped. NOTE WHAT THAT DOES AND DOES NOT DO: it does
         * not make the old row disappear. Rows come from the pinned-host map via
         * `KnownHosts.build`/`groupByIdentity`, and the old one goes because `confirmCertRepair`
         * called `PinnedHostStore.forgetHost`, which removes every alias holding the old hash. This
         * removal is orphan-preference hygiene — worth doing so a dead identity stops carrying a
         * nickname and a timestamp, but not the mechanism that clears the list.
         *
         * This and the `recordKnownHostConnection` that follows it are two separate DataStore
         * transactions, deliberately not merged. Cancellation between them leaves the new row named
         * but unstamped, which the next connection repairs, and the nickname — the thing that cannot
         * be reproduced — is the half that lands first.
         */
        suspend fun migrateKnownHostIdentity(oldIdentity: String, newIdentity: String) {
                if (oldIdentity.isBlank() || newIdentity.isBlank()) return
                if (oldIdentity == newIdentity) return
                context.dataStore.edit { prefs ->
                        val nickname = prefs[knownHostNicknameKey(oldIdentity)]?.trim()
                        val destinationAlreadyNamed =
                                !prefs[knownHostNicknameKey(newIdentity)].isNullOrBlank()
                        if (!nickname.isNullOrEmpty() && !destinationAlreadyNamed) {
                                prefs[knownHostNicknameKey(newIdentity)] = nickname
                        }

                        prefs.remove(knownHostNicknameKey(oldIdentity))
                        prefs.remove(knownHostLastAddressKey(oldIdentity))
                        prefs.remove(knownHostLastPortKey(oldIdentity))
                        prefs.remove(knownHostLastConnectedKey(oldIdentity))
                }
        }

        /**
         * Drops everything remembered about one PC.
         *
         * Called when the user unpairs it. Leaving the nickname behind would silently re-attach it
         * to the next PC that derived the same identity — which can only be the same machine, but
         * would also mean an unpair that did not forget.
         */
        suspend fun forgetKnownHost(identity: String) {
                if (identity.isBlank()) return
                context.dataStore.edit { prefs ->
                        prefs.remove(knownHostNicknameKey(identity))
                        prefs.remove(knownHostLastAddressKey(identity))
                        prefs.remove(knownHostLastPortKey(identity))
                        prefs.remove(knownHostLastConnectedKey(identity))
                }
        }
}
