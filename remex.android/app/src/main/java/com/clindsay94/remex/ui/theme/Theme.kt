package com.clindsay94.remex.ui.theme

import android.annotation.SuppressLint
import android.app.UiModeManager
import android.content.Context
import android.database.ContentObserver
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.runtime.remember
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Matrix
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.Modifier
import androidx.compose.material3.*
import androidx.compose.foundation.lazy.grid.*
import androidx.graphics.shapes.Morph
import androidx.graphics.shapes.RoundedPolygon
import com.google.android.material.color.utilities.Hct
import com.google.android.material.color.utilities.MaterialDynamicColors
import com.google.android.material.color.utilities.SchemeExpressive
import com.google.android.material.color.utilities.SchemeMonochrome
import com.google.android.material.color.utilities.SchemeNeutral
import com.google.android.material.color.utilities.SchemeTonalSpot
import com.google.android.material.color.utilities.SchemeVibrant

import com.clindsay94.remex.R

/**
 * RemEx brand seed — the launcher/splash amber (see SplashBrand.Amber and
 * ic_launcher_background's slate backdrop). The static fallback schemes below derive from it
 * so pre-Android-S devices and dynamic-color-off users get a branded scheme instead of the
 * Compose-template purple.
 */
private val BrandSeed = Color(0xFFFFB63D)

// Lazy is load-bearing: colorSchemeFromSeed reads top-level vals declared later in this file,
// and Kotlin initializes top-level properties in file order — eager init here crashes the app
// at class-load with an NPE inside the Material color utilities (verified on-device).
// internal (not private) so FallbackSchemeBrandHueTest can assert the brand hue — the API<31
// path these serve cannot be exercised on a modern emulator.
internal val DarkColorScheme by lazy { colorSchemeFromSeed(BrandSeed, darkTheme = true) }

internal val LightColorScheme by lazy { colorSchemeFromSeed(BrandSeed, darkTheme = false) }

@Immutable
data class CustomColors(
    val success: Color,
    val onSuccess: Color,
    val successContainer: Color,
    val onSuccessContainer: Color
)

val LocalCustomColors = staticCompositionLocalOf {
    CustomColors(
        success = Color.Unspecified,
        onSuccess = Color.Unspecified,
        successContainer = Color.Unspecified,
        onSuccessContainer = Color.Unspecified
    )
}

/**
 * Success roles derived through the same DynamicScheme pipeline as [colorSchemeFromSeed]: a
 * green tonal-spot scheme (seeded from the former hardcoded light success #386A20, so the hue
 * family is unchanged) built for the current darkTheme + contrast. Its
 * primary/onPrimary/primaryContainer/onPrimaryContainer become the four success roles, so
 * success now tracks dark mode and the user's contrast setting like every other role instead
 * of staying frozen at fixed hex values. Deliberately NOT seeded from the user's theme seed:
 * success is a semantic color and must stay green.
 *
 * A function rather than top-level vals: Theme.kt top-level property init order is fragile
 * (see the DarkColorScheme lazy note above) and callers cache via remember anyway.
 */
internal fun customColorsForScheme(darkTheme: Boolean, contrast: Double): CustomColors {
    val successSeed = Hct.fromInt(0xFF386A20.toInt())
    val scheme = SchemeTonalSpot(successSeed, darkTheme, contrast)
    val m3 = MaterialDynamicColorsInstance
    return CustomColors(
        success = Color(m3.primary().getArgb(scheme)),
        onSuccess = Color(m3.onPrimary().getArgb(scheme)),
        successContainer = Color(m3.primaryContainer().getArgb(scheme)),
        onSuccessContainer = Color(m3.onPrimaryContainer().getArgb(scheme)),
    )
}

/**
 * Squircle crop for app-icon-like previews (tutorial demo tiles, the About logo, Settings'
 * app icon). Percent-based so it scales with the element; not part of the M3 dp scale, which
 * is why it lives here as a named token rather than as per-site literals (RemEx-flro).
 */
val remexIconSquircle = RoundedCornerShape(percent = 30)

val remexShapes = Shapes(
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

    // Index 24 - plain rounded rectangle for content-heavy cards (PC status). Handled as a
    // discrete special case in cardShape(), not a real morph target; the placeholder entry here
    // only keeps materialShapesList/materialShapeNames/shapeSafety index-aligned.
    MaterialShapes.Square,
)

