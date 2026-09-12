// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeMonochrome.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A monochrome theme, colors are purely black / white / gray.</summary>
public sealed class SchemeMonochrome : DynamicScheme
{
    public SchemeMonochrome(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Monochrome,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0))
    {
    }
}
