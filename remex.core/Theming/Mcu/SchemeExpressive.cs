// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeExpressive.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A playful theme - the source color's hue does not appear in the theme.</summary>
public sealed class SchemeExpressive : DynamicScheme
{
    private static readonly double[] Hues = { 0, 21, 51, 121, 151, 191, 271, 321, 360 };
    private static readonly double[] SecondaryRotations = { 45, 95, 45, 20, 45, 90, 45, 45, 45 };
    private static readonly double[] TertiaryRotations = { 120, 120, 20, 45, 20, 15, 20, 120, 120 };

    public SchemeExpressive(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Expressive,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue + 240.0), 40.0),
            TonalPalette.FromHueAndChroma(DynamicScheme.GetRotatedHue(sourceColorHct, Hues, SecondaryRotations), 24.0),
            TonalPalette.FromHueAndChroma(DynamicScheme.GetRotatedHue(sourceColorHct, Hues, TertiaryRotations), 32.0),
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue + 15.0), 8.0),
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue + 15.0), 12.0))
    {
    }
}
