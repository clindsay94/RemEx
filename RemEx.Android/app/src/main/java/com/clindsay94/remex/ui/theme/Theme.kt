package com.clindsay94.remex.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

private val DarkColorScheme = darkColorScheme(
    primary = Purple80,
    secondary = PurpleGrey80,
    tertiary = Pink80
)

private val LightColorScheme = lightColorScheme(
    primary = Purple40,
    secondary = PurpleGrey40,
    tertiary = Pink40

    /* Other default colors to override
    background = Color(0xFFFFFBFE),
    surface = Color(0xFFFFFBFE),
    onPrimary = Color.White,
    onSecondary = Color.White,
    onTertiary = Color.White,
    onBackground = Color(0xFF1C1B1F),
    onSurface = Color(0xFF1C1B1F),
    */
)

private val CyberDarkScheme = darkColorScheme(
    primary = Color(0xFF00F3FF),
    secondary = Color(0xFF5EF0FF),
    tertiary = Color(0xFFFF00FF),
    background = Color(0xFF050505),
    surface = Color(0xFF101115)
)

private val SolarLightScheme = lightColorScheme(
    primary = Color(0xFFFFB800),
    secondary = Color(0xFFFF8A00),
    tertiary = Color(0xFFFFD166),
    background = Color(0xFFFFF8F1),
    surface = Color(0xFFFFFFFF)
)

private val MonolithDarkScheme = darkColorScheme(
    primary = Color(0xFF0A84FF),
    secondary = Color(0xFF6BA8FF),
    tertiary = Color(0xFF9DBDFF),
    background = Color(0xFF1E1F22),
    surface = Color(0xFF2C2C2E)
)

@Composable
fun RemExTheme(
    themeMode: String = "system",
    themePalette: String = "default",
    // Dynamic color is available on Android 12+
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit
) {
    val darkTheme = when (themeMode.lowercase()) {
        "dark" -> true
        "light" -> false
        else -> isSystemInDarkTheme()
    }

    val colorScheme = when {
        themePalette.equals("cyber", ignoreCase = true) -> CyberDarkScheme
        themePalette.equals("solar", ignoreCase = true) -> if (darkTheme) DarkColorScheme else SolarLightScheme
        themePalette.equals("monolith", ignoreCase = true) -> MonolithDarkScheme
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
            val context = LocalContext.current
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        }

        darkTheme -> DarkColorScheme
        else -> LightColorScheme
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content
    )
}