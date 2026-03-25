package com.clindsay94.remex.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Matrix
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asComposePath
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.graphics.shapes.Morph
import androidx.graphics.shapes.RoundedPolygon
import androidx.graphics.shapes.circle
import androidx.graphics.shapes.star
import dev.sasikanth.material.color.utilities.dynamiccolor.MaterialDynamicColors
import dev.sasikanth.material.color.utilities.hct.Hct
import dev.sasikanth.material.color.utilities.scheme.SchemeExpressive
import dev.sasikanth.material.color.utilities.scheme.SchemeMonochrome
import dev.sasikanth.material.color.utilities.scheme.SchemeNeutral
import dev.sasikanth.material.color.utilities.scheme.SchemeTonalSpot
import dev.sasikanth.material.color.utilities.scheme.SchemeVibrant

private val Purple80 = Color(0xFFD0BCFF)
private val PurpleGrey80 = Color(0xFFCCC2DC)
private val Pink80 = Color(0xFFEFB8C8)

private val Purple40 = Color(0xFF6650a4)
private val PurpleGrey40 = Color(0xFF625b71)
private val Pink40 = Color(0xFF7D5260)

private val DarkColorScheme = darkColorScheme(
    primary = Purple80,
    secondary = PurpleGrey80,
    tertiary = Pink80
)

private val LightColorScheme = lightColorScheme(
    primary = Purple40,
    secondary = PurpleGrey40,
    tertiary = Pink40
)

val Shapes = Shapes(
    extraSmall = RoundedCornerShape(4.dp),
    small = RoundedCornerShape(8.dp),
    medium = RoundedCornerShape(12.dp),
    large = RoundedCornerShape(16.dp),
    extraLarge = RoundedCornerShape(28.dp)
)

private val MaterialDynamicColorsInstance = MaterialDynamicColors()

val materialShapesList = listOf(
    RoundedPolygon.circle(numVertices = 4), // Square
    RoundedPolygon.circle(numVertices = 4, cornerRadius = 0.2f), // Rounded Square
    RoundedPolygon.circle(), // Circle
    RoundedPolygon(numVertices = 3), // Triangle
    RoundedPolygon(numVertices = 3, cornerRadius = 0.2f), // Rounded Triangle
    RoundedPolygon(numVertices = 5), // Pentagon
    RoundedPolygon(numVertices = 6), // Hexagon
    RoundedPolygon(numVertices = 8), // Octagon
    RoundedPolygon.star(numVerticesPerRadius = 5, innerRadiusRatio = 0.5f), // Star
    RoundedPolygon.star(numVerticesPerRadius = 5, innerRadiusRatio = 0.5f, innerRadiusRoundingRadius = 0.2f, radius = 1f, roundingRadius = 0.2f), // Soft Star
    RoundedPolygon.star(numVerticesPerRadius = 8, innerRadiusRatio = 0.8f), // Cog
    RoundedPolygon.star(numVerticesPerRadius = 4, innerRadiusRatio = 0.3f), // Cross
    RoundedPolygon.star(numVerticesPerRadius = 12, innerRadiusRatio = 0.9f), // Certificate
    RoundedPolygon.star(numVerticesPerRadius = 4, innerRadiusRatio = 0.5f), // Diamond
    RoundedPolygon.star(numVerticesPerRadius = 6, innerRadiusRatio = 0.7f), // Flower
    RoundedPolygon.star(numVerticesPerRadius = 10, innerRadiusRatio = 0.6f) // Burst
)

class MorphPolygonShape(
    private val morph: Morph,
    private val progress: Float
) : Shape {
    private val matrix = Matrix()

    override fun createOutline(
        size: Size,
        layoutDirection: LayoutDirection,
        density: Density
    ): Outline {
        val androidPath = android.graphics.Path()
        morph.toPath(progress, androidPath)
        
        val composePath = androidPath.asComposePath()
        
        matrix.reset()
        val bounds = morph.calculateBounds()
        val scaleX = size.width / (bounds[2] - bounds[0])
        val scaleY = size.height / (bounds[3] - bounds[1])
        
        matrix.translate(size.width / 2f, size.height / 2f)
        matrix.scale(scaleX, scaleY)
        
        composePath.transform(matrix)
        return Outline.Generic(composePath)
    }
}

fun cardShape(index: Float, cornerRadiusDp: Int): Shape {
    if (materialShapesList.isEmpty()) {
        return RoundedCornerShape(cornerRadiusDp.dp)
    }
    val maxIndex = materialShapesList.size - 1
    val safeIndex = index.coerceIn(0f, maxIndex.toFloat())
    
    val startIndex = safeIndex.toInt()
    val endIndex = (startIndex + 1).coerceAtMost(maxIndex)
    val progress = safeIndex - startIndex
    
    val startPolygon = materialShapesList[startIndex]
    val endPolygon = materialShapesList[endIndex]
    
    val morph = Morph(startPolygon, endPolygon)
    return MorphPolygonShape(morph, progress)
}

