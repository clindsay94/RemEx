package com.clindsay94.remex

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.clindsay94.remex.ui.navigation.AppNavigation
import com.clindsay94.remex.ui.theme.RemExTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            RemExTheme {
                AppNavigation()
            }
        }
    }
}
