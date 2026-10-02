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

    /// <summary>
    /// The phone's display fonts are byte-for-byte the PC's, and every shipped family carries its
    /// OFL licence (RemEx-kq10x.4). A licence can't live in res/font, so the phone keeps them in
    /// assets/licenses.
    /// </summary>
    [Theory]
    [InlineData("bungee_shade_regular.ttf", "BungeeShade-Regular.ttf")]
    [InlineData("orbitron_medium.ttf", "Orbitron-Medium.ttf")]
    [InlineData("victor_mono_bold.ttf", "victor_mono_bold.ttf")]
    public void PhoneDisplayFontsAreThePcFiles(string phoneFile, string pcFile)
    {
        var phone = Path.Combine(RepoRoot(), "remex.android", "app", "src", "main", "res", "font", phoneFile);
        var pc = Path.Combine(RepoRoot(), "remex.desktop", "Assets", "Fonts", pcFile);

        File.Exists(phone).Should().BeTrue($"the phone draws titles in {phoneFile}");
        File.ReadAllBytes(phone).Should().Equal(File.ReadAllBytes(pc), "both apps share one display type");
    }

    [Theory]
    [InlineData("remex.desktop/Assets/Fonts/OFL-Orbitron.txt")]
    [InlineData("remex.desktop/Assets/Fonts/OFL-VictorMono.txt")]
    [InlineData("remex.desktop/Assets/Fonts/OFL-BungeeShade.txt")]
    [InlineData("remex.android/app/src/main/assets/licenses/OFL-Orbitron.txt")]
    [InlineData("remex.android/app/src/main/assets/licenses/OFL-VictorMono.txt")]
    [InlineData("remex.android/app/src/main/assets/licenses/OFL-BungeeShade.txt")]
    public void EveryShippedDisplayFontHasItsOflLicence(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(path).Should().BeTrue("the SIL Open Font License requires the licence to travel with the font");
        File.ReadAllText(path).Should().Contain("SIL Open Font License");
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
