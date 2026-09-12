// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeVibrant.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A loud theme, colorfulness is maximum for Primary palette, increased for others.</summary>
public sealed class SchemeVibrant : DynamicScheme
{
    private static readonly double[] Hues = { 0, 41, 61, 101, 131, 181, 251, 301, 360 };
    private static readonly double[] SecondaryRotations = { 18, 15, 10, 12, 15, 18, 15, 12, 12 };
    private static readonly double[] TertiaryRotations = { 35, 30, 20, 25, 30, 35, 30, 25, 25 };

    public SchemeVibrant(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Vibrant,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 200.0),
            TonalPalette.FromHueAndChroma(DynamicScheme.GetRotatedHue(sourceColorHct, Hues, SecondaryRotations), 24.0),
            TonalPalette.FromHueAndChroma(DynamicScheme.GetRotatedHue(sourceColorHct, Hues, TertiaryRotations), 32.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 10.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 12.0))
    {
    }
}
