package com.clindsay94.remex.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.googlefonts.Font
import androidx.compose.ui.text.googlefonts.GoogleFont
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.R

val provider = GoogleFont.Provider(
    providerAuthority = "com.google.android.gms.fonts",
    providerPackage = "com.google.android.gms",
    certificates = R.array.com_google_android_gms_fonts_certs
)

fun getGoogleFontFamily(fontName: String): FontFamily {
    val font = GoogleFont(fontName)
    return FontFamily(
        Font(googleFont = font, fontProvider = provider),
        Font(googleFont = font, fontProvider = provider, weight = FontWeight.Medium),
        Font(googleFont = font, fontProvider = provider, weight = FontWeight.Bold)
    )
}

// Set of Material typography styles to start with
val Typography = Typography(
    bodyLarge = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 16.sp,
        lineHeight = 24.sp,
        letterSpacing = 0.5.sp
    )
)

fun typographyForFontFamily(fontFamilyKey: String, fontScale: Float = 1.0f): Typography {
    val family = when (fontFamilyKey.lowercase()) {
        "sans" -> FontFamily.SansSerif
        "serif" -> FontFamily.Serif
        "mono" -> FontFamily.Monospace
        "cursive" -> FontFamily.Cursive
        "roboto" -> getGoogleFontFamily("Roboto")
        "lato" -> getGoogleFontFamily("Lato")
        "montserrat" -> getGoogleFontFamily("Montserrat")
        "oswald" -> getGoogleFontFamily("Oswald")
        "poppins" -> getGoogleFontFamily("Poppins")
        "playfair" -> getGoogleFontFamily("Playfair Display")
        "merriweather" -> getGoogleFontFamily("Merriweather")
        "inter" -> getGoogleFontFamily("Inter")
        "outfit" -> getGoogleFontFamily("Outfit")
        "space_grotesk" -> getGoogleFontFamily("Space Grotesk")
        "syne" -> getGoogleFontFamily("Syne")
        "lexend" -> getGoogleFontFamily("Lexend")
        "jetbrains_mono" -> getGoogleFontFamily("JetBrains Mono")
        else -> FontFamily.Default
    }

    fun TextStyle.scaled(scale: Float): TextStyle {
        return this.copy(
            fontSize = (this.fontSize.value * scale).sp,
            lineHeight = (this.lineHeight.value * scale).sp
        )
    }

    return Typography.copy(
        displayLarge = Typography.displayLarge.copy(fontFamily = family).scaled(fontScale),
        displayMedium = Typography.displayMedium.copy(fontFamily = family).scaled(fontScale),
        displaySmall = Typography.displaySmall.copy(fontFamily = family).scaled(fontScale),
        headlineLarge = Typography.headlineLarge.copy(fontFamily = family).scaled(fontScale),
        headlineMedium = Typography.headlineMedium.copy(fontFamily = family).scaled(fontScale),
        headlineSmall = Typography.headlineSmall.copy(fontFamily = family).scaled(fontScale),
        titleLarge = Typography.titleLarge.copy(fontFamily = family).scaled(fontScale),
        titleMedium = Typography.titleMedium.copy(fontFamily = family).scaled(fontScale),
        titleSmall = Typography.titleSmall.copy(fontFamily = family).scaled(fontScale),
        bodyLarge = Typography.bodyLarge.copy(fontFamily = family).scaled(fontScale),
        bodyMedium = Typography.bodyMedium.copy(fontFamily = family).scaled(fontScale),
        bodySmall = Typography.bodySmall.copy(fontFamily = family).scaled(fontScale),
        labelLarge = Typography.labelLarge.copy(fontFamily = family).scaled(fontScale),
        labelMedium = Typography.labelMedium.copy(fontFamily = family).scaled(fontScale),
        labelSmall = Typography.labelSmall.copy(fontFamily = family).scaled(fontScale)
    )
}