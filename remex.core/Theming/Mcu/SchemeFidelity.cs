// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeFidelity.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A scheme that places the source color in Scheme.primaryContainer.</summary>
public sealed class SchemeFidelity : DynamicScheme
{
    public SchemeFidelity(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Fidelity,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, Math.Max(sourceColorHct.Chroma - 32.0, sourceColorHct.Chroma * 0.5)),
            TonalPalette.FromHct(DislikeAnalyzer.FixIfDisliked(new TemperatureCache(sourceColorHct).GetComplement())),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma / 8.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma / 8.0 + 4.0))
    {
    }
}
