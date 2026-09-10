using System;
using System.IO;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>The Wallpaper background mode's pure decisions (RemEx-ddynd): blur mapping and which file to draw.</summary>
public static class WallpaperBackdrop
{
    /// <summary>Blur 1.0 in device pixels. 48 is where a 4K wallpaper becomes colour fields rather than a picture.</summary>
    public const double MaxBlurRadius = 48.0;

    /// <summary><c>WallpaperBlur</c> (0–1) to a blur radius (0–48 px). NaN and out-of-range clamp.</summary>
    public static double BlurRadiusFor(double blur)
    {
        if (double.IsNaN(blur)) return 0.0;
        return Math.Clamp(blur, 0.0, 1.0) * MaxBlurRadius;
    }

    /// <summary>
    /// The file to draw: the desktop's own wallpaper, or the app-owned copy — and null when there is
    /// nothing to draw, which the caller renders as Solid for the session without touching the
    /// setting. An Image source whose copy is gone does NOT fall through to the desktop wallpaper:
    /// showing a different picture than the one the person picked is a silent substitution.
    /// </summary>
    public static string? ResolvePath(CustomizationSettings settings, Func<string?> desktopWallpaperPath)
    {
        if (string.Equals(settings.WallpaperSource, WallpaperSources.Image, StringComparison.Ordinal))
        {
            var copy = settings.WallpaperImagePath;
            return !string.IsNullOrWhiteSpace(copy) && File.Exists(copy) ? copy : null;
        }

        try
        {
            return desktopWallpaperPath();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
