package com.clindsay94.remex.ui.theme

import android.graphics.Matrix as AndroidMatrix
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
import androidx.graphics.shapes.CornerRounding
import androidx.graphics.shapes.Morph
import androidx.graphics.shapes.RoundedPolygon
import androidx.graphics.shapes.circle
import androidx.graphics.shapes.star
import androidx.graphics.shapes.transformed
import com.google.android.material.color.utilities.MaterialDynamicColors
import com.google.android.material.color.utilities.Hct
import com.google.android.material.color.utilities.SchemeExpressive
import com.google.android.material.color.utilities.SchemeMonochrome
import com.google.android.material.color.utilities.SchemeNeutral
import com.google.android.material.color.utilities.SchemeTonalSpot
import com.google.android.material.color.utilities.SchemeVibrant

// Standard M3 colors are already defined in Color.kt

private val DarkColorScheme = darkColorScheme(
    primary = Purple80,
    secondary = PurpleGrey80,
    tertiary = Pink80,
    surfaceContainerHighest = Color(0xFF36343B),
    surfaceContainerHigh = Color(0xFF2B2930),
    surfaceContainer = Color(0xFF211F26),
    surfaceContainerLow = Color(0xFF1D1B20),
    surfaceContainerLowest = Color(0xFF0F0D13)
)

private val LightColorScheme = lightColorScheme(
    primary = Purple40,
    secondary = PurpleGrey40,
    tertiary = Pink40,
    surfaceContainerHighest = Color(0xFFE6E1E5),
    surfaceContainerHigh = Color(0xFFECE6EA),
    surfaceContainer = Color(0xFFF3EDF1),
    surfaceContainerLow = Color(0xFFF7F2F7),
    surfaceContainerLowest = Color(0xFFFFFFFF)
)

val remexShapes = Shapes(
    extraSmall = RoundedCornerShape(4.dp),
    small = RoundedCornerShape(8.dp),
    medium = RoundedCornerShape(12.dp),
    large = RoundedCornerShape(16.dp),
    extraLarge = RoundedCornerShape(28.dp)
)

private val MaterialDynamicColorsInstance = MaterialDynamicColors()

val materialShapesList: List<RoundedPolygon> = listOf(
    // Geometric — clean, always readable
    RoundedPolygon.circle(numVertices = 32),
    RoundedPolygon(numVertices = 4, rounding = CornerRounding(0.1f)),
    RoundedPolygon(numVertices = 3, rounding = CornerRounding(0.1f)),
    RoundedPolygon(numVertices = 4, rounding = CornerRounding(0.05f)).transformed(
        Matrix().apply { rotateZ(45f) }),
    RoundedPolygon(numVertices = 5, rounding = CornerRounding(0.1f)),
    RoundedPolygon(numVertices = 6, rounding = CornerRounding(0.1f)),
    RoundedPolygon(numVertices = 8, rounding = CornerRounding(0.1f)),
    RoundedPolygon.rectangle(width = 2f, height = 1f, rounding = CornerRounding(0.4f)),
    star(numVerticesPerRadius = 4, innerRadius = 0.5f, rounding = CornerRounding(0.1f)),
    star(numVerticesPerRadius = 5, innerRadius = 0.55f, rounding = CornerRounding(0.1f)),
    star(numVerticesPerRadius = 6, innerRadius = 0.65f, rounding = CornerRounding(0.1f)),
    star(numVerticesPerRadius = 8, innerRadius = 0.75f, rounding = CornerRounding(0.1f)),
)

/** Human-readable names that correspond 1-to-1 with [materialShapesList]. */
val materialShapeNames: List<String> = listOf(
    "Circle", "Square", "Triangle", "Diamond", "Pentagon",
    "Hexagon", "Octagon", "Pill",
    "Star 4", "Star 5", "Star 6", "Star 8"
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
        val composePath = androidx.compose.ui.graphics.Path()

        var first = true
        morph.forEachCubic(progress) { bezier ->
            if (first) {
                composePath.moveTo(bezier.anchor0X, bezier.anchor0Y)
                first = false
            }
            composePath.cubicTo(
                bezier.control0X, bezier.control0Y,
                bezier.control1X, bezier.control1Y,
                bezier.anchor1X, bezier.anchor1Y
            )
        }
        composePath.close()

        val bounds = composePath.getBounds()

        // Scale and translate the path to fit EXACTLY into the `size`
        val scaleX = size.width / bounds.width
        val scaleY = size.height / bounds.height

        matrix.reset()
        matrix.scale(scaleX, scaleY)
        matrix.translate(-bounds.left, -bounds.top)
        
        composePath.transform(matrix)

        return Outline.Generic(composePath)
    }
}

/** LRU cache for Morph objects to avoid per-recomposition allocation. */
private val morphCache: MutableMap<Long, Morph> = java.util.Collections.synchronizedMap(
    LinkedHashMap<Long, Morph>(16, 0.75f, true)
)
private const val MORPH_CACHE_MAX = 32

