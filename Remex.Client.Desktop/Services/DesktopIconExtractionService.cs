using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Remex.Core.Services;

namespace Remex.Client.Desktop.Services;

public class DesktopIconExtractionService : IIconExtractionService
{
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
