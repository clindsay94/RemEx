using System;
using System.IO;
using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the wallpaper seed extraction (RemEx-rdzet) against a synthetic image whose dominant
/// colours are known, so "the quantizer ran" and "the quantizer answered sensibly" stay two
/// different claims.
/// </summary>
/// <remarks>
/// The registry halves (Windows accent, wallpaper path) are deliberately not driven here — they
/// read the developer's live HKCU and their absence contract is "null, never throw", which the
/// off-Windows guard test below pins for the half of it a test can own on any machine.
/// </remarks>
public class SystemSeedSourcesTests
{
    private static string WriteImage(int width, int height, Action<SKCanvas> draw)
    {
        var path = Path.Combine(Path.GetTempPath(), $"remex-seed-{Guid.NewGuid():N}.png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            draw(canvas);
        }

        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void ADominantChromaticColourWinsOverALargerDarkField()
    {
        // The scoring half is the point, not the clustering: raw frequency hands back the
        // near-black that covers most of this image — measured, not imagined: with scoring
        // bypassed, the developer's real wallpaper yielded five near-blacks (#000000, #0B0000...),
        // the exact "the sky or a wall" failure the bead warns about.
        //
        // ASSERTED AS CHROMA SPREAD, NOT AS A CHANNEL ORDER. The first draft drew grey-plus-blue
        // and asserted the winner was blue-family — and stayed green under the frequency
        // injection, because the quantizer BLENDS regions: the biggest cluster was a blue-grey
        // centroid (#707098) that satisfied "B > R" while being exactly the muddy non-answer the
        // scorer exists to reject. A blend of near-black and one hue is still low-spread, so the
        // spread floor survives centroid drift where the channel order did not.
        var path = WriteImage(160, 160, canvas =>
        {
            canvas.Clear(new SKColor(0x10, 0x10, 0x10));
            using var paint = new SKPaint { Color = new SKColor(0xD0, 0x28, 0x30) };
            canvas.DrawRect(new SKRect(0, 120, 160, 160), paint);
        });

        try
        {
            var seeds = SystemSeedSources.ExtractSeedsFromImage(path, 5);

            seeds.Should().NotBeEmpty();
            var top = Color.Parse(seeds[0]);
            var spread = Math.Max(top.R, Math.Max(top.G, top.B)) - Math.Min(top.R, Math.Min(top.G, top.B));
            spread.Should().BeGreaterThanOrEqualTo(48,
                $"the top seed '{seeds[0]}' must carry real chroma — a near-black or a muddy blend "
                + "is the frequency answer, not the scored one");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CandidatesAreValidDistinctHexColours()
    {
        var path = WriteImage(90, 90, canvas =>
        {
            using var red = new SKPaint { Color = new SKColor(0xC0, 0x20, 0x28) };
            using var green = new SKPaint { Color = new SKColor(0x1E, 0x8A, 0x3C) };
            using var blue = new SKPaint { Color = new SKColor(0x20, 0x47, 0xC0) };
            canvas.DrawRect(new SKRect(0, 0, 90, 30), red);
            canvas.DrawRect(new SKRect(0, 30, 90, 60), green);
            canvas.DrawRect(new SKRect(0, 60, 90, 90), blue);
        });

        try
        {
            var seeds = SystemSeedSources.ExtractSeedsFromImage(path, 5);

            seeds.Should().HaveCountGreaterThanOrEqualTo(2, "three strong hues must yield plural candidates");
            seeds.Should().OnlyHaveUniqueItems();
            foreach (var seed in seeds)
            {
                Color.TryParse(seed, out _).Should().BeTrue($"'{seed}' must parse as a colour");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AGreyscaleImageYieldsNothingRatherThanGoogleBlue()
    {
        // Review finding, measured against the real library: when no cluster clears the scorer's
        // chroma floor, MaterialColorUtilities 0.3.0 returns a hardcoded #4285F4 sentinel rather
        // than an empty list. Without the produced-by-the-quantizer filter, a black-and-white
        // wallpaper would overwrite the user's seed with a Google brand blue that appears nowhere
        // in the image — and show it as the single "extracted" swatch.
        var path = WriteImage(120, 120, canvas =>
        {
            canvas.Clear(new SKColor(0x30, 0x30, 0x30));
            using var light = new SKPaint { Color = new SKColor(0xC0, 0xC0, 0xC0) };
            using var mid = new SKPaint { Color = new SKColor(0x78, 0x78, 0x78) };
            canvas.DrawRect(new SKRect(0, 40, 120, 80), light);
            canvas.DrawRect(new SKRect(0, 80, 120, 120), mid);
        });

        try
        {
            SystemSeedSources.ExtractSeedsFromImage(path, 5).Should().BeEmpty(
                "an image with no seed-worthy colour must offer nothing, not the library's sentinel");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableImageYieldsNoCandidatesRatherThanThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remex-seed-{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "not an image");

        try
        {
            SystemSeedSources.ExtractSeedsFromImage(path, 5).Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileYieldsNoCandidatesRatherThanThrowing()
    {
        SystemSeedSources
            .ExtractSeedsFromImage(Path.Combine(Path.GetTempPath(), "remex-no-such-file.png"), 5)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheWindowsAccentIsAParseableColourWhereItExistsAtAll()
    {
        var accent = SystemSeedSources.TryGetWindowsAccent();

        if (!OperatingSystem.IsWindows())
        {
            // The Linux half of the acceptance: the source answers null, and the view hides the
            // buttons — nothing here may throw.
            accent.Should().BeNull();
            return;
        }

        // On Windows the key can legitimately be absent (stripped installs); when present it must
        // be a displayable colour, because it goes straight into AccentColor.
        if (accent is not null)
        {
            Color.TryParse(accent, out _).Should().BeTrue($"'{accent}' feeds the palette directly");
        }
    }
}
