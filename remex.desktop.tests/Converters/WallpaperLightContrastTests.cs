using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Converters;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Tests.Services;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Light mode over a wallpaper must read as a light surface (RemEx-pp4cm.23).
/// </summary>
/// <remarks>
/// <para>
/// The 3.0 palette sweep ran every light cell over Connor's own wallpaper profile and found every page
/// washed out: the Acrylic floor of 0.55 left the light Surface at a mid grey over a dark picture, so
/// cards read as translucent grey and secondary and muted text lost their contrast. Dark cells were fine.
/// </para>
/// <para>
/// WHAT IS MEASURED. The wallpaper veil is the solved Surface (GlassBaseDarkBrush) at
/// <see cref="VeilOpacityConverter.WallpaperLightFloor"/> over the picture, then a card is
/// SurfaceContainer at the Card Opacity alpha over that. The worst picture for the light palette's dark
/// ink is black, so the veil is composited over black. Compositing is per channel in sRGB, which is what
/// Skia does. A card between "no card" and "opaque SurfaceContainer" only moves the ground between those
/// two, and dark text's contrast is monotone in the ground's luminance, so both ends are checked.
/// </para>
/// <para>
/// Body text (OnSurface = TextPrimary, OnSurfaceVariant = TextSecondary) must clear 4.5:1. The muted role
/// (Outline = TextMuted) is held to the 3:1 large-text/UI bar: M3's Outline does not reach 4.5:1 even on
/// the bare Surface, so 4.5:1 would be a requirement on the palette generator, not on the veil.
/// </para>
/// </remarks>
public class WallpaperLightContrastTests
{
    private const double BodyMinimum = 4.5;
    private const double MutedMinimum = 3.0;

    /// <summary>The sweep's seeds (scripts/ui-palette-sweep.ps1) plus every seeded preset.</summary>
    private static IEnumerable<string> Seeds()
    {
        var seeds = new List<string> { "#6C4CFF", "#F5F5F5", "#0B0B0F", "#00FF00" };
        seeds.AddRange(ThemeDictionary.SelectThemeCases().Where(c => c.IsSeeded).Select(c => c.Seed));
        return seeds.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> Cells() =>
        from seed in Seeds()
        from variant in SchemeVariants.All
        from contrast in new[] { 0.0, 1.0 }
        select new object[] { seed, variant, contrast };

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Ratio(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Over(Color top, double alpha, Color under)
    {
        byte Mix(byte t, byte u) => (byte)Math.Round(t * alpha + u * (1 - alpha));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static DynamicColorGenerator.M3Palette Light(string seed, string variant, double contrast)
    {
        Assert.True(Color.TryParse(seed, out var c), $"unparseable seed {seed}");
        return DynamicColorGenerator.Generate(c, variant, isDark: false, contrast: contrast);
    }

    /// <summary>The worst ground a light page can get over a wallpaper: veil over black, then the
    /// two card extremes on top of it.</summary>
    private static IEnumerable<(string Where, Color Ground)> Grounds(DynamicColorGenerator.M3Palette p, double veil)
    {
        var page = Over(p.Surface, veil, Colors.Black);
        yield return ("page", page);
        yield return ("faint card", Over(p.SurfaceContainer, 0.05, page));
        yield return ("opaque card", p.SurfaceContainer);
    }

    [Fact]
    public void TheSweepIsNotEmpty()
    {
        // Anti-vacuity: an empty theory would pass every cell it never ran.
        Cells().Count().Should().BeGreaterThanOrEqualTo(4 * SchemeVariants.All.Count * 2);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void LightTextOverABlackWallpaper_MeetsAA(string seed, string variant, double contrast)
    {
        var p = Light(seed, variant, contrast);
        var veil = (double)VeilOpacityConverter.Wallpaper.Convert(
            new List<object?> { 0.0, Avalonia.Styling.ThemeVariant.Light }, typeof(double), "1.0",
            System.Globalization.CultureInfo.InvariantCulture)!;
        veil.Should().Be(VeilOpacityConverter.WallpaperLightFloor, "a zero window-opacity knob falls to the floor");

        foreach (var (where, ground) in Grounds(p, veil))
        {
            Ratio(p.OnSurface, ground).Should().BeGreaterThanOrEqualTo(BodyMinimum, $"primary text on the {where}");
            Ratio(p.OnSurfaceVariant, ground).Should().BeGreaterThanOrEqualTo(BodyMinimum, $"secondary text on the {where}");
            Ratio(p.Outline, ground).Should().BeGreaterThanOrEqualTo(MutedMinimum, $"muted text on the {where}");
        }
    }

    /// <summary>The old shared floor is what the sweep caught; it must stay caught.</summary>
    [Fact]
    public void TheAcrylicFloor_WouldFailOverABlackWallpaper()
    {
        var p = Light("#6C4CFF", "TonalSpot", 0.0);
        var page = Over(p.Surface, VeilOpacityConverter.LightFloor, Colors.Black);
        Ratio(p.Outline, page).Should().BeLessThan(MutedMinimum,
            "if 0.55 were enough this test would be measuring the wrong thing");
    }

    [Fact]
    public void DarkMode_KeepsTheUserKnobUnfloored()
    {
        VeilOpacityConverter.Wallpaper.Convert(
            new List<object?> { 0.1, Avalonia.Styling.ThemeVariant.Dark }, typeof(double), "1.0",
            System.Globalization.CultureInfo.InvariantCulture).Should().Be(0.1);
    }
}
