// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeRainbow.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A playful theme - the source color's hue does not appear in the theme.</summary>
public sealed class SchemeRainbow : DynamicScheme
{
    public SchemeRainbow(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Rainbow,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 48.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 16.0),
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue + 60.0), 24.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0))
    {
    }
}
