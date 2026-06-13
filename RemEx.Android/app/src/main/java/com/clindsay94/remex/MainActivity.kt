package com.clindsay94.remex

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.ui.screens.PersonalizationViewModel
import com.clindsay94.remex.ui.navigation.AppNavigation
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.widget.WidgetDataCache

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        RemexClientManager.initialize(this)
        WidgetDataCache.startCaching(this)
        setContent {
            val personalizationViewModel: PersonalizationViewModel = viewModel()
            val personalization by personalizationViewModel.personalization.collectAsStateWithLifecycle()
            
            var splashShown by rememberSaveable { mutableStateOf(false) }
            
            val prefs = personalization
            if (prefs != null) {
                RemExTheme(
                    themeMode = prefs.themeMode,
                    themePalette = prefs.themePalette,
                    themeStyle = prefs.themeStyle,
                    themeSeedColor = prefs.themeSeedColor,
                    themeSeedChroma = prefs.themeSeedChroma,
                    themeContrast = prefs.themeContrast,
                    fontFamilyKey = prefs.fontFamily,
                    fontScale = prefs.fontScale
                ) {
                    AppNavigation(
                        splashShown = splashShown,
                        onMarkSplashShown = { splashShown = true }
                    )
                }
            } else {
                // Fallback to default theme until prefs are loaded
                RemExTheme {
                    AppNavigation(
                        splashShown = splashShown,
                        onMarkSplashShown = { splashShown = true }
                    )
                }
            }
        }
    }
}
