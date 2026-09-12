namespace Remex.Core.Theming.Mcu;

/// <summary>The one seam callers use: seed + variant + mode + contrast → the scheme (palettes) or every role (ARGB).</summary>
public static class McuScheme
{
    public static DynamicScheme Create(uint seedArgb, SchemeVariant variant, bool isDark, double contrastLevel)
    {
        var hct = Hct.FromInt(seedArgb);
        return variant switch
        {
            SchemeVariant.Monochrome => new SchemeMonochrome(hct, isDark, contrastLevel),
            SchemeVariant.Neutral => new SchemeNeutral(hct, isDark, contrastLevel),
            SchemeVariant.TonalSpot => new SchemeTonalSpot(hct, isDark, contrastLevel),
            SchemeVariant.Vibrant => new SchemeVibrant(hct, isDark, contrastLevel),
            SchemeVariant.Expressive => new SchemeExpressive(hct, isDark, contrastLevel),
            SchemeVariant.Fidelity => new SchemeFidelity(hct, isDark, contrastLevel),
            SchemeVariant.Content => new SchemeContent(hct, isDark, contrastLevel),
            SchemeVariant.Rainbow => new SchemeRainbow(hct, isDark, contrastLevel),
            SchemeVariant.FruitSalad => new SchemeFruitSalad(hct, isDark, contrastLevel),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

    public static MaterialRoles Build(uint seedArgb, SchemeVariant variant, bool isDark, double contrastLevel)
        => MaterialRoles.From(Create(seedArgb, variant, isDark, contrastLevel));
}
