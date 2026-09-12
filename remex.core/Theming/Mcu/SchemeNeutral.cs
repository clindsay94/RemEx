// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeNeutral.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A theme that's slightly more chromatic than monochrome, which is purely black / white / gray.</summary>
public sealed class SchemeNeutral : DynamicScheme
{
    public SchemeNeutral(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Neutral,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 12.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 8.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 16.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 2.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 2.0))
    {
    }
}
