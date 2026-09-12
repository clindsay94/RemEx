// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeTonalSpot.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A calm theme, sedated colors that aren't particularly chromatic.</summary>
public sealed class SchemeTonalSpot : DynamicScheme
{
    public SchemeTonalSpot(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.TonalSpot,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 36.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 16.0),
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue + 60.0), 24.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 6.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 8.0))
    {
    }
}
