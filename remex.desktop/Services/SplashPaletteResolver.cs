using Avalonia.Media;
using Remex.Branding;
using Remex.Core.Models;
using Remex.Desktop.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// Maps a <see cref="LastSeedSidecarData"/> record onto a <see cref="SplashPalette"/> (RemEx-alwfa.1,
/// Connor's decision (c) 2026-09-07). Pure and Skia-free so it is unit-testable without a rendering
/// surface: <see cref="Resolve"/> takes an already-read sidecar record and never touches disk.
/// <see cref="ResolveFromSidecar"/> is the one entry point production code calls — it does the file
/// read too, and never throws: any failure (no sidecar, corrupt JSON, an unparseable seed) falls
/// back to <see cref="SplashPalette.Default"/>, exactly like Android's own
/// <c>SplashPaletteResolver.resolveOrFallback</c> falls back to a static scheme.
/// </summary>
public static class SplashPaletteResolver
{
    /// <summary>WCAG-style floor for keeping the fixed amber accent instead of falling back to
    /// tertiary. Mirrors <c>remex.android/.../SplashPalette.kt</c>'s <c>MinAccentContrast</c>.</summary>
    private const double MinAccentContrast = 3.0;

    /// <summary>Reads the sidecar and resolves it to a palette. Never throws — see the class summary.</summary>
    public static SplashPalette ResolveFromSidecar()
    {
        try
        {
            return LastSeedSidecar.TryRead(out var seed) ? Resolve(seed) : SplashPalette.Default;
        }
        catch
        {
            // Never let a bad sidecar or a resolver bug break the splash's first frame.
            return SplashPalette.Default;
        }
    }

    /// <summary>
    /// Pure: maps an already-read sidecar record onto a palette via the SAME seed engine
    /// (<see cref="DynamicColorGenerator"/>) the rest of the desktop shell uses, so the splash never
    /// shows a colour the app it hands off to is about to contradict. An unparseable seed falls back
    /// to <see cref="SplashPalette.Default"/> rather than throwing.
    /// </summary>
    public static SplashPalette Resolve(LastSeedSidecarData seed)
    {
        if (!Color.TryParse(seed.Seed, out var seedColor)) return SplashPalette.Default;

        bool isLight = seed.Mode switch
        {
            ThemeModes.Light => true,
            ThemeModes.Dark => false,
            // System: the same OS ask ThemeService itself uses (TryGetOsIsLight), reachable here
            // without a Window because it only reads Application.Current.PlatformSettings. Falls
            // back to dark when the platform cannot say, matching ResolveIsLight's own System arm.
            ThemeModes.System => ThemeService.TryGetOsIsLight() ?? false,
            // Unknown/missing mode: dark, the same default every profile painted before ThemeMode
            // existed.
            _ => false,
        };

        var variant = SchemeVariants.Normalize(seed.Variant);
        var palette = DynamicColorGenerator.Generate(seedColor, variant, isDark: !isLight, contrast: seed.Contrast);

        uint surfaceArgb = ToArgb(palette.Surface);
        uint accent = ContrastRatio(RemexBrandData.AmberArgb, surfaceArgb) >= MinAccentContrast
            ? RemexBrandData.AmberArgb
            : ToArgb(palette.Tertiary);

        return new SplashPalette(
            BackdropStart: surfaceArgb,
            BackdropEnd: ToArgb(palette.SurfaceContainerHigh),
            MarkStart: ToArgb(palette.Primary),
            MarkEnd: ToArgb(palette.Tertiary),
            Accent: accent);
    }

    private static uint ToArgb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>WCAG 2.x relative-luminance contrast ratio: (L_lighter + 0.05) / (L_darker + 0.05).
    /// Mirrors the Android resolver's <c>contrastRatio</c> (Compose's <c>Color.luminance()</c>).</summary>
    private static double ContrastRatio(uint a, uint b)
    {
        double la = RelativeLuminance(a) + 0.05;
        double lb = RelativeLuminance(b) + 0.05;
        return Math.Max(la, lb) / Math.Min(la, lb);
    }

    private static double RelativeLuminance(uint argb)
    {
        double r = Linearize(((argb >> 16) & 0xFF) / 255.0);
        double g = Linearize(((argb >> 8) & 0xFF) / 255.0);
        double b = Linearize((argb & 0xFF) / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double Linearize(double c) =>
        c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}
