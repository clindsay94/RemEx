package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class PersonalizationViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val personalization: StateFlow<SettingsManager.PersonalizationPreferences?> =
        settingsManager.personalizationPreferencesFlow
            .stateIn(
                scope = viewModelScope,
                started = SharingStarted.WhileSubscribed(5000),
                initialValue = null
            )

    fun save(
        themeMode: String,
        themePalette: String,
        themeStyle: String,
        themeSeedColor: String,
        fontFamily: String,
        fontScale: Float,
        cardCornerRadius: Int,
        cardOpacity: Float,
        pcCardShapePreset: String,
        telemetryCardShapePreset: String,
        appLauncherCardShapePreset: String,
        taskManagerCardShapePreset: String,
        remoteDesktopCardShapePreset: String,
        remoteControlCardShapePreset: String,
        remoteMouseCardShapePreset: String
    ) {
        viewModelScope.launch {
            settingsManager.savePersonalization(
                themeMode = themeMode,
                themePalette = themePalette,
                themeStyle = themeStyle,
                themeSeedColor = themeSeedColor,
                fontFamily = fontFamily,
                fontScale = fontScale,
                cardCornerRadius = cardCornerRadius,
                cardOpacity = cardOpacity,
                pcCardShapePreset = pcCardShapePreset,
                telemetryCardShapePreset = telemetryCardShapePreset,
                appLauncherCardShapePreset = appLauncherCardShapePreset,
                taskManagerCardShapePreset = taskManagerCardShapePreset,
                remoteDesktopCardShapePreset = remoteDesktopCardShapePreset,
                remoteControlCardShapePreset = remoteControlCardShapePreset,
                remoteMouseCardShapePreset = remoteMouseCardShapePreset
            )
        }
    }
}
