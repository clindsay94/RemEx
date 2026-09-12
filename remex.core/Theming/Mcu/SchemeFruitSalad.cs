// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeFruitSalad.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A playful theme - the source color's hue does not appear in the theme.</summary>
public sealed class SchemeFruitSalad : DynamicScheme
{
    public SchemeFruitSalad(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.FruitSalad,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue - 50.0), 48.0),
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue - 50.0), 36.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 36.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 10.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 16.0))
    {
    }
}
