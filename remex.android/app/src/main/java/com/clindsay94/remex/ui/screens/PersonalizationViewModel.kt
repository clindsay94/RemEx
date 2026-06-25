package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

@OptIn(FlowPreview::class)
class PersonalizationViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val personalization: StateFlow<SettingsManager.PersonalizationPreferences?> =
        settingsManager.personalizationPreferencesFlow
            .stateIn(
                scope = viewModelScope,
                started = SharingStarted.WhileSubscribed(5000),
                initialValue = null
            )

    // Pending save state — debounced to avoid a DataStore write on every slider frame.
    private val _pendingSave = MutableStateFlow<SettingsManager.PersonalizationPreferences?>(null)

    init {
        viewModelScope.launch {
            _pendingSave
                .filterNotNull()
                .debounce(500L)
                .collectLatest { prefs ->
                    settingsManager.savePersonalization(
                        themeMode = prefs.themeMode,
                        themePalette = prefs.themePalette,
                        themeStyle = prefs.themeStyle,
                        themeSeedColor = prefs.themeSeedColor,
                        themeSeedChroma = prefs.themeSeedChroma,
                        themeContrast = prefs.themeContrast,
                        fontFamily = prefs.fontFamily,
                        fontScale = prefs.fontScale,
                        cardCornerRadius = prefs.cardCornerRadius,
                        cardOpacity = prefs.cardOpacity,
                        pcCardShapePreset = prefs.pcCardShapePreset,
                        telemetryCardShapePreset = prefs.telemetryCardShapePreset,
                        appLauncherCardShapePreset = prefs.appLauncherCardShapePreset,
                        taskManagerCardShapePreset = prefs.taskManagerCardShapePreset,
                        remoteDesktopCardShapePreset = prefs.remoteDesktopCardShapePreset,
                        remoteControlCardShapePreset = prefs.remoteControlCardShapePreset,
                        remoteMouseCardShapePreset = prefs.remoteMouseCardShapePreset,
                        splashStyle = prefs.splashStyle
                    )
                }
        }
    }

    fun save(
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
        _pendingSave.value = SettingsManager.PersonalizationPreferences(
            themeMode = themeMode,
            themePalette = themePalette,
            themeStyle = themeStyle,
            themeSeedColor = themeSeedColor,
            themeSeedChroma = themeSeedChroma,
            themeContrast = themeContrast,
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
            remoteMouseCardShapePreset = remoteMouseCardShapePreset,
            splashStyle = splashStyle
        )
    }
}
