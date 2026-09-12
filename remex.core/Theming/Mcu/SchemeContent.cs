// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeContent.java
namespace Remex.Core.Theming.Mcu;

/// <summary>A scheme that places the source color in Scheme.primaryContainer, with an analogous tertiary container.</summary>
public sealed class SchemeContent : DynamicScheme
{
    public SchemeContent(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Content,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, Math.Max(sourceColorHct.Chroma - 32.0, sourceColorHct.Chroma * 0.5)),
            TonalPalette.FromHct(DislikeAnalyzer.FixIfDisliked(
                new TemperatureCache(sourceColorHct).GetAnalogousColors(count: 3, divisions: 6)[2])),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma / 8.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma / 8.0 + 4.0))
    {
    }
}
