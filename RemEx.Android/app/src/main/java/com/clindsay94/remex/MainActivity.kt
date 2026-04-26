package com.clindsay94.remex

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.collectAsState
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.ui.screens.PersonalizationViewModel
import com.clindsay94.remex.ui.navigation.AppNavigation
import com.clindsay94.remex.ui.theme.RemExTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            val personalizationViewModel: PersonalizationViewModel = viewModel()
            val personalization by personalizationViewModel.personalization.collectAsState()
            
            var splashShown by rememberSaveable { mutableStateOf(false) }
            
            if (personalization != null) {
                val prefs = personalization!!
                RemExTheme(
                    themeMode = prefs.themeMode,
                    themePalette = prefs.themePalette,
                    themeStyle = prefs.themeStyle,
                    themeSeedColor = prefs.themeSeedColor,
                    themeSeedChroma = prefs.themeSeedChroma,
                    themeContrast = prefs.themeContrast,
                    fontFamilyKey = prefs.fontFamily
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