/** Human-readable name resource IDs that correspond 1-to-1 with [materialShapesList]. */
val materialShapeNames: List<Int> = listOf(
    R.string.shape_circle, R.string.shape_square, R.string.shape_triangle,
    R.string.shape_diamond, R.string.shape_pentagon, R.string.shape_arch,
    R.string.shape_semi_circle, R.string.shape_pill, R.string.shape_slanted,
    R.string.shape_fan, R.string.shape_clam_shell, R.string.shape_gem,
    R.string.shape_heart, R.string.shape_flower, R.string.shape_puffy,
    R.string.shape_puffy_diamond, R.string.shape_ghostish, R.string.shape_oval,
    R.string.shape_clover_4_leaf, R.string.shape_clover_8_leaf, R.string.shape_sunny,
    R.string.shape_soft_burst, R.string.shape_soft_boom, R.string.shape_pixel_circle,
    R.string.shape_rounded_rectangle
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
    // RoundedRectangle (24) is discrete, not a morph target - the morph engine needs a real
    // RoundedPolygon at each endpoint, so snap the top of the slider range straight to it instead
    // of ever morphing into the index-24 placeholder entry in materialShapesList.
    if (index >= 23.5f) {
        return RoundedCornerShape(cornerRadiusDp.dp)
    }
    // -2, not -1: the last slot is the non-morphable RoundedRectangle placeholder (handled above).
    val maxIndex = (materialShapesList.size - 2).coerceAtLeast(0)
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
        0.55f, // Circle (corners cut off significantly)
        1.00f, // Square (perfectly safe)
        0.30f, // Triangle (extreme corner clipping)
        0.45f, // Diamond (heavy corner clipping)
        0.65f, // Pentagon
        0.75f, // Arch
        0.65f, // Semi-Circle
        0.85f, // Pill
        0.80f, // Slanted
        0.70f, // Fan
        0.65f, // Clam Shell
        0.55f, // Gem
        0.40f, // Heart (top-center and bottom-center clipping)
        0.50f, // Flower
        0.65f, // Puffy
        0.55f, // Puffy Diamond
        0.65f, // Ghostish
        0.65f, // Oval
        0.55f, // 4-Leaf Clover
        0.55f, // 8-Leaf Clover
        0.45f, // Sunny
        0.55f, // Soft Burst
        0.65f, // Soft Boom
        0.75f, // Pixel Circle
        0.97f  // Rounded Rectangle (plain RoundedCornerShape, no polygon clipping)
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
            surfaceBright = Color(m3.surfaceBright().getArgb(scheme)),
            surfaceDim = Color(m3.surfaceDim().getArgb(scheme)),
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
            surfaceBright = Color(m3.surfaceBright().getArgb(scheme)),
            surfaceDim = Color(m3.surfaceDim().getArgb(scheme)),
            outline = Color(m3.outline().getArgb(scheme)),
            outlineVariant = Color(m3.outlineVariant().getArgb(scheme)),
            scrim = Color(m3.scrim().getArgb(scheme)),
            inverseSurface = Color(m3.inverseSurface().getArgb(scheme)),
            inverseOnSurface = Color(m3.inverseOnSurface().getArgb(scheme)),
            inversePrimary = Color(m3.inversePrimary().getArgb(scheme))
        )
    }
}

/**
 * True when the system "Remove animations" accessibility setting is on (animator duration
 * scale = 0). The theme already swaps to the calmer standard [MotionScheme] automatically;
 * screens with self-driven choreography (splash zooms, coach-mark loops, infinite pulses)
 * should additionally consult this and suppress or shorten that motion themselves.
 */
val LocalReducedMotion = staticCompositionLocalOf { false }

/**
 * Maps the system animator duration scale to the app-wide [MotionScheme]: expressive springs
 * normally, the non-bouncy standard scheme when the user has disabled animations (scale = 0).
 */
internal fun motionSchemeForAnimatorScale(animatorDurationScale: Float): MotionScheme =
    if (animatorDurationScale == 0f) MotionScheme.standard() else MotionScheme.expressive()

/**
 * Reads Settings.Global.ANIMATOR_DURATION_SCALE and stays current via a ContentObserver, so
 * toggling "Remove animations" while the app is running re-themes without a restart.
 */
@Composable
private fun rememberAnimatorDurationScale(): Float {
    val context = LocalContext.current
    fun read(): Float = Settings.Global.getFloat(
        context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f
    )
    var scale by remember(context) { mutableFloatStateOf(read()) }
    DisposableEffect(context) {
        val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) {
                scale = read()
            }
        }
        context.contentResolver.registerContentObserver(
            Settings.Global.getUriFor(Settings.Global.ANIMATOR_DURATION_SCALE), false, observer
        )
        onDispose { context.contentResolver.unregisterContentObserver(observer) }
    }
    return scale
}

/**
 * System contrast (-1..1) from UiModeManager on API 34+, kept live via a
 * ContrastChangeListener; 0 (default) below API 34. Lets the success palette track the SAME
 * contrast source as the main scheme when dynamic color is active (RemEx-84dj).
 */
