using System.IO;
using System.Runtime.CompilerServices;

using SkiaSharp;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the <c>SKFilterQuality.Medium</c> to <see cref="SKSamplingOptions"/> equivalence that the
/// SkiaSharp 3 migration relied on (RemEx-jcma3).
/// </summary>
/// <remarks>
/// <para>
/// SkiaSharp 3 deleted <c>SKFilterQuality</c>. Three downscales had to be rewritten by hand —
/// thumbnail generation, the Linux JPEG encoder, and Linux screen capture — and two of those are on
/// the capture path that <c>docs/REGRESSION-GUARDS.md</c> covers. Getting the replacement wrong does
/// not fail: it silently changes how every scaled frame is filtered.
/// </para>
/// <para>
/// THIS TEST EXISTS BECAUSE THE QUESTION WAS ACTUALLY CONTESTED. Review argued the mapping should be
/// <c>Linear + Nearest</c>, citing upstream Skia's C++ legacy conversion, where <c>kMedium</c> does
/// map to <c>SkMipmapMode::kNearest</c>. That is a correct fact about Skia and the wrong answer for
/// SkiaSharp: this binding's own <c>SkiaExtensions.ToSamplingOptions</c> returns
/// <c>Linear + Linear</c> for Medium. Measured, not argued — and the measurement is what this test
/// preserves, so the next person to read the upstream C++ and reach for "Nearest" gets a red test
/// with the reason attached instead of a quietly softer capture stream.
/// </para>
/// <para>
/// It asserts against the library rather than against a hard-coded pair, so if a future SkiaSharp
/// genuinely changes the equivalence this fails and asks for a decision, rather than silently
/// disagreeing with the production call sites.
/// </para>
/// </remarks>
public class SamplingOptionsMigrationTests
{
    /// <summary>What the three rewritten downscales pass to <c>ScalePixels</c>.</summary>
    private static readonly SKSamplingOptions WhatRemexUses = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    [Fact]
    public void TheHandWrittenOptionsMatchWhatSkiaSharpCallsMedium()
    {
#pragma warning disable CS0618 // SKFilterQuality is obsolete; reading its official replacement is the point.
        var mediumsOwnReplacement = SkiaExtensions.ToSamplingOptions(SKFilterQuality.Medium);
#pragma warning restore CS0618

        Assert.True(
            WhatRemexUses == mediumsOwnReplacement,
            "The three ScalePixels sites were migrated off SKFilterQuality.Medium and must land on "
            + "exactly what SkiaSharp itself considers Medium's replacement. Expected "
            + $"Filter={mediumsOwnReplacement.Filter} Mipmap={mediumsOwnReplacement.Mipmap}, "
            + $"got Filter={WhatRemexUses.Filter} Mipmap={WhatRemexUses.Mipmap}.");
    }

    [Fact]
    public void MediumIsNotTheSameAsLowOrHigh()
    {
        // ANTI-VACUITY. Without this, a SkiaSharp release that collapsed every quality onto one
        // value would leave the assertion above green while every downscale in the app silently
        // changed. It also states the distinction that made Medium the right choice: Low drops
        // mipmapping entirely, which is what aliases a reduction.
#pragma warning disable CS0618
        var low = SkiaExtensions.ToSamplingOptions(SKFilterQuality.Low);
        var high = SkiaExtensions.ToSamplingOptions(SKFilterQuality.High);
#pragma warning restore CS0618

        Assert.False(WhatRemexUses == low, "Low is Linear with NO mipmapping, which aliases a downscale.");
        Assert.False(WhatRemexUses == high, "High is a Mitchell cubic resampler, not a filter/mipmap pair.");
        Assert.Equal(SKMipmapMode.Linear, WhatRemexUses.Mipmap);
    }

    [Fact]
    public void AllThreeDownscaleSitesUseIt()
    {
        // The equivalence above is only worth having if the production sites actually carry it.
        // Checked in source because the three calls are inline expressions with no seam to read.
        var repo = RepoRoot();
        var sites = new[]
        {
            Path.Combine(repo, "remex.agent", "Services", "FileTransfer", "ThumbnailService.cs"),
            Path.Combine(repo, "remex.agent", "Services", "RemoteDesktop", "Linux", "Capture", "LinuxJpegEncoder.cs"),
            Path.Combine(repo, "remex.agent", "Services", "ScreenCapture", "LinuxScreenCaptureService.cs"),
        };

        foreach (var site in sites)
        {
            // COMMENTS STRIPPED, and this assertion caught its own author. All three call sites
            // carry a comment explaining what SKFilterQuality used to be and why it is gone, so the
            // "must not reach back for the removed enum" check failed on the prose that documents
            // the migration rather than on any live code. Same trap as the HarfBuzz assertion in
            // MaterialPackagePinTests, arrived at from the opposite direction: there a comment made
            // a missing call look present, here it made an absent type look used.
            var source = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(site),
                @"//.*$",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline);
            var name = Path.GetFileName(site);

            Assert.True(
                source.Contains("SKMipmapMode.Linear", System.StringComparison.Ordinal),
                $"{name} downscales and must keep Medium's mipmapping (SKMipmapMode.Linear). "
                + "Nearest is upstream Skia's C++ mapping, not SkiaSharp's — see this class's remarks.");
            Assert.False(
                source.Contains("SKFilterQuality", System.StringComparison.Ordinal),
                $"{name} must not reach back for the removed quality enum.");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
