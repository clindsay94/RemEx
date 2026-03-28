package com.clindsay94.remex.ui.theme

import com.google.android.material.color.utilities.DynamicScheme
import com.google.android.material.color.utilities.Variant
import com.google.android.material.color.utilities.Hct
import com.google.android.material.color.utilities.TonalPalette
import com.google.android.material.color.utilities.MathUtils

/** A playful theme - the source color's hue does not appear in the theme. */
class SchemeFruitSalad(
    sourceColorHct: Hct,
    isDark: Boolean,
    contrastLevel: Double
) : DynamicScheme(
    sourceColorHct,
    Variant.FRUIT_SALAD,
    isDark,
    contrastLevel,
    TonalPalette.fromHueAndChroma(MathUtils.sanitizeDegreesDouble(sourceColorHct.hue + 50.0), 48.0),
    TonalPalette.fromHueAndChroma(MathUtils.sanitizeDegreesDouble(sourceColorHct.hue + 50.0), 36.0),
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 10.0),
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 16.0),
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 24.0)
)

/** A playful theme - the source color's hue does not appear in the theme. */
class SchemeRainbow(
    sourceColorHct: Hct,
    isDark: Boolean,
    contrastLevel: Double
) : DynamicScheme(
    sourceColorHct,
    Variant.RAINBOW,
    isDark,
    contrastLevel,
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 48.0),
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 16.0),
    TonalPalette.fromHueAndChroma(MathUtils.sanitizeDegreesDouble(sourceColorHct.hue + 60.0), 24.0),
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 0.0),
    TonalPalette.fromHueAndChroma(sourceColorHct.hue, 0.0)
)
