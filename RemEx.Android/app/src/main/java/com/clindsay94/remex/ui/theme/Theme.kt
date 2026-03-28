package com.clindsay94.remex.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialTheme
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

/**
 * Curated Material 3 Expressive shape set.
 * Allowed shapes: square, pentagon, circle, puffy, clover4leaf, pill,
 * slanted, diamond, soft burst, arrow, gem, 4-sided cookie, bun,
 * clamshell, ghost-ish, arch.
 */
val materialShapesList: List<RoundedPolygon> = listOf(
    // 0: Square
    RoundedPolygon(numVertices = 4, rounding = CornerRounding(0.05f)),
    // 1: Pentagon
    RoundedPolygon(numVertices = 5, rounding = CornerRounding(0.1f)),
    // 2: Circle
    RoundedPolygon.circle(),
    // 3: Puffy (inflated quad)
    RoundedPolygon.star(
        numVerticesPerRadius = 4, innerRadius = 0.85f,
        rounding = CornerRounding(0.6f), innerRounding = CornerRounding(0.6f)
    ),
    // 4: Clover 4-leaf
    RoundedPolygon.star(
        numVerticesPerRadius = 4, innerRadius = 0.4f,
        rounding = CornerRounding(0.5f), innerRounding = CornerRounding(0.1f)
    ),
    // 5: Pill
    RoundedPolygon(numVertices = 4, rounding = CornerRounding(1.0f)),
    // 6: Slanted (parallelogram)
    RoundedPolygon(
        vertices = floatArrayOf(-0.7f, -1f, 1f, -1f, 0.7f, 1f, -1f, 1f),
        rounding = CornerRounding(0.15f)
    ),
    // 7: Diamond
    RoundedPolygon.star(
        numVerticesPerRadius = 2, innerRadius = 0.5f,
        rounding = CornerRounding(0.1f)
    ),
    // 8: Soft Burst
    RoundedPolygon.star(
        numVerticesPerRadius = 8, innerRadius = 0.75f,
        rounding = CornerRounding(0.15f), innerRounding = CornerRounding(0.15f)
    ),
    // 9: Arrow
    RoundedPolygon(
        vertices = floatArrayOf(
            0f, -1f, 0.6f, -0.2f, 0.3f, -0.2f,
            0.3f, 1f, -0.3f, 1f, -0.3f, -0.2f, -0.6f, -0.2f
        ),
        rounding = CornerRounding(0.1f)
    ),
    // 10: Gem
    RoundedPolygon(numVertices = 6, rounding = CornerRounding(0.2f)),
    // 11: 4-sided Cookie
    RoundedPolygon.star(
        numVerticesPerRadius = 4, innerRadius = 0.6f,
        rounding = CornerRounding(0.3f), innerRounding = CornerRounding(0.3f)
    ),
    // 12: Bun
    RoundedPolygon.star(
        numVerticesPerRadius = 3, innerRadius = 0.75f,
        rounding = CornerRounding(0.5f), innerRounding = CornerRounding(0.5f)
    ),
    // 13: Clamshell
    RoundedPolygon(
        vertices = floatArrayOf(
            -1f, 0.5f, -0.7f, -0.5f, 0f, -1f,
            0.7f, -0.5f, 1f, 0.5f, 0f, 1f
        ),
        rounding = CornerRounding(0.3f)
    ),
    // 14: Ghost-ish
    RoundedPolygon(
        vertices = floatArrayOf(
            -0.8f, -1f, 0.8f, -1f, 0.8f, 0.6f, 0.5f, 1f,
            0.2f, 0.6f, 0f, 1f, -0.2f, 0.6f, -0.5f, 1f, -0.8f, 0.6f
        ),
        rounding = CornerRounding(0.25f)
    ),
    // 15: Arch
    RoundedPolygon(
        vertices = floatArrayOf(
            -1f, 1f, -1f, -0.3f, -0.5f, -1f,
            0.5f, -1f, 1f, -0.3f, 1f, 1f
        ),
        rounding = CornerRounding(0.4f)
    ),
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

        // Fix orientation: Rotate by -45 degrees to align square/diamond correctly
        // Doing this BEFORE calculateBounds ensures the shape fills the container properly.
        matrix.reset()
        matrix.rotateZ(-45f)
        composePath.transform(matrix)

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
private val morphCache = LinkedHashMap<Long, Morph>(16, 0.75f, true)
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
    val morph = morphCache.getOrPut(cacheKey) {
        if (morphCache.size >= MORPH_CACHE_MAX) {
            morphCache.remove(morphCache.keys.first())
        }
        Morph(materialShapesList[startIndex], materialShapesList[endIndex])
    }

    return MorphPolygonShape(morph, progress)
}

fun colorSchemeFromSeed(
    seedColor: Color,
    darkTheme: Boolean,
    style: String = "tonal_spot"
): ColorScheme {
    val hct = Hct.fromInt(seedColor.toArgb())
    val scheme = when (style.lowercase()) {
        "expressive" -> SchemeExpressive(hct, darkTheme, 0.0)
        "vibrant" -> SchemeVibrant(hct, darkTheme, 0.0)
        "neutral" -> SchemeNeutral(hct, darkTheme, 0.0)
        "monochrome" -> SchemeMonochrome(hct, darkTheme, 0.0)
        "fruit_salad" -> SchemeFruitSalad(hct, darkTheme, 0.0)
        "rainbow" -> SchemeRainbow(hct, darkTheme, 0.0)
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
        themePalette.equals("custom", ignoreCase = true) -> colorSchemeFromSeed(
            seedColor,
            darkTheme,
            themeStyle
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