fun cardShape(index: Float, cornerRadiusDp: Int): Shape {
    if (materialShapesList.isEmpty()) {
        return RoundedCornerShape(cornerRadiusDp.dp)
    }
    val maxIndex = materialShapesList.size - 1
    val safeIndex = index.coerceIn(0f, maxIndex.toFloat())

    val startIndex = safeIndex.toInt()
    val endIndex = (startIndex + 1).coerceAtMost(maxIndex)
    val progress = safeIndex - startIndex

    // Cache key combines start and end polygon indices
    val cacheKey = (startIndex.toLong() shl 32) or endIndex.toLong()
    val morph = synchronized(morphCache) {
        morphCache.getOrPut(cacheKey) {
            if (morphCache.size >= MORPH_CACHE_MAX) {
                morphCache.remove(morphCache.keys.first())
            }
            Morph(materialShapesList[startIndex], materialShapesList[endIndex])
        }
    }

    return MorphPolygonShape(morph, progress)
}

fun calculateAdaptivePadding(shapePreset: Float): androidx.compose.ui.unit.Dp {
    val shapeIndex = shapePreset.toInt()
    val progress = shapePreset - shapeIndex

    // Define "safety" of each shape. 1.0 = safe (square), 0.0 = very unsafe (extreme clipping)
    val shapeSafety = listOf(
        0.55f, // Circle
        1.00f, // Square
        0.30f, // Triangle
        0.45f, // Diamond
        0.65f, // Pentagon
        0.75f, // Hexagon
        0.80f, // Octagon
        0.85f, // Pill
        0.50f, // Star 4
        0.55f, // Star 5
        0.60f, // Star 6
        0.65f  // Star 8
    )

    val currentSafety = shapeSafety.getOrElse(shapeIndex) { 0.6f }
    val nextSafety = shapeSafety.getOrElse(shapeIndex + 1) { currentSafety }
    val safety = currentSafety + (nextSafety - currentSafety) * progress

    // Base padding is 8dp, max padding for unsafe shapes is 24dp
    return androidx.compose.ui.unit.lerp(24.dp, 8.dp, safety)
}

fun colorSchemeFromSeed(
    seedColor: Color,
    darkTheme: Boolean,
    style: String = "tonal_spot",
    contrast: Double = 0.0
): ColorScheme {
    val hct = Hct.fromInt(seedColor.toArgb())
    val scheme = when (style.lowercase()) {
        "expressive" -> SchemeExpressive(hct, darkTheme, contrast)
        "vibrant" -> SchemeVibrant(hct, darkTheme, contrast)
        "neutral" -> SchemeNeutral(hct, darkTheme, contrast)
        "monochrome" -> SchemeMonochrome(hct, darkTheme, contrast)
        "fruit_salad" -> SchemeFruitSalad(hct, darkTheme, contrast)
        "rainbow" -> SchemeRainbow(hct, darkTheme, contrast)
        else -> SchemeTonalSpot(hct, darkTheme, contrast)
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
            surfaceContainerHighest = Color(m3.surfaceContainerHighest().getArgb(scheme)),
            surfaceContainerHigh = Color(m3.surfaceContainerHigh().getArgb(scheme)),
            surfaceContainer = Color(m3.surfaceContainer().getArgb(scheme)),
            surfaceContainerLow = Color(m3.surfaceContainerLow().getArgb(scheme)),
            surfaceContainerLowest = Color(m3.surfaceContainerLowest().getArgb(scheme)),
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
            surfaceContainerHighest = Color(m3.surfaceContainerHighest().getArgb(scheme)),
            surfaceContainerHigh = Color(m3.surfaceContainerHigh().getArgb(scheme)),
            surfaceContainer = Color(m3.surfaceContainer().getArgb(scheme)),
            surfaceContainerLow = Color(m3.surfaceContainerLow().getArgb(scheme)),
            surfaceContainerLowest = Color(m3.surfaceContainerLowest().getArgb(scheme)),
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
    themeSeedChroma: Float = 48.0f,
    themeContrast: Float = 0.0f,
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
        val baseColor = android.graphics.Color.parseColor(themeSeedColor)
        if (themePalette.equals("custom", ignoreCase = true)) {
            val baseHct = Hct.fromInt(baseColor)
            Color(Hct.from(baseHct.hue, themeSeedChroma.toDouble(), baseHct.tone).toInt())
        } else {
            Color(baseColor)
        }
    } catch (_: Exception) {
        Color(0xFF6750A4)
    }

    val colorScheme = when {
        themePalette.equals("custom", ignoreCase = true) -> colorSchemeFromSeed(
            seedColor,
            darkTheme,
            themeStyle,
            themeContrast.toDouble()
        )

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
        shapes = remexShapes,
        content = content
    )
}