@Composable
private fun rememberSystemContrast(): Float {
    if (Build.VERSION.SDK_INT < 34) return 0f
    val context = LocalContext.current
    val uiModeManager = remember(context) {
        context.getSystemService(Context.UI_MODE_SERVICE) as UiModeManager
    }
    var contrast by remember { mutableFloatStateOf(uiModeManager.contrast) }
    DisposableEffect(uiModeManager) {
        val listener = UiModeManager.ContrastChangeListener { c -> contrast = c }
        uiModeManager.addContrastChangeListener(context.mainExecutor, listener)
        onDispose { uiModeManager.removeContrastChangeListener(listener) }
    }
    return contrast
}

// WARNING: This file imports com.google.android.material.color.utilities.* (Hct, Hct.from, etc.)
// which are @RestrictTo(LIBRARY_GROUP) internals of the Material library. They are extremely
// sensitive to material version bumps. Keep material version pinned to 1.14.0 in libs.versions.toml
// to avoid runtime link errors or compilation breakage.
@SuppressLint("RestrictedApi")
@Composable
fun RemExTheme(
    themeMode: String = "system",
    themePalette: String = "default",
    themeStyle: String = "tonal_spot",
    themeSeedColor: String = "#6750A4",
    themeSeedChroma: Float = 48.0f,
    themeContrast: Float = 0.0f,
    fontFamilyKey: String = "default",
    fontScale: Float = 1.0f,
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit
) {
    val darkTheme = when (themeMode.lowercase()) {
        "dark" -> true
        "light" -> false
        else -> isSystemInDarkTheme()
    }

    // Clamp the in-app scale so it cannot compound with the system font-size setting past
    // MAX_COMBINED_FONT_SCALE (RemEx-95ls); the system's own scale is always fully honored.
    val systemFontScale = LocalDensity.current.fontScale
    val effectiveFontScale = clampedAppFontScale(fontScale, systemFontScale)
    val typography = remember(fontFamilyKey, effectiveFontScale) {
        typographyForFontFamily(fontFamilyKey, effectiveFontScale)
    }

    val seedColor = remember(themeSeedColor, themePalette, themeSeedChroma) {
        try {
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
    }

    val colorScheme = when {
        themePalette.equals("custom", ignoreCase = true) ->
            remember(seedColor, darkTheme, themeStyle, themeContrast) {
                colorSchemeFromSeed(
                    seedColor,
                    darkTheme,
                    themeStyle,
                    themeContrast.toDouble()
                )
            }

        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
            val context = LocalContext.current
            remember(darkTheme, context) {
                if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
            }
        }

        darkTheme -> DarkColorScheme
        else -> LightColorScheme
    }

    // When dynamic color drives the main scheme, the success palette follows the SYSTEM
    // contrast (the same source the dynamic scheme responds to); otherwise the app's
    // themeContrast, matching the custom/static paths (RemEx-84dj).
    val usesDynamicScheme = !themePalette.equals("custom", ignoreCase = true) &&
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S
    val successContrast = if (usesDynamicScheme) rememberSystemContrast() else themeContrast
    val customColors = remember(darkTheme, successContrast) {
        customColorsForScheme(darkTheme, successContrast.toDouble())
    }

    val animatorDurationScale = rememberAnimatorDurationScale()
    CompositionLocalProvider(
        LocalCustomColors provides customColors,
        LocalReducedMotion provides (animatorDurationScale == 0f),
    ) {
        MaterialTheme(
            colorScheme = colorScheme,
            typography = typography,
            shapes = remexShapes,
            motionScheme = remember(animatorDurationScale) {
                motionSchemeForAnimatorScale(animatorDurationScale)
            },
            content = content
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun RemExThemePreview() {
    RemExTheme {
        Surface(modifier = Modifier.fillMaxSize()) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Text("RemEx Theme", style = MaterialTheme.typography.headlineMedium)

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Box(modifier = Modifier.size(40.dp).background(MaterialTheme.colorScheme.primary, CircleShape))
                    Box(modifier = Modifier.size(40.dp).background(MaterialTheme.colorScheme.secondary, CircleShape))
                    Box(modifier = Modifier.size(40.dp).background(MaterialTheme.colorScheme.tertiary, CircleShape))
                    Box(modifier = Modifier.size(40.dp).background(MaterialTheme.colorScheme.error, CircleShape))
                }

                Button(onClick = {}) { Text("Primary Button") }
                ElevatedCard(modifier = Modifier.fillMaxWidth()) {
                    Text("Elevated Card", modifier = Modifier.padding(16.dp))
                }

                Text("Shapes", style = MaterialTheme.typography.titleMedium)
                LazyVerticalGrid(columns = GridCells.Fixed(4), verticalArrangement = Arrangement.spacedBy(8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(8) { index ->
                        Box(
                            modifier = Modifier
                                .aspectRatio(1f)
                                .clip(cardShape(index.toFloat(), 12))
                                .background(MaterialTheme.colorScheme.primaryContainer)
                        )
                    }
                }
            }
        }
    }
}
