using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// Every bundled font file is a real font (RemEx-9ufs2).
/// </summary>
/// <remarks>
/// victor_mono_bold.ttf shipped for months as a saved GitHub web page renamed to .ttf, in both the PC
/// and the phone app. Nothing failed: the PC splash's typeface load returned null and fell back to the
/// default font, and the phone fell back the same way, so both splashes silently drew in the wrong
/// font. A build accepts any bytes under a .ttf name, so this checks the sfnt signature of each file.
/// </remarks>
public class BundledFontFilesTests
{
    // TrueType (0x00010000), CFF OpenType ("OTTO") and Apple TrueType ("true").
    private static readonly byte[][] SfntSignatures =
    [
        [0x00, 0x01, 0x00, 0x00],
        "OTTO"u8.ToArray(),
        "true"u8.ToArray(),
    ];

    private static readonly string[] FontDirectories =
    [
        Path.Combine("remex.desktop", "Assets", "Fonts"),
        Path.Combine("remex.android", "app", "src", "main", "res", "font"),
    ];

    [Fact]
    public void EveryBundledFontFileStartsWithAFontSignature()
    {
        var fonts = BundledFonts().ToList();

        // Anti-vacuity: both directories were found and scanned, including the file that was broken.
        fonts.Should().Contain(f => f.EndsWith(Path.Combine("Assets", "Fonts", "victor_mono_bold.ttf")));
        fonts.Should().Contain(f => f.EndsWith(Path.Combine("res", "font", "victor_mono_bold.ttf")));

        var notFonts = fonts
            .Where(f => !SfntSignatures.Any(sig => Header(f).AsSpan().StartsWith(sig)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();

        notFonts.Should().BeEmpty("a file named .ttf/.otf that is not a font loads as null and the app silently draws in a fallback font");
    }

    private static IEnumerable<string> BundledFonts() =>
        FontDirectories
            .Select(d => Path.Combine(RepoRoot(), d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d))
            .Where(f => f.EndsWith(".ttf", System.StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".otf", System.StringComparison.OrdinalIgnoreCase));

    private static byte[] Header(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[4];
        return stream.Read(header, 0, header.Length) == header.Length ? header : [];
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
