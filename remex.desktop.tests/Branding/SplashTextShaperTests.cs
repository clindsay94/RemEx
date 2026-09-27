using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Branding;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

/// <summary>
/// Splash text is shaped with font fallback (RemEx-8g6n0.2 review, HIGH): drawn with one SKFont, the
/// Hindi status line and any Devanagari nickname came out as missing-glyph boxes (glyph 0).
/// </summary>
public class SplashTextShaperTests
{
    [Fact]
    public void TheHindiStatusLineShapesWithNoMissingGlyphs()
    {
        // Needs at least one installed face that covers Devanagari (Windows ships Nirmala UI). On a
        // box with none there is nothing to fall back to, and "no boxes" cannot be asked of it.
        using var devanagari = SKFontManager.Default.MatchCharacter('क');
        if (OperatingSystem.IsWindows())
            devanagari.Should().NotBeNull("Windows ships Nirmala UI, so this test must really run there");
        if (devanagari is null || !devanagari.ContainsGlyph('क')) return;

        var hi = HindiStrings();
        var lines = new[]
        {
            string.Format(hi["Splash_LiveHandshake_LinkedTo"], "Galaxy S26 Ultra"),
            string.Format(hi["Splash_LiveHandshake_Pinging_Other"], 3),
            string.Format(hi["Splash_LiveHandshake_ListeningOnPort"], 5005),
            hi["Splash_LiveHandshake_NonePaired"],
            "पिंकी का फ़ोन  " + hi["Splash_LiveHandshake_NodeLinked"],
        };

        using var shaper = new SplashTextShaper();
        foreach (var text in lines)
        {
            using var line = shaper.Shape(text, SKTypeface.Default, 14f);
            line.Glyphs.Should().NotBeEmpty(text);
            line.Glyphs.Should().NotContain((ushort)0, $"'{text}' must not draw a missing-glyph box");
            line.Width.Should().BeGreaterThan(0f);
            line.Blob.Should().NotBeNull();
        }
    }

    [Fact]
    public void DevanagariIsShapedNotMappedCharacterByCharacter()
    {
        using var devanagari = SKFontManager.Default.MatchCharacter('क');
        if (devanagari is null || !devanagari.ContainsGlyph('क')) return;

        // "कि" is consonant + vowel sign I, which renders to the LEFT of the consonant: a shaper
        // reorders or ligates it, a naive char-to-glyph map keeps the logical order and is wrong.
        using var shaper = new SplashTextShaper();
        using var line = shaper.Shape("लिंक्ड", SKTypeface.Default, 14f);
        var naive = devanagari.GetGlyphs("लिंक्ड");

        line.Glyphs.Should().NotEqual(naive, "HarfBuzz must apply the Devanagari shaping rules");
    }

    [Fact]
    public void LatinTextStaysOnThePrimaryFaceAndRendersIntoTheFrame()
    {
        using var shaper = new SplashTextShaper();
        using var line = shaper.Shape("LINKED · GALAXY S26 ULTRA", SKTypeface.Default, 14f);

        line.Glyphs.Should().NotContain((ushort)0);
        line.Width.Should().BeGreaterThan(50f);

        using var surface = SKSurface.Create(new SKImageInfo(400, 40, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        line.Draw(surface.Canvas, 4, 28, paint);
        using var image = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(image);
        Enumerable.Range(0, 400).Any(x => Enumerable.Range(0, 40).Any(y => bmp.GetPixel(x, y).Alpha > 0))
            .Should().BeTrue("the blob must actually paint");
    }

    [Fact]
    public void EmptyTextIsAnEmptyLine()
    {
        using var shaper = new SplashTextShaper();
        using var line = shaper.Shape(string.Empty, SKTypeface.Default, 14f);
        line.Width.Should().Be(0f);
        line.Blob.Should().BeNull();
    }

    private static Dictionary<string, string> HindiStrings()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "Localization", "Strings.hi.resx"));
        return doc.Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
