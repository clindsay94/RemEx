using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Remex.Core.Services;

/// <summary>
/// Service to extract icons from files/executables.
/// </summary>
public interface IIconExtractionService
{
    string ExtractIconAsBase64(string filePath);
}

public class IconExtractionService : IIconExtractionService
{
    // A simple 16x16 or 32x32 blank SVG/PNG or geometric shape embedded as base64 string
    // Here we use a generic 32x32 transparent PNG with a subtle grey rounded square as fallback
    // (A real SVG to Base64 could also be used, but this is a tiny valid PNG image)
    private const string FallbackBase64Icon = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAACXBIWXMAAAsTAAALEwEAmpwYAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAACNSURBVHgB7dexCQBACAAw3L/nCgY2YWcRweD0kUDeP/P1vT8AAAAASUVORK5CYII=";

    public string ExtractIconAsBase64(string filePath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(filePath))
        {
            try
            {
                return ExtractWindowsIcon(filePath);
            }
            catch (Exception)
            {
                // TODO: Log the exception (e.g., using ILogger) to aid in debugging.
                // Fallback to default if anything goes wrong during extraction
                return FallbackBase64Icon;
            }
        }

        return FallbackBase64Icon;
    }

    private string ExtractWindowsIcon(string filePath)
    {
#pragma warning disable CA1416 // Validate platform compatibility
        using var icon = Icon.ExtractAssociatedIcon(filePath);
        if (icon != null)
        {
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            byte[] imageBytes = ms.ToArray();
            return Convert.ToBase64String(imageBytes);
        }
#pragma warning restore CA1416

        return FallbackBase64Icon;
    }
}
