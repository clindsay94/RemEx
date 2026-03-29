package com.clindsay94.remex.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.floatPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsManager(private val context: Context) {

    companion object {
        val HOST_KEY = stringPreferencesKey("host")
        val PORT_KEY = intPreferencesKey("port")
        val MAC_KEY = stringPreferencesKey("mac_address")
        val BROADCAST_IP_KEY = stringPreferencesKey("broadcast_ip")
        val SUBNET_MASK_KEY = stringPreferencesKey("subnet_mask")
        val ACCESS_KEY = stringPreferencesKey("access_key")

        val DESKTOP_QUALITY_KEY = intPreferencesKey("desktop_quality")
        val DESKTOP_TARGET_FPS_KEY = intPreferencesKey("desktop_target_fps")
        val DESKTOP_SCALE_KEY = floatPreferencesKey("desktop_scale")
        val DESKTOP_DIRECT_TOUCH_KEY = androidx.datastore.preferences.core.booleanPreferencesKey("desktop_direct_touch")

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
        val TELEMETRY_CARD_SHAPE_PRESET_KEY = floatPreferencesKey("telemetry_card_shape_preset_v2")
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
    }

    data class ConnectionPreferences(
        val host: String = "192.168.1.100",
        val port: Int = 5005,
        val macAddress: String = "",
        val broadcastIp: String = "255.255.255.255",
        val subnetMask: String = "255.255.255.0",
        val accessKey: String = ""
    )

    data class RemoteDesktopPreferences(
        val quality: Int = 50,
        val targetFps: Int = 30,
        val scale: Float = 0.6f,
        val directTouch: Boolean = false
    )

    data class PersonalizationPreferences(
        val themeMode: String = "system",
        val themePalette: String = "default",
        val themeStyle: String = "tonal_spot",
        val themeSeedColor: String = "#6750A4", // Default M3 Purple
        val fontFamily: String = "default",
        val fontScale: Float = 1.0f,
        val cardCornerRadius: Int = 20,
        val cardOpacity: Float = 1.0f,
        val pcCardShapePreset: Float = 0f,
        val telemetryCardShapePreset: Float = 0f,
        val appLauncherCardShapePreset: Float = 0f,
        val taskManagerCardShapePreset: Float = 0f,
        val remoteDesktopCardShapePreset: Float = 0f,
        val remoteControlCardShapePreset: Float = 0f,
        val remoteMouseCardShapePreset: Float = 0f
    )

    val appLauncherCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val taskManagerCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val remoteDesktopCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val remoteControlCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val remoteMouseCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val hostFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[HOST_KEY] ?: "192.168.1.100"
    }

    val portFlow: Flow<Int> = context.dataStore.data.map { preferences ->
        preferences[PORT_KEY] ?: 5005
    }

    val macAddressFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[MAC_KEY] ?: ""
    }

    val broadcastIpFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[BROADCAST_IP_KEY] ?: "255.255.255.255"
    }

    val subnetMaskFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[SUBNET_MASK_KEY] ?: "255.255.255.0"
    }

    val accessKeyFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[ACCESS_KEY] ?: ""
    }

    val desktopQualityFlow: Flow<Int> = context.dataStore.data.map { preferences ->
        preferences[DESKTOP_QUALITY_KEY] ?: 50
    }

    val desktopTargetFpsFlow: Flow<Int> = context.dataStore.data.map { preferences ->
        preferences[DESKTOP_TARGET_FPS_KEY] ?: 30
    }

    val desktopScaleFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[DESKTOP_SCALE_KEY] ?: 0.6f
    }

    val homeLayoutJsonFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[HOME_LAYOUT_JSON_KEY] ?: ""
    }

    val homeEnabledCardsJsonFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[HOME_ENABLED_CARDS_JSON_KEY] ?: "[]"
    }

    val themeModeFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[THEME_MODE_KEY] ?: "system"
    }

    val themePaletteFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[THEME_PALETTE_KEY] ?: "default"
    }

    val themeSeedColorFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[THEME_SEED_COLOR_KEY] ?: "#6750A4"
    }

    val fontScaleFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[FONT_SCALE_KEY] ?: 1.0f
    }

    val fontFamilyFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[FONT_FAMILY_KEY] ?: "default"
    }

    val cardCornerRadiusFlow: Flow<Int> = context.dataStore.data.map { preferences ->
        preferences[CARD_CORNER_RADIUS_KEY] ?: 20
    }

    val cardOpacityFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[CARD_OPACITY_KEY] ?: 1.0f
    }

    val pcCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[PC_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val telemetryCardShapePresetFlow: Flow<Float> = context.dataStore.data.map { preferences ->
        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: 0f
    }

    val connectionPreferencesFlow: Flow<ConnectionPreferences> =
        context.dataStore.data.map { preferences ->
            ConnectionPreferences(
                host = preferences[HOST_KEY] ?: "192.168.1.100",
                port = preferences[PORT_KEY] ?: 5005,
                macAddress = preferences[MAC_KEY] ?: "",
                broadcastIp = preferences[BROADCAST_IP_KEY] ?: "255.255.255.255",
                subnetMask = preferences[SUBNET_MASK_KEY] ?: "255.255.255.0",
                accessKey = preferences[ACCESS_KEY] ?: ""
            )
        }

    val remoteDesktopPreferencesFlow: Flow<RemoteDesktopPreferences> =
        context.dataStore.data.map { preferences ->
            RemoteDesktopPreferences(
                quality = preferences[DESKTOP_QUALITY_KEY] ?: 50,
                targetFps = preferences[DESKTOP_TARGET_FPS_KEY] ?: 30,
                scale = preferences[DESKTOP_SCALE_KEY] ?: 0.6f,
                directTouch = preferences[DESKTOP_DIRECT_TOUCH_KEY] ?: false
            )
        }

    val personalizationPreferencesFlow: Flow<PersonalizationPreferences> =
        context.dataStore.data.map { preferences ->
            PersonalizationPreferences(
                themeMode = preferences[THEME_MODE_KEY] ?: "system",
                themePalette = preferences[THEME_PALETTE_KEY] ?: "default",
                themeStyle = preferences[THEME_STYLE_KEY] ?: "tonal_spot",
                themeSeedColor = preferences[THEME_SEED_COLOR_KEY] ?: "#6750A4",
                fontFamily = preferences[FONT_FAMILY_KEY] ?: "default",
                fontScale = preferences[FONT_SCALE_KEY] ?: 1.0f,
                cardCornerRadius = preferences[CARD_CORNER_RADIUS_KEY] ?: 20,
                cardOpacity = preferences[CARD_OPACITY_KEY] ?: 1.0f,
                pcCardShapePreset = preferences[PC_CARD_SHAPE_PRESET_KEY] ?: 0f,
                telemetryCardShapePreset = preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY]
                    ?: 0f,
                appLauncherCardShapePreset = preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY]
                    ?: 0f,
                taskManagerCardShapePreset = preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY]
                    ?: 0f,
                remoteDesktopCardShapePreset = preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY]
                    ?: 0f,
                remoteControlCardShapePreset = preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY]
                    ?: 0f,
                remoteMouseCardShapePreset = preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY]
                    ?: 0f
            )
        }

    suspend fun saveSettings(
        host: String,
        port: Int,
        mac: String = "",
        broadcast: String = "255.255.255.255"
    ) {
        saveConnectionSettings(
            host = host,
            port = port,
            mac = mac,
            broadcast = broadcast,
            subnetMask = "255.255.255.0"
        )
    }

    suspend fun saveConnectionSettings(
        host: String,
        port: Int,
        mac: String,
        broadcast: String,
        subnetMask: String,
        accessKey: String = ""
    ) {
        context.dataStore.edit { preferences ->
            preferences[HOST_KEY] = host
            preferences[PORT_KEY] = port
            preferences[MAC_KEY] = mac
            preferences[BROADCAST_IP_KEY] = broadcast
            preferences[SUBNET_MASK_KEY] = subnetMask
            preferences[ACCESS_KEY] = accessKey
        }
    }

    suspend fun saveRemoteDesktopDefaults(quality: Int, targetFps: Int, scale: Float) {
        context.dataStore.edit { preferences ->
            preferences[DESKTOP_QUALITY_KEY] = quality.coerceIn(1, 100)
            preferences[DESKTOP_TARGET_FPS_KEY] = targetFps.coerceIn(1, 120)
            preferences[DESKTOP_SCALE_KEY] = scale.coerceIn(0.25f, 1.0f)
        }
    }

    suspend fun saveRemoteDesktopDirectTouch(enabled: Boolean) {
        context.dataStore.edit { preferences ->
            preferences[DESKTOP_DIRECT_TOUCH_KEY] = enabled
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

    suspend fun savePersonalization(
        themeMode: String,
        themePalette: String,
        themeStyle: String,
        themeSeedColor: String,
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
        remoteMouseCardShapePreset: Float
    ) {
        context.dataStore.edit { preferences ->
            preferences[THEME_MODE_KEY] = themeMode
            preferences[THEME_PALETTE_KEY] = themePalette
            preferences[THEME_STYLE_KEY] = themeStyle
            preferences[THEME_SEED_COLOR_KEY] = themeSeedColor
            preferences[FONT_FAMILY_KEY] = fontFamily
            preferences[FONT_SCALE_KEY] = fontScale.coerceIn(0.85f, 1.4f)
            preferences[CARD_CORNER_RADIUS_KEY] = cardCornerRadius.coerceIn(4, 36)
            preferences[CARD_OPACITY_KEY] = cardOpacity.coerceIn(0.4f, 1.0f)
            preferences[PC_CARD_SHAPE_PRESET_KEY] = pcCardShapePreset
            preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] = telemetryCardShapePreset
            preferences[APP_LAUNCHER_CARD_SHAPE_PRESET_KEY] = appLauncherCardShapePreset
            preferences[TASK_MANAGER_CARD_SHAPE_PRESET_KEY] = taskManagerCardShapePreset
            preferences[REMOTE_DESKTOP_CARD_SHAPE_PRESET_KEY] = remoteDesktopCardShapePreset
            preferences[REMOTE_CONTROL_CARD_SHAPE_PRESET_KEY] = remoteControlCardShapePreset
            preferences[REMOTE_MOUSE_CARD_SHAPE_PRESET_KEY] = remoteMouseCardShapePreset
        }
    }
}
