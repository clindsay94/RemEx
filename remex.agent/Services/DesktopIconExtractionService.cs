using System.Drawing;
using System.Drawing.Imaging;
using Remex.Core.Services;

namespace Remex.Agent.Services;

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

        if (OperatingSystem.IsLinux() && File.Exists(filePath))
        {
            try
            {
                return ExtractLinuxIcon(filePath);
            }
            catch (Exception)
            {
                return FallbackBase64Icon;
            }
        }

        return FallbackBase64Icon;
    }

    private string ExtractLinuxIcon(string filePath)
    {
        string? iconName = null;

        if (filePath.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
        {
            iconName = ParseDesktopFileForIcon(filePath);
        }
        else
        {
            // If it's a binary, try to find its .desktop file
            iconName = FindIconNameFromBinary(filePath);
        }

        if (string.IsNullOrEmpty(iconName))
            return FallbackBase64Icon;

        // iconName could be a full path or just a name
        string? iconPath = iconName;
        if (!Path.IsPathRooted(iconName))
        {
            iconPath = ResolveIconPath(iconName);
        }

        if (iconPath != null && File.Exists(iconPath))
        {
            var ext = Path.GetExtension(iconPath).ToLowerInvariant();
            
            // Avalonia's Bitmap class doesn't support SVG natively.
            // If it's an SVG or other unsupported format, try to find a PNG version for the same icon name
            if (ext == ".svg" || ext == ".xpm")
            {
                string? pngPath = ResolveIconPath(Path.GetFileNameWithoutExtension(iconName), true);
                if (pngPath != null && File.Exists(pngPath))
                {
                    iconPath = pngPath;
                }
                else if (ext == ".svg")
                {
                    // If no PNG alternative found for SVG, skip to prevent UI errors
                    return FallbackBase64Icon;
                }
            }

            if (iconPath != null && File.Exists(iconPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(iconPath);
                    return Convert.ToBase64String(bytes);
                }
                catch
                {
                    return FallbackBase64Icon;
                }
            }
        }

        return FallbackBase64Icon;
    }

    private string? ParseDesktopFileForIcon(string filePath)
    {
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (line.StartsWith("Icon=", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(5).Trim();
                }
            }
        }
        catch { /* an icon that cannot be extracted degrades to no icon; the launcher entry is still usable without one */ }
        return null;
    }

    private string? FindIconNameFromBinary(string binaryPath)
    {
        var binaryName = Path.GetFileName(binaryPath);
        
        // Use XDG standards for data directories
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") 
                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share");
        var xdgDataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS")?.Split(':') ?? Array.Empty<string>())
                          .Concat(new[] { "/usr/local/share", "/usr/share" })
                          .Distinct();

        var searchPaths = new List<string> { Path.Combine(xdgDataHome, "applications") };
        searchPaths.AddRange(xdgDataDirs.Select(dir => Path.Combine(dir, "applications")));

        foreach (var dir in searchPaths)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var desktopFile in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                try
                {
                    bool foundExec = false;
                    string? icon = null;

                    foreach (var line in File.ReadLines(desktopFile))
                    {
                        if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                        {
                            var execLine = line.Substring(5).Trim();
                            // Check if the binary name is part of the command (ignoring arguments)
                            if (execLine.Split(' ').Any(arg => arg.Contains(binaryName, StringComparison.OrdinalIgnoreCase)))
                                foundExec = true;
                        }
                        else if (line.StartsWith("Icon=", StringComparison.OrdinalIgnoreCase))
                        {
                            icon = line.Substring(5).Trim();
                        }
                    }

                    if (foundExec && !string.IsNullOrEmpty(icon))
                        return icon;
                }
                catch { /* Ignore read errors */ }
            }
        }

        return null;
    }

    private string? ResolveIconPath(string iconName, bool pngOnly = false)
    {
        var extensions = pngOnly ? new[] { ".png" } : new[] { ".png", ".svg", ".xpm", ".jpg", ".jpeg" };
        
        // Standard icon sizes in order of preference
        var sizes = new[] { "256x256", "128x128", "64x64", "48x48", "32x32", "scalable" };
        
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") 
                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share");
        var xdgDataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS")?.Split(':') ?? Array.Empty<string>())
                          .Concat(new[] { "/usr/local/share", "/usr/share" })
                          .Distinct();

        var baseDirs = new List<string>
        {
            Path.Combine(xdgDataHome, "icons"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".icons")
        };
        baseDirs.AddRange(xdgDataDirs.Select(dir => Path.Combine(dir, "icons")));
        baseDirs.Add("/usr/share/pixmaps");

        foreach (var baseDir in baseDirs)
        {
            if (!Directory.Exists(baseDir)) continue;

            if (baseDir.EndsWith("pixmaps", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ext in extensions)
                {
                    var path = Path.Combine(baseDir, iconName + ext);
                    if (File.Exists(path)) return path;
                }
                continue;
            }

            // Search in hicolor first (standard), then others
            var themes = new[] { "hicolor", "breeze", "Adwaita", "ubuntu-mono-dark" };
            foreach (var theme in themes)
            {
                var themeDir = Path.Combine(baseDir, theme);
                if (!Directory.Exists(themeDir)) continue;

                foreach (var size in sizes)
                {
                    var appsDir = Path.Combine(themeDir, size, "apps");
                    if (!Directory.Exists(appsDir)) continue;

                    foreach (var ext in extensions)
                    {
                        var path = Path.Combine(appsDir, iconName + ext);
                        if (File.Exists(path)) return path;
                    }
                }
            }
        }

        return null;
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
