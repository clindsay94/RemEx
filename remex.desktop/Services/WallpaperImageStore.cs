using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Remex.Desktop.Services;

/// <summary>
/// Copies a picked wallpaper image under the per-user data directory, downscaled so the longest
/// edge is at most <see cref="MaxEdge"/> pixels (spec section 6). The profile stores the COPY's
/// path, never the original's: the original can move, and the copy is sized for the window.
/// </summary>
/// <remarks>EVERY PATH DEGRADES TO FALSE, NEVER TO A THROW — the same rule SystemSeedSources
/// follows; the caller raises the snackbar and keeps the previous image.</remarks>
public static class WallpaperImageStore
{
    public const int MaxEdge = 2560;

    /// <summary>The folder under the same root the profile file uses.</summary>
    public static string DirectoryFor(string perUserRoot) => Path.Combine(perUserRoot, "wallpapers");

    public static bool TryCopyDownscaled(string sourcePath, string directory, out string? copyPath)
    {
        copyPath = null;
        try
        {
            if (!File.Exists(sourcePath)) return false;

            using var decoded = SKBitmap.Decode(sourcePath);
            if (decoded is null) return false;

            var scale = Math.Min(1.0, MaxEdge / (double)Math.Max(decoded.Width, decoded.Height));
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));

            // NO SECOND FULL-SIZE COPY (perf audit P3-55). At scale 1.0 - or when Resize fails - the
            // decoded bitmap is encoded directly; it was being Copy()'d first, doubling peak memory
            // for a pick that is already at or under MaxEdge. Only a real resize owns a new bitmap.
            using var resized = scale < 1.0
                ? decoded.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
                : null;
            var sized = resized ?? decoded;
            // Immutable lets FromBitmap share the pixels instead of copying them once more.
            sized.SetImmutable();
            using var image = SKImage.FromBitmap(sized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) return false;

            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, $"wallpaper-{Guid.NewGuid():N}.png");
            var temp = target + ".tmp";
            using (var stream = File.Create(temp)) data.SaveTo(stream);
            File.Move(temp, target, overwrite: true);

            copyPath = target;
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WallpaperImageStore: could not copy '{sourcePath}' — {ex.Message}");
            return false;
        }
    }

    /// <summary>Best-effort removal of a superseded copy. Only files this store wrote are touched.</summary>
    public static void TryDeleteCopy(string? copyPath, string directory)
    {
        if (string.IsNullOrWhiteSpace(copyPath)) return;
        try
        {
            // Full paths, trailing separators trimmed, so a same-folder path that merely differs
            // in casing or a trailing slash still resolves as "inside" (RemEx-8twk0.5). GetFullPath
            // also collapses any ".." traversal before the comparison, so a path that only reaches
            // this folder via a parent reference is judged on where it actually lands.
            var copyDir = Path.GetDirectoryName(Path.GetFullPath(copyPath))?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (copyDir is not null && string.Equals(copyDir, targetDir, comparison) && File.Exists(copyPath))
                File.Delete(copyPath);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WallpaperImageStore: could not delete '{copyPath}' — {ex.Message}");
        }
    }
}
