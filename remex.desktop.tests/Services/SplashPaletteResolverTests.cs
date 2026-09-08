using System.Diagnostics;
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
    public void Resolve_DarkSurface_KeepsBrandAmber()
    {
        // A dark surface is near-black; amber's contrast against it is high, well past the 3.0 floor.
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Dark));

        palette.Accent.Should().Be(RemexBrandData.AmberArgb);
    }

    [Fact]
    public void Resolve_LightSurface_FallsBackToTertiary_WhenAmberFailsContrast()
    {
        // Light mode's surface tone is always near-white (M3's own convention, independent of the
        // seed), and amber-against-near-white measures well under the 3.0 WCAG-style floor — so the
        // light arm is exactly "a seed that makes the backdrop light amber-ish" for any seed.
        var palette = SplashPaletteResolver.Resolve(Seed(ThemeModes.Light));

        palette.Accent.Should().NotBe(RemexBrandData.AmberArgb);
        var m3 = DynamicColorGenerator.Generate(
            Avalonia.Media.Color.Parse(SampleSeed), SchemeVariants.TonalSpot, isDark: false, contrast: 0.0);
        palette.Accent.Should().Be(ToArgb(m3.Tertiary));
    }

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
    /// UI thread before its first frame (<c>SkiaSplashControl.OnAttachedToVisualTree</c>). A tiny
    /// JSON read plus one <see cref="DynamicColorGenerator.Generate"/> call is the same cost
    /// <c>ThemeService.ApplyCustomization</c> already pays on every launch and every slider drag, so
    /// this pins it rather than merely asserting it by argument: comfortably under the 5 ms budget.
    /// </summary>
    [Fact]
    public async Task ResolveFromSidecar_CompletesWellUnderFiveMilliseconds()
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
            // One warm-up call so JIT tiering does not count against the budget it will not pay in
            // production either (the type is already loaded by the time the splash attaches).
            SplashPaletteResolver.ResolveFromSidecar();

            var sw = Stopwatch.StartNew();
            SplashPaletteResolver.ResolveFromSidecar();
            sw.Stop();

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(5));
        }
        finally
        {
            File.Delete(LastSeedSidecar.FilePath);
        }
    }

    private static uint ToArgb(Avalonia.Media.Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
