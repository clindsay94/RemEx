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

    val personalization: StateFlow<SettingsManager.PersonalizationPreferences> =
        settingsManager.personalizationPreferencesFlow
            .stateIn(
                scope = viewModelScope,
                started = SharingStarted.WhileSubscribed(5000),
                initialValue = SettingsManager.PersonalizationPreferences()
            )

    fun save(
        themeMode: String,
        themePalette: String,
        fontScale: Float,
        cardCornerRadius: Int,
        cardOpacity: Float
    ) {
        viewModelScope.launch {
            settingsManager.savePersonalization(
                themeMode = themeMode,
                themePalette = themePalette,
                fontScale = fontScale,
                cardCornerRadius = cardCornerRadius,
                cardOpacity = cardOpacity
            )
        }
    }
}