fun colorSchemeFromSeed(seedColor: Color, darkTheme: Boolean, style: String = "tonal_spot"): ColorScheme {
    val hct = Hct.fromInt(seedColor.toArgb())
    val scheme = when (style.lowercase()) {
        "expressive" -> SchemeExpressive(hct, darkTheme, 0.0)
        "vibrant" -> SchemeVibrant(hct, darkTheme, 0.0)
        "neutral" -> SchemeNeutral(hct, darkTheme, 0.0)
        "monochrome" -> SchemeMonochrome(hct, darkTheme, 0.0)
        else -> SchemeTonalSpot(hct, darkTheme, 0.0)
    }
    val m3 = MaterialDynamicColorsInstance

    return if (darkTheme) {
        darkColorScheme(
            primary = Color(m3.primary().getArgb(scheme)),
            onPrimary = Color(m3.onPrimary().getArgb(scheme)),
            primaryContainer = Color(m3.primaryContainer().getArgb(scheme)),
            onPrimaryContainer = Color(m3.onPrimaryContainer().getArgb(scheme)),
            secondary = Color(m3.secondary().getArgb(scheme)),
            onSecondary = Color(m3.onSecondary().getArgb(scheme)),
            secondaryContainer = Color(m3.secondaryContainer().getArgb(scheme)),
            onSecondaryContainer = Color(m3.onSecondaryContainer().getArgb(scheme)),
            tertiary = Color(m3.tertiary().getArgb(scheme)),
            onTertiary = Color(m3.onTertiary().getArgb(scheme)),
            tertiaryContainer = Color(m3.tertiaryContainer().getArgb(scheme)),
            onTertiaryContainer = Color(m3.onTertiaryContainer().getArgb(scheme)),
            error = Color(m3.error().getArgb(scheme)),
            onError = Color(m3.onError().getArgb(scheme)),
            errorContainer = Color(m3.errorContainer().getArgb(scheme)),
            onErrorContainer = Color(m3.onErrorContainer().getArgb(scheme)),
            background = Color(m3.background().getArgb(scheme)),
            onBackground = Color(m3.onBackground().getArgb(scheme)),
            surface = Color(m3.surface().getArgb(scheme)),
            onSurface = Color(m3.onSurface().getArgb(scheme)),
            surfaceVariant = Color(m3.surfaceVariant().getArgb(scheme)),
            onSurfaceVariant = Color(m3.onSurfaceVariant().getArgb(scheme)),
            outline = Color(m3.outline().getArgb(scheme)),
            outlineVariant = Color(m3.outlineVariant().getArgb(scheme)),
            scrim = Color(m3.scrim().getArgb(scheme)),
            inverseSurface = Color(m3.inverseSurface().getArgb(scheme)),
            inverseOnSurface = Color(m3.inverseOnSurface().getArgb(scheme)),
            inversePrimary = Color(m3.inversePrimary().getArgb(scheme))
        )
    } else {
        lightColorScheme(
            primary = Color(m3.primary().getArgb(scheme)),
            onPrimary = Color(m3.onPrimary().getArgb(scheme)),
            primaryContainer = Color(m3.primaryContainer().getArgb(scheme)),
            onPrimaryContainer = Color(m3.onPrimaryContainer().getArgb(scheme)),
            secondary = Color(m3.secondary().getArgb(scheme)),
            onSecondary = Color(m3.onSecondary().getArgb(scheme)),
            secondaryContainer = Color(m3.secondaryContainer().getArgb(scheme)),
            onSecondaryContainer = Color(m3.onSecondaryContainer().getArgb(scheme)),
            tertiary = Color(m3.tertiary().getArgb(scheme)),
            onTertiary = Color(m3.onTertiary().getArgb(scheme)),
            tertiaryContainer = Color(m3.tertiaryContainer().getArgb(scheme)),
            onTertiaryContainer = Color(m3.onTertiaryContainer().getArgb(scheme)),
            error = Color(m3.error().getArgb(scheme)),
            onError = Color(m3.onError().getArgb(scheme)),
            errorContainer = Color(m3.errorContainer().getArgb(scheme)),
            onErrorContainer = Color(m3.onErrorContainer().getArgb(scheme)),
            background = Color(m3.background().getArgb(scheme)),
            onBackground = Color(m3.onBackground().getArgb(scheme)),
            surface = Color(m3.surface().getArgb(scheme)),
            onSurface = Color(m3.onSurface().getArgb(scheme)),
            surfaceVariant = Color(m3.surfaceVariant().getArgb(scheme)),
            onSurfaceVariant = Color(m3.onSurfaceVariant().getArgb(scheme)),
            outline = Color(m3.outline().getArgb(scheme)),
            outlineVariant = Color(m3.outlineVariant().getArgb(scheme)),
            scrim = Color(m3.scrim().getArgb(scheme)),
            inverseSurface = Color(m3.inverseSurface().getArgb(scheme)),
            inverseOnSurface = Color(m3.inverseOnSurface().getArgb(scheme)),
            inversePrimary = Color(m3.inversePrimary().getArgb(scheme))
        )
    }
}

@Composable
fun RemExTheme(
    themeMode: String = "system",
    themePalette: String = "default",
    themeStyle: String = "tonal_spot",
    themeSeedColor: String = "#6750A4",
    fontFamilyKey: String = "default",
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit
) {
    val darkTheme = when (themeMode.lowercase()) {
        "dark" -> true
        "light" -> false
        else -> isSystemInDarkTheme()
    }

    val seedColor = try {
        Color(android.graphics.Color.parseColor(themeSeedColor))
    } catch (_: Exception) {
        Color(0xFF6750A4)
    }

    val colorScheme = when {
        themePalette.equals("custom", ignoreCase = true) -> colorSchemeFromSeed(seedColor, darkTheme, themeStyle)
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
            val context = LocalContext.current
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        }
        darkTheme -> DarkColorScheme
        else -> LightColorScheme
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = typographyForFontFamily(fontFamilyKey),
        shapes = Shapes,
        content = content
    )
}
