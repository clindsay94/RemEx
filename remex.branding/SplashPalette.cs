namespace Remex.Branding;

/// <summary>
/// The seed-derived splash/mark palette (RemEx-alwfa.1, Connor's decision (c) 2026-09-07): the
/// backdrop is the resolved scheme's surface tone (two stops, for the same diagonal gradient the
/// brand default already paints), the mark is a primary -&gt; tertiary gradient, and
/// <see cref="Accent"/> is the brand amber UNLESS it fails contrast against the recoloured
/// backdrop, in which case the caller substitutes the scheme's tertiary. All five fields are
/// packed ARGB (<c>0xAARRGGBB</c>), matching <see cref="RemexBrandData"/>'s own constants, so this
/// type stays free of any colour-library dependency (remex.branding is consumed by tools too).
/// </summary>
public readonly record struct SplashPalette(
    uint BackdropStart,
    uint BackdropEnd,
    uint MarkStart,
    uint MarkEnd,
    uint Accent)
{
    /// <summary>
    /// The fixed brand colours, unchanged. Used whenever no seed palette is available (no sidecar,
    /// a corrupt one, or a caller — BrandRasterizer/BrandAssetGen — that renders the static asset
    /// rather than the live app). <see cref="MarkStart"/> and <see cref="MarkEnd"/> are both the
    /// brand's window-fill colour, so the mark gradient collapses to the exact solid fill the brand
    /// default always painted.
    /// </summary>
    public static SplashPalette Default { get; } = new(
        RemexBrandData.BackdropStartArgb,
        RemexBrandData.BackdropEndArgb,
        RemexBrandData.WindowFillArgb,
        RemexBrandData.WindowFillArgb,
        RemexBrandData.AmberArgb);
}
