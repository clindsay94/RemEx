package com.clindsay94.remex.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.floatPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.util.UUID
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsManager(val context: Context) {

        companion object {
                val HOST_KEY = stringPreferencesKey("host")
                val PORT_KEY = intPreferencesKey("port")
                val MAC_KEY = stringPreferencesKey("mac_address")
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
                // (both screens combined), or "monitor:<displayId>" for a specific monitor.
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
                val THEME_SEED_COLOR_KEY = stringPreferencesKey("theme_seed_color")
                val THEME_SEED_CHROMA_KEY = floatPreferencesKey("theme_seed_chroma")
                val THEME_CONTRAST_KEY = floatPreferencesKey("theme_contrast")

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
                val fontFamily: String = "default",
                val fontScale: Float = 1.0f,
                val cardCornerRadius: Int = 20,
                val cardOpacity: Float = 1.0f,
                val pcCardShapePreset: Float = 18.0f,
                val telemetryCardShapePreset: Float = 18.0f,
                val appLauncherCardShapePreset: Float = 18.0f,
                val taskManagerCardShapePreset: Float = 18.0f,
                val remoteDesktopCardShapePreset: Float = 18.0f,
                val remoteControlCardShapePreset: Float = 18.0f,
                val remoteMouseCardShapePreset: Float = 18.0f,
                val splashStyle: String = "RemexCommand"
        )

        val appLauncherCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] ?: 18.0f
                }

        val taskManagerCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] ?: 18.0f
                }

        val remoteDesktopCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] ?: 18.0f
                }

        val remoteControlCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] ?: 18.0f
                }

        val remoteMouseCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] ?: 18.0f
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

        val hostFlow: Flow<String> =
                context.dataStore.data.map { preferences ->
                        preferences[HOST_KEY] ?: DEFAULT_HOST_PLACEHOLDER
                }

        val portFlow: Flow<Int> =
                context.dataStore.data.map { preferences -> preferences[PORT_KEY] ?: 5005 }

        val macAddressFlow: Flow<String> =
                context.dataStore.data.map { preferences -> preferences[MAC_KEY] ?: "" }

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
                        preferences[PC_CARD_SHAPE_PRESET_KEY] ?: 18.0f
                }

        val telemetryCardShapePresetFlow: Flow<Float> =
                context.dataStore.data.map { preferences ->
                        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: 18.0f
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
                                fontFamily = preferences[FONT_FAMILY_KEY] ?: "default",
                                fontScale = preferences[FONT_SCALE_KEY] ?: 1.0f,
                                cardCornerRadius = preferences[CARD_CORNER_RADIUS_KEY] ?: 20,
                                cardOpacity = preferences[CARD_OPACITY_KEY] ?: 1.0f,
                                pcCardShapePreset = preferences[PC_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
                                telemetryCardShapePreset =
                                        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
                                appLauncherCardShapePreset =
                                        preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
                                taskManagerCardShapePreset =
                                        preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
                                remoteDesktopCardShapePreset = preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
                                remoteControlCardShapePreset = preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
                                remoteMouseCardShapePreset = preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] ?: 18.0f,
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

        suspend fun saveRemoteDesktopDisplayTarget(token: String) {
                context.dataStore.edit { preferences ->
                        preferences[DESKTOP_DISPLAY_TARGET_KEY] = token
                }
        }

        suspend fun markOnboardingCompleted() {
                context.dataStore.edit { it[HAS_COMPLETED_ONBOARDING_KEY] = true }
        }

        suspend fun resetOnboarding() {
                context.dataStore.edit { it[HAS_COMPLETED_ONBOARDING_KEY] = false }
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
}
