package com.clindsay94.remex.ui.theme

import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
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

/**
 * Ceiling for the COMBINED font scale (system accessibility scale x in-app scale). The in-app
 * scale multiplies role sizes that are emitted in .sp, and .sp is scaled again by the system
 * setting at render time — so system Largest (1.3) plus in-app max (1.4) used to compound to
 * ~1.82x, clipping single-line ellipsized headers (RemEx-95ls). 1.6x is the largest combined
 * scale the app's headers and chip labels survive without clipping.
 */
internal const val MAX_COMBINED_FONT_SCALE = 1.6f

/**
 * Clamps the in-app font-scale multiplier so system x app never exceeds
 * [MAX_COMBINED_FONT_SCALE] — but never below 1.0: the app must reduce only its OWN
 * contribution, never counteract the system accessibility setting (on API 34+ non-linear
 * scaling the system alone can reach 2.0, and it wins).
 */
internal fun clampedAppFontScale(appFontScale: Float, systemFontScale: Float): Float {
    if (systemFontScale <= 0f) return appFontScale
    val ceiling = (MAX_COMBINED_FONT_SCALE / systemFontScale).coerceAtLeast(1f)
    return appFontScale.coerceAtMost(ceiling)
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

/**
 * Builds the app typography for the chosen font family and in-app scale, including the
 * emphasized roles with the app's canonical emphasis weights (RemEx-0855).
 *
 * Before the emphasized roles existed here, 60+ call sites bolted ad-hoc
 * `fontWeight = FontWeight.X` onto typography roles, with the SAME role carrying
 * different weights on different screens. These values are the majority weight from that
 * pre-migration inventory (ties resolved to SemiBold, the M3-typical emphasis step):
 *
 * - titleMedium/titleSmall: SemiBold 6 vs Bold 3-1 → SemiBold
 * - labelSmall: Bold 9 vs SemiBold/ExtraBold 1 each → Bold
 * - labelLarge: SemiBold 5 vs Bold 5 (tie) → SemiBold
 * - labelMedium: SemiBold 2 vs Bold 2 (tie) → SemiBold
 * - bodyMedium: Bold 4 vs SemiBold 3 → Bold
 * - headline/title-large/body-large and unused roles: Bold, the only weight observed there
 *
 * Emphasized roles are the base role plus this weight — same size, line height, and letter
 * spacing — so migrating a call site from `role + fontWeight` to `roleEmphasized` is
 * pixel-identical wherever the site already used the canonical weight.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
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
        labelSmall = Typography.labelSmall.copy(fontFamily = family).scaled(fontScale),
        // Emphasized roles carry the app's canonical emphasis weights (see KDoc above). They
        // MUST be built here alongside the base roles: Typography.copy() leaves any role not
        // named at its default, which would silently drop the user's font family and scale.
        displayLargeEmphasized = Typography.displayLarge.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        displayMediumEmphasized = Typography.displayMedium.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        displaySmallEmphasized = Typography.displaySmall.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        headlineLargeEmphasized = Typography.headlineLarge.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        headlineMediumEmphasized = Typography.headlineMedium.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        headlineSmallEmphasized = Typography.headlineSmall.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        titleLargeEmphasized = Typography.titleLarge.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        titleMediumEmphasized = Typography.titleMedium.copy(fontFamily = family, fontWeight = FontWeight.SemiBold).scaled(fontScale),
        titleSmallEmphasized = Typography.titleSmall.copy(fontFamily = family, fontWeight = FontWeight.SemiBold).scaled(fontScale),
        bodyLargeEmphasized = Typography.bodyLarge.copy(fontFamily = family, fontWeight = FontWeight.SemiBold).scaled(fontScale),
        bodyMediumEmphasized = Typography.bodyMedium.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        bodySmallEmphasized = Typography.bodySmall.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale),
        labelLargeEmphasized = Typography.labelLarge.copy(fontFamily = family, fontWeight = FontWeight.SemiBold).scaled(fontScale),
        labelMediumEmphasized = Typography.labelMedium.copy(fontFamily = family, fontWeight = FontWeight.SemiBold).scaled(fontScale),
        labelSmallEmphasized = Typography.labelSmall.copy(fontFamily = family, fontWeight = FontWeight.Bold).scaled(fontScale)
    )
}