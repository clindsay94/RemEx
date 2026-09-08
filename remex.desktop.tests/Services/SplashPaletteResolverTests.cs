using FluentAssertions;
using Remex.Branding;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins <see cref="SplashPaletteResolver"/> (RemEx-alwfa.1, decision (c)): backdrop = surface,
/// mark = primary -&gt; tertiary, and the amber/tertiary contrast fallback. <see cref="SplashPaletteResolver.Resolve"/> is
/// pure (no Skia, no disk), so these construct <see cref="LastSeedSidecarData"/> records directly
/// rather than going through the sidecar file.
/// </summary>
public sealed class SplashPaletteResolverTests
{
    private const string SampleSeed = "#6C4CFF";

    private static LastSeedSidecarData Seed(string mode, string hex = SampleSeed) =>
        new(hex, SchemeVariants.TonalSpot, mode, Contrast: 0.0);

    [Fact]
    public void Resolve_LightAndDarkModes_ProduceDistinctBackdropAndMarkColours()
    {
        var light = SplashPaletteResolver.Resolve(Seed(ThemeModes.Light));
        var dark = SplashPaletteResolver.Resolve(Seed(ThemeModes.Dark));

        light.BackdropStart.Should().NotBe(dark.BackdropStart);
        light.BackdropEnd.Should().NotBe(dark.BackdropEnd);
        light.MarkStart.Should().NotBe(dark.MarkStart);
        light.MarkEnd.Should().NotBe(dark.MarkEnd);
    }

    [Fact]
    public void Resolve_BackdropAndMark_ComeFromSurfaceAndPrimaryTertiary()
    {
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Dark));
        var m3 = DynamicColorGenerator.Generate(
            Avalonia.Media.Color.Parse(SampleSeed), SchemeVariants.TonalSpot, isDark: true, contrast: 0.0);

        palette.BackdropStart.Should().Be(ToArgb(m3.Surface));
        palette.BackdropEnd.Should().Be(ToArgb(m3.SurfaceContainerHigh));
        palette.MarkStart.Should().Be(ToArgb(m3.Primary));
        palette.MarkEnd.Should().Be(ToArgb(m3.Tertiary));
    }

    [Fact]
    public void Resolve_DarkSurface_AccentClearsSurfaceAndNeverEqualsMarkEnd()
    {
        // Was "KeepsBrandAmber": that assumed amber-vs-Surface was the only floor that mattered. It
        // is not — the accent elements paint OVER the mark's own gradient fill (SplashBrand.DrawMark:
        // Window fill first, then Dot1/Chevron/Cursor on top), whose end stop is Tertiary, and a dark
        // scheme's Tertiary role sits at a light, hue-independent tone band by M3 convention, so it
        // can still clash with amber even on a near-black dark Surface. For THIS seed it does — the
        // widened rule (RemEx-alwfa.1, Opus review MEDIUM) correctly falls back here instead of
        // keeping an amber that would have been hard to see over the mark's end.
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Dark));
        var m3 = DynamicColorGenerator.Generate(
            Avalonia.Media.Color.Parse(SampleSeed), SchemeVariants.TonalSpot, isDark: true, contrast: 0.0);

        // Pins the actual regression this widened rule fixes: amber passes the OLD Surface-only
        // gate here (a near-black dark Surface), so a Surface-only check would wrongly keep it even
        // though it fails the MarkEnd floor.
        palette.Accent.Should().NotBe(RemexBrandData.AmberArgb);
        ContrastRatio(palette.Accent, ToArgb(m3.Surface)).Should().BeGreaterThanOrEqualTo(3.0);
        // The bug this fixes: a Tertiary fallback is drawn in the EXACT colour of MarkEnd (MarkEnd
        // IS Tertiary), which is 1:1 contrast against itself — invisible wherever it overlaps. The
        // resolver must never return that colour.
        palette.Accent.Should().NotBe(ToArgb(m3.Tertiary));
    }

    [Fact]
    public void Resolve_LightSurface_FallsBackToOnSurface_WhenAmberFailsContrast()
    {
        // Light mode's surface tone is always near-white (M3's own convention, independent of the
        // seed), and amber-against-near-white measures well under the 3.0 WCAG-style floor — so the
        // light arm is exactly "a seed that makes the backdrop light amber-ish" for any seed.
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Light));

        palette.Accent.Should().NotBe(RemexBrandData.AmberArgb);
        var m3 = DynamicColorGenerator.Generate(
            Avalonia.Media.Color.Parse(SampleSeed), SchemeVariants.TonalSpot, isDark: false, contrast: 0.0);

        // NOT Tertiary any more (RemEx-alwfa.1, Opus review MEDIUM): Tertiary IS MarkEnd, so it can
        // never legibly stand in for the accent over the mark's own fill — the candidate ladder
        // ([Tertiary, OnSurface], each checked against BOTH Surface and MarkEnd) always cascades past
        // it to OnSurface in practice, since Tertiary-vs-MarkEnd is a fixed 1:1 self-comparison.
        palette.Accent.Should().Be(ToArgb(m3.OnSurface));

        // The chosen fallback has to be legible where the accent elements are actually drawn: over
        // the mark's gradient (which ends at Tertiary), not just over the Surface backdrop. The
        // Surface floor is comfortably met (OnSurface/Surface is a high-contrast M3 pair by
        // construction). The MarkEnd floor is the ladder's best-effort axis ("else the candidate
        // with the highest minimum of the two") and for THIS seed does not clear 3.0 either
        // (measured ~2.65:1) — see the Deviations note this test's author left for the exact
        // handoff conflict: the literal [Tertiary, OnSurface] ladder cannot guarantee >= 3.0 against
        // MarkEnd for every seed, only that it beats the 1:1 vanish Tertiary itself would produce.
        ContrastRatio(palette.Accent, ToArgb(m3.Surface)).Should().BeGreaterThanOrEqualTo(3.0);
        palette.Accent.Should().NotBe(ToArgb(m3.Tertiary));
        ContrastRatio(palette.Accent, ToArgb(m3.Tertiary)).Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void Resolve_NearWhiteSweepSeed_LightMode_AccentNeverVanishesOverMarkEnd()
    {
        // "Chalk" (#F5F5F5) from scripts/ui-palette-sweep.ps1 -ListCells: the near-white seed the
        // sweep uses to stress the generator. In light mode this is the shape that exposed the
        // amber-vanishes-over-the-mark-fill bug (RemEx-alwfa.1, Opus review MEDIUM) — Surface and
        // MarkEnd (Tertiary) can land close enough together that a fallback picked against Surface
        // alone still fails against MarkEnd.
        //
        // The candidate ladder's own contract is best-effort past its first choice ("else the
        // candidate with the highest minimum of the two") — it does not promise 3.0 against MarkEnd
        // for every seed, and this near-white stress seed is exactly the case where OnSurface still
        // falls short of 3.0 against Tertiary/MarkEnd (measured ~2.66:1 here). What IS guaranteed,
        // and is the actual bug being fixed: the Surface floor still holds, and the accent is never
        // drawn in MarkEnd's own colour (the literal 1:1 vanish).
        const string ChalkSeed = "#F5F5F5";
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Light, hex: ChalkSeed));
        var m3 = DynamicColorGenerator.Generate(
            Avalonia.Media.Color.Parse(ChalkSeed), SchemeVariants.TonalSpot, isDark: false, contrast: 0.0);

        ContrastRatio(palette.Accent, ToArgb(m3.Surface)).Should().BeGreaterThanOrEqualTo(3.0);
        palette.Accent.Should().NotBe(ToArgb(m3.Tertiary));
        ContrastRatio(palette.Accent, ToArgb(m3.Tertiary)).Should().BeGreaterThan(1.0);
    }

    /// <summary>WCAG 2.x relative-luminance contrast ratio, mirroring the private helper under test
    /// in <see cref="SplashPaletteResolver"/> so these assertions do not need internals access.</summary>
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

    [Fact]
    public void Resolve_UnparseableSeed_ReturnsDefault()
    {
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Dark, hex: "not-a-colour"));

        palette.Should().Be(SplashPalette.Default);
    }

    [Fact]
    public void ResolveFromSidecar_WithNoSidecarFile_ReturnsDefault()
    {
        if (File.Exists(LastSeedSidecar.FilePath)) File.Delete(LastSeedSidecar.FilePath);

        SplashPaletteResolver.ResolveFromSidecar().Should().Be(SplashPalette.Default);
    }

    [Fact]
    public void ResolveFromSidecar_WithCorruptSidecar_ReturnsDefault()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastSeedSidecar.FilePath)!);
        File.WriteAllText(LastSeedSidecar.FilePath, "{ not json");

        try
        {
            SplashPaletteResolver.ResolveFromSidecar().Should().Be(SplashPalette.Default);
        }
        finally
        {
            File.Delete(LastSeedSidecar.FilePath);
        }
    }

    /// <summary>
    /// The splash calls <see cref="SplashPaletteResolver.ResolveFromSidecar"/> synchronously on the
    /// UI thread before its first frame (<c>SkiaSplashControl.OnAttachedToVisualTree</c>) — a tiny
    /// JSON read plus one <see cref="DynamicColorGenerator.Generate"/> call, the same cost class
    /// <c>ThemeService.ApplyCustomization</c> already pays on every launch and every slider drag. NOT
    /// a wall-clock pin (removed, RemEx-alwfa.1 Opus review MEDIUM: flake class RemEx-w7ei) — CI
    /// timing is not the machine's actual budget, so this just asserts the real-sidecar path resolves
    /// without throwing.
    /// </summary>
    [Fact]
    public async Task ResolveFromSidecar_WithRealSidecar_ResolvesWithoutThrowing()
    {
        await LastSeedSidecar.WriteAsync(new CustomizationSettings
        {
            AccentColor = SampleSeed,
            SchemeVariant = SchemeVariants.TonalSpot,
            ThemeMode = ThemeModes.Dark,
            ThemeContrast = 0.0,
        });

        try
        {
            var palette = SplashPaletteResolver.ResolveFromSidecar();

            palette.Should().NotBe(SplashPalette.Default);
        }
        finally
        {
            File.Delete(LastSeedSidecar.FilePath);
        }
    }

    private static uint ToArgb(Avalonia.Media.Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
