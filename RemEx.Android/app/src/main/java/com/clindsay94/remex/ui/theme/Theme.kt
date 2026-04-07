package com.clindsay94.remex.ui.theme

import android.graphics.Matrix as AndroidMatrix
import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MotionScheme
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

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
val materialShapesList: List<RoundedPolygon> = listOf(
    // Geometric — clean, always readable
    MaterialShapes.Circle,
    MaterialShapes.Square,
    MaterialShapes.Triangle,
    MaterialShapes.Diamond,
    MaterialShapes.Pentagon,

    // Expressive / Organic — usable interior area
    MaterialShapes.Arch,
    MaterialShapes.SemiCircle,
    MaterialShapes.Pill,
    MaterialShapes.Slanted,
    MaterialShapes.Fan,
    MaterialShapes.ClamShell,
    MaterialShapes.Gem,
    MaterialShapes.Heart,
    MaterialShapes.Flower,
    MaterialShapes.Puffy,
    MaterialShapes.PuffyDiamond,
    MaterialShapes.Ghostish,
    MaterialShapes.Oval,

    // Decorative — kept only where interior is still usable for data cards
    MaterialShapes.Clover4Leaf,
    MaterialShapes.Clover8Leaf,
    MaterialShapes.Sunny,       // gentle star — acceptable
    MaterialShapes.SoftBurst,   // soft star — usable
    MaterialShapes.SoftBoom,    // soft balloon — usable
    MaterialShapes.PixelCircle, // fun but legible

    // Removed: Cookie4/6/7/9/12Sided (scalloped edges clip content),
    //          VerySunny / Burst / Boom (too many spikes), PixelTriangle (awkward aspect),
    //          Arrow (directional, terrible for data cards)
)

/** Human-readable names that correspond 1-to-1 with [materialShapesList]. */
val materialShapeNames: List<String> = listOf(
    "Circle", "Square", "Triangle", "Diamond", "Pentagon",
    "Arch", "Semi-Circle", "Pill", "Slanted", "Fan",
    "Clam Shell", "Gem", "Heart", "Flower", "Puffy",
    "Puffy Diamond", "Ghostish", "Oval",
    "4-Leaf Clover", "8-Leaf Clover", "Sunny",
    "Soft Burst", "Soft Boom", "Pixel Circle"
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

    @OptIn(ExperimentalMaterial3ExpressiveApi::class)
    MaterialTheme(
        colorScheme = colorScheme,
        typography = typographyForFontFamily(fontFamilyKey),
        shapes = Shapes,
        motionScheme = MotionScheme.expressive(),
        content = content
    )
}
