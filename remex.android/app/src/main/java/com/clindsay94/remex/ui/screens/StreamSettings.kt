package com.clindsay94.remex.ui.screens

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.flow.updateAndGet
import kotlinx.coroutines.launch

/**
 * The stream settings' rules, in one place for both sheets (RemEx-172ba): the stream's own
 * [RemoteDesktopViewModel] and the Desktop tab's [PreStreamSettingsViewModel]. Ranges match the
 * sliders; a preset sets quality, frame rate and scale together, never one at a time
 * (docs/REGRESSION-GUARDS.md, "Preset changes are atomic").
 */
internal object StreamSettingsRules {
    fun fromPreferences(prefs: SettingsManager.RemoteDesktopPreferences): RemoteDesktopConfigState =
            RemoteDesktopConfigState(
                    quality = prefs.quality.coerceIn(1, 100),
                    targetFps = prefs.targetFps.coerceIn(1, DESKTOP_MAX_FPS),
                    scale = prefs.scale.coerceIn(0.25f, 1.0f),
                    preset = DesktopPreset.fromId(prefs.preset),
                    adaptiveScale = prefs.adaptiveScale
            )

    fun withQuality(state: RemoteDesktopConfigState, value: Int) = state.copy(quality = value.coerceIn(1, 100))

    fun withTargetFps(state: RemoteDesktopConfigState, value: Int) =
            state.copy(targetFps = value.coerceIn(1, DESKTOP_MAX_FPS))

    fun withScale(state: RemoteDesktopConfigState, value: Float) = state.copy(scale = value.coerceIn(0.25f, 1.0f))

    fun withPreset(state: RemoteDesktopConfigState, preset: DesktopPreset, quality: Int, fps: Int, scale: Float) =
            state.copy(
                    quality = quality.coerceIn(1, 100),
                    targetFps = fps.coerceIn(1, DESKTOP_MAX_FPS),
                    scale = scale.coerceIn(0.25f, 1.0f),
                    preset = preset
            )

    /** Custom keeps the current numbers and only stops calling them a named bundle. */
    fun asCustom(state: RemoteDesktopConfigState) = state.copy(preset = DesktopPreset.CUSTOM)
}

/**
 * The Desktop tab's stream settings before a stream starts (RemEx-172ba). Writes the same saved
 * defaults the stream reads when it opens, so a change here is what the next stream uses. Nothing is
 * sent to the PC: there is no stream to apply it to yet. Deliberately not [RemoteDesktopViewModel],
 * which collects frames, cursors and errors and reconnects on its own.
 */
class PreStreamSettingsViewModel(application: Application) : AndroidViewModel(application) {
    private val settingsManager = SettingsManager(application)

    private val _config = MutableStateFlow(RemoteDesktopConfigState())
    val config: StateFlow<RemoteDesktopConfigState> = _config.asStateFlow()

    val preferences: StateFlow<SettingsManager.RemoteDesktopPreferences> =
            settingsManager.remoteDesktopPreferencesFlow.stateIn(
                    viewModelScope,
                    SharingStarted.WhileSubscribed(5000),
                    SettingsManager.RemoteDesktopPreferences()
            )

    init {
        viewModelScope.launch {
            settingsManager.remoteDesktopPreferencesFlow.collect { _config.value = StreamSettingsRules.fromPreferences(it) }
        }
    }

    fun applyPreset(preset: DesktopPreset, quality: Int, fps: Int, scale: Float) =
            change { StreamSettingsRules.withPreset(it, preset, quality, fps, scale) }

    fun selectCustomPreset() = change { StreamSettingsRules.asCustom(it) }

    fun updateQuality(value: Int) = change { StreamSettingsRules.withQuality(it, value) }

    fun updateTargetFps(value: Int) = change { StreamSettingsRules.withTargetFps(it, value) }

    fun updateScale(value: Float) = change { StreamSettingsRules.withScale(it, value) }

    fun updateAdaptiveScale(enabled: Boolean) {
        _config.update { it.copy(adaptiveScale = enabled) }
        viewModelScope.launch { settingsManager.saveRemoteDesktopAdaptiveScale(enabled) }
    }

    fun updateDirectTouch(enabled: Boolean) {
        viewModelScope.launch { settingsManager.saveRemoteDesktopDirectTouch(enabled) }
    }

    fun updatePointerSpeed(speed: Float) {
        viewModelScope.launch { settingsManager.saveRemoteDesktopPointerSpeed(speed) }
    }

    fun updateCursorScale(value: Float) {
        viewModelScope.launch { settingsManager.saveRemoteDesktopCursorScale(value) }
    }

    fun updateScrollSensitivity(vertical: Float, horizontal: Float) {
        viewModelScope.launch { settingsManager.saveRemoteDesktopScrollSensitivity(vertical, horizontal) }
    }

    private fun change(transform: (RemoteDesktopConfigState) -> RemoteDesktopConfigState) {
        val next = _config.updateAndGet(transform)
        viewModelScope.launch {
            settingsManager.saveRemoteDesktopDefaults(
                    quality = next.quality,
                    targetFps = next.targetFps,
                    scale = next.scale,
                    preset = next.preset.id
            )
        }
    }
}
