using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MaterialColorUtilities.Quantize;
using MaterialColorUtilities.Score;
using SkiaSharp;

namespace Remex.Desktop.Services;

/// <summary>
/// Seeds the palette can take from the desktop itself (RemEx-rdzet): the Windows accent colour,
/// and the dominant colours of the current wallpaper — the same trick Android plays with
/// Material You, pointed at this machine.
/// </summary>
/// <remarks>
/// <para>
/// EVERY PATH HERE DEGRADES TO "NO ANSWER", NEVER TO A THROW. These run from button clicks on the
/// Palette Studio; a missing registry key, a deleted wallpaper file, or a codec Skia cannot read
/// is a reason to offer nothing, not a reason to take the panel down. The Linux build never calls
/// in at all — the view hides the buttons behind <c>OperatingSystem.IsWindows()</c>, and the
/// registry reads are additionally guarded here so a future caller cannot turn a missing guard
/// into a crash.
/// </para>
/// <para>
/// The wallpaper path yields CANDIDATES, plural, on purpose: dominant-colour extraction is a
/// guess, so the studio shows the top few as swatches and the user picks. Extraction is
/// CPU-bound (decode + quantize + score) — callers run it off the UI thread, the same rule the
/// palette solve already follows.
/// </para>
/// </remarks>
public static class SystemSeedSources
{
    /// <summary>How many wallpaper candidates to offer. Enough to recover from a wrong guess, few enough to stay a choice.</summary>
    public const int WallpaperCandidateCount = 5;

    /// <summary>
    /// The pixel budget the wallpaper is downscaled into before quantizing. 96×96 keeps the
    /// quantizer fast on a 4K wallpaper while leaving far more samples than the 128 clusters ask for.
    /// </summary>
    private const int MaxSampleEdge = 96;

    /// <summary>
    /// The user's Windows accent colour as "#RRGGBB", or null off-Windows / when the key is absent.
    /// </summary>
    /// <remarks>
    /// <c>HKCU\Software\Microsoft\Windows\DWM\AccentColor</c> holds the accent the user picked in
    /// Settings as an ABGR dword; <c>ColorizationColor</c> (ARGB) is the older key some builds
    /// carry instead, so it is the fallback rather than the primary.
    /// </remarks>
    public static string? TryGetWindowsAccent()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var value = unchecked((uint)abgr);
                return $"#{(byte)value:X2}{(byte)(value >> 8):X2}{(byte)(value >> 16):X2}";
            }

            if (key?.GetValue("ColorizationColor") is int argb)
            {
                var value = unchecked((uint)argb);
                return $"#{(byte)(value >> 16):X2}{(byte)(value >> 8):X2}{(byte)value:X2}";
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"SystemSeedSources: could not read the Windows accent — {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// The current wallpaper's top seed candidates as "#RRGGBB", best first. Empty off-Windows,
    /// when no wallpaper is set, or when the file cannot be decoded.
    /// </summary>
    public static IReadOnlyList<string> ExtractWallpaperSeeds()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

        var path = TryGetWallpaperPath();
        if (path is null) return Array.Empty<string>();

        return ExtractSeedsFromImage(path, WallpaperCandidateCount);
    }

    /// <summary>
    /// The dominant-colour candidates of <paramref name="imagePath"/>, best first. Public and
    /// platform-neutral so the extraction itself is testable with a synthetic image on any OS.
    /// </summary>
    public static IReadOnlyList<string> ExtractSeedsFromImage(string imagePath, int count)
    {
        try
        {
            using var decoded = SKBitmap.Decode(imagePath);
            if (decoded is null) return Array.Empty<string>();

            // Downscale before quantizing: the quantizer's cost scales with pixel count and the
            // dominant colours of a wallpaper survive a 96px thumbnail entirely.
            var scale = Math.Min(1.0, MaxSampleEdge / (double)Math.Max(decoded.Width, decoded.Height));
            var width = Math.Max(1, (int)(decoded.Width * scale));
            var height = Math.Max(1, (int)(decoded.Height * scale));
            using var sampled = decoded.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
                ?? decoded.Copy();

            var pixels = sampled.Pixels
                .Where(p => p.Alpha == byte.MaxValue)
                .Select(p => (uint)((0xFFu << 24) | ((uint)p.Red << 16) | ((uint)p.Green << 8) | p.Blue))
                .ToArray();
            if (pixels.Length == 0) return Array.Empty<string>();

            // The same pipeline Android's wallpaper theming runs: cluster, then score clusters for
            // suitability as a SEED (chroma and population, not just frequency) — raw frequency
            // alone hands back the sky or a wall.
            var clusters = QuantizerCelebi.Quantize(pixels, 128);
            var ranked = Scorer.Score(clusters);

            return ranked
                // ONLY COLOURS THE QUANTIZER ACTUALLY PRODUCED (review, measured): when no cluster
                // clears its chroma floor, Scorer.Score does not return empty — it returns a
                // hardcoded Google-blue sentinel (#4285F4) that appears nowhere in the image. A
                // greyscale wallpaper must yield NOTHING, not silently overwrite the user's seed
                // with a brand colour; membership in the cluster set is what separates an
                // extracted answer from the library's apology for not having one.
                .Where(clusters.ContainsKey)
                .Take(Math.Max(1, count))
                .Select(argb => $"#{(byte)(argb >> 16):X2}{(byte)(argb >> 8):X2}{(byte)argb:X2}")
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"SystemSeedSources: could not extract wallpaper seeds — {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The wallpaper file Windows currently shows, or null when none is set or the file is gone.
    /// </summary>
    /// <remarks>
    /// The registry names the ORIGINAL file; the transcoded copy under the Themes folder is the
    /// fallback because a slideshow or a since-deleted original still leaves one behind.
    /// </remarks>
    internal static string? TryGetWallpaperPath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string wallpaper
                && !string.IsNullOrWhiteSpace(wallpaper)
                && File.Exists(wallpaper))
            {
                return wallpaper;
            }

            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            return File.Exists(transcoded) ? transcoded : null;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"SystemSeedSources: could not resolve the wallpaper path — {ex.Message}");
            return null;
        }
    }
}
