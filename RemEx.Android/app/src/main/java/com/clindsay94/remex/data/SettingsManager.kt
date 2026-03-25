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

        val DESKTOP_QUALITY_KEY = intPreferencesKey("desktop_quality")
        val DESKTOP_TARGET_FPS_KEY = intPreferencesKey("desktop_target_fps")
        val DESKTOP_SCALE_KEY = floatPreferencesKey("desktop_scale")

        val HOME_LAYOUT_JSON_KEY = stringPreferencesKey("home_layout_json")
        val HOME_ENABLED_CARDS_JSON_KEY = stringPreferencesKey("home_enabled_cards_json")

        val THEME_MODE_KEY = stringPreferencesKey("theme_mode")
        val THEME_PALETTE_KEY = stringPreferencesKey("theme_palette")
        val FONT_FAMILY_KEY = stringPreferencesKey("font_family")
        val FONT_SCALE_KEY = floatPreferencesKey("font_scale")
        val CARD_CORNER_RADIUS_KEY = intPreferencesKey("card_corner_radius")
        val CARD_OPACITY_KEY = floatPreferencesKey("card_opacity")
        val PC_CARD_SHAPE_PRESET_KEY = stringPreferencesKey("pc_card_shape_preset")
        val TELEMETRY_CARD_SHAPE_PRESET_KEY = stringPreferencesKey("telemetry_card_shape_preset")
    }

    data class ConnectionPreferences(
        val host: String = "192.168.1.100",
        val port: Int = 5005,
        val macAddress: String = "",
        val broadcastIp: String = "255.255.255.255",
        val subnetMask: String = "255.255.255.0"
    )

    data class RemoteDesktopPreferences(
        val quality: Int = 50,
        val targetFps: Int = 30,
        val scale: Float = 0.6f
    )

    data class PersonalizationPreferences(
        val themeMode: String = "system",
        val themePalette: String = "default",
        val fontFamily: String = "default",
        val fontScale: Float = 1.0f,
        val cardCornerRadius: Int = 20,
        val cardOpacity: Float = 1.0f,
        val pcCardShapePreset: String = "rounded",
        val telemetryCardShapePreset: String = "rounded"
    )

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

    val pcCardShapePresetFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[PC_CARD_SHAPE_PRESET_KEY] ?: "rounded"
    }

    val telemetryCardShapePresetFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: "rounded"
    }

    val connectionPreferencesFlow: Flow<ConnectionPreferences> = context.dataStore.data.map { preferences ->
        ConnectionPreferences(
            host = preferences[HOST_KEY] ?: "192.168.1.100",
            port = preferences[PORT_KEY] ?: 5005,
            macAddress = preferences[MAC_KEY] ?: "",
            broadcastIp = preferences[BROADCAST_IP_KEY] ?: "255.255.255.255",
            subnetMask = preferences[SUBNET_MASK_KEY] ?: "255.255.255.0"
        )
    }

    val remoteDesktopPreferencesFlow: Flow<RemoteDesktopPreferences> = context.dataStore.data.map { preferences ->
        RemoteDesktopPreferences(
            quality = preferences[DESKTOP_QUALITY_KEY] ?: 50,
            targetFps = preferences[DESKTOP_TARGET_FPS_KEY] ?: 30,
            scale = preferences[DESKTOP_SCALE_KEY] ?: 0.6f
        )
    }

    val personalizationPreferencesFlow: Flow<PersonalizationPreferences> = context.dataStore.data.map { preferences ->
        PersonalizationPreferences(
            themeMode = preferences[THEME_MODE_KEY] ?: "system",
            themePalette = preferences[THEME_PALETTE_KEY] ?: "default",
            fontFamily = preferences[FONT_FAMILY_KEY] ?: "default",
            fontScale = preferences[FONT_SCALE_KEY] ?: 1.0f,
            cardCornerRadius = preferences[CARD_CORNER_RADIUS_KEY] ?: 20,
            cardOpacity = preferences[CARD_OPACITY_KEY] ?: 1.0f,
            pcCardShapePreset = preferences[PC_CARD_SHAPE_PRESET_KEY] ?: "rounded",
            telemetryCardShapePreset = preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] ?: "rounded"
        )
    }

    suspend fun saveSettings(host: String, port: Int, mac: String = "", broadcast: String = "255.255.255.255") {
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

    suspend fun saveRemoteDesktopDefaults(quality: Int, targetFps: Int, scale: Float) {
        context.dataStore.edit { preferences ->
            preferences[DESKTOP_QUALITY_KEY] = quality.coerceIn(1, 100)
            preferences[DESKTOP_TARGET_FPS_KEY] = targetFps.coerceIn(1, 120)
            preferences[DESKTOP_SCALE_KEY] = scale.coerceIn(0.25f, 1.0f)
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
        fontFamily: String,
        fontScale: Float,
        cardCornerRadius: Int,
        cardOpacity: Float,
        pcCardShapePreset: String,
        telemetryCardShapePreset: String
    ) {
        context.dataStore.edit { preferences ->
            preferences[THEME_MODE_KEY] = themeMode
            preferences[THEME_PALETTE_KEY] = themePalette
            preferences[FONT_FAMILY_KEY] = fontFamily
            preferences[FONT_SCALE_KEY] = fontScale.coerceIn(0.85f, 1.4f)
            preferences[CARD_CORNER_RADIUS_KEY] = cardCornerRadius.coerceIn(4, 36)
            preferences[CARD_OPACITY_KEY] = cardOpacity.coerceIn(0.4f, 1.0f)
            preferences[PC_CARD_SHAPE_PRESET_KEY] = pcCardShapePreset
            preferences[TELEMETRY_CARD_SHAPE_PRESET_KEY] = telemetryCardShapePreset
        }
    }
}
