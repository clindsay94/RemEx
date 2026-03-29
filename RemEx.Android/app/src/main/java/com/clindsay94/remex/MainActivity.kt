package com.clindsay94.remex

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.navigation.AppNavigation
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.widget.WidgetDataCache
import androidx.compose.runtime.collectAsState
import kotlinx.coroutines.flow.map

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        RemexClientManager.initialize(this)
        WidgetDataCache.startCaching(this)
        setContent {
            val settingsManager = remember { SettingsManager(this) }
            val themeMode by settingsManager.themeModeFlow.collectAsState(initial = "system")
            val themePalette by settingsManager.themePaletteFlow.collectAsState(initial = "default")
            val themeStyle by settingsManager.personalizationPreferencesFlow.map { it.themeStyle }.collectAsState(initial = "tonal_spot")
            val themeSeedColor by settingsManager.themeSeedColorFlow.collectAsState(initial = "#6750A4")
            val fontFamily by settingsManager.fontFamilyFlow.collectAsState(initial = "default")
            val fontScale by settingsManager.fontScaleFlow.collectAsState(initial = 1.0f)

            RemExTheme(
                themeMode = themeMode,
                themePalette = themePalette,
                themeStyle = themeStyle,
                themeSeedColor = themeSeedColor,
                fontFamilyKey = fontFamily
            ) {
                val density = LocalDensity.current
                CompositionLocalProvider(
                    LocalDensity provides Density(density = density.density, fontScale = fontScale)
                ) {
                    AppNavigation()
                }
            }
        }
    }
}
