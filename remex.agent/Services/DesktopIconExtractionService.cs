using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Largest edge we will store. Windows ships 256px icon variants for most modern executables and
    /// the launcher tile draws at 80px logical, so 256 stays crisp to 300% display scaling. Capped
    /// rather than unbounded because the icon travels as base64 inside the launcher-sync message.
    /// </summary>
    private const int MaxIconEdge = 256;

    /// <summary>
    /// Below this the icon is treated as low-resolution — the size the old
    /// <see cref="Icon.ExtractAssociatedIcon"/> path always produced. Callers use the same threshold
    /// to decide whether a stored icon is worth re-extracting.
    /// </summary>
    public const int LowResolutionIconEdge = 64;

    /// <summary>
    /// Base64 budget for a single icon. The whole launcher list travels in one WebSocket message
    /// (<c>MessageSerializer</c> caps that at 4 MB), so one oversized icon must not eat the budget
    /// for the rest of the list. An icon over this is re-encoded at half the edge length.
    /// </summary>
    private const int MaxIconBase64Length = 48 * 1024;

    /// <summary>
    /// Extracts the highest-resolution icon Windows has for <paramref name="filePath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT <see cref="Icon.ExtractAssociatedIcon"/>, which is what this used to be.
    /// That API always hands back 32x32 regardless of what the file actually contains, and the
    /// launcher then upscales it into an 80px tile — 2.5x at 100% scaling, 5x on a 4K display at
    /// 200%. Every tile came out soft and there was no log line or error to point at, because
    /// nothing failed; the icon was simply too small (RemEx-u4244).
    /// </para>
    /// <para>
    /// The shell system image list is the way to reach the 256px variant. SHIL_JUMBO is tried
    /// first, then SHIL_EXTRALARGE (48) and SHIL_LARGE (32), then the old API as a last resort, so
    /// a file whose icon cannot be resolved still lands on something rather than nothing.
    /// </para>
    /// </remarks>
    private string ExtractWindowsIcon(string filePath)
    {
#pragma warning disable CA1416 // Validate platform compatibility
        foreach (var imageListSize in new[] { ShilJumbo, ShilExtraLarge, ShilLarge })
        {
            using var shellBitmap = TryGetShellIconBitmap(filePath, imageListSize);
            if (shellBitmap is null)
                continue;

            var encoded = EncodeIcon(shellBitmap);
            if (encoded != null)
                return encoded;
        }

        using var icon = Icon.ExtractAssociatedIcon(filePath);
        if (icon != null)
        {
            using var bitmap = icon.ToBitmap();
            var encoded = EncodeIcon(bitmap);
            if (encoded != null)
                return encoded;
        }
#pragma warning restore CA1416

        return FallbackBase64Icon;
    }

    /// <summary>PNG-encodes to base64, re-encoding smaller if the result blows the per-icon budget.</summary>
    private static string? EncodeIcon(Bitmap bitmap)
    {
        try
        {
            if (bitmap.Width > MaxIconEdge || bitmap.Height > MaxIconEdge)
            {
                using var clamped = ResizeTo(bitmap, MaxIconEdge);
                return ToBase64Png(clamped);
            }

            var encoded = ToBase64Png(bitmap);
            if (encoded.Length <= MaxIconBase64Length)
                return encoded;

            var reducedEdge = Math.Max(LowResolutionIconEdge, Math.Max(bitmap.Width, bitmap.Height) / 2);
            using var reduced = ResizeTo(bitmap, reducedEdge);
            return ToBase64Png(reduced);
        }
        catch
        {
            // A bitmap that cannot be encoded degrades to the next source, then to the fallback icon.
            return null;
        }
    }

    private static string ToBase64Png(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static Bitmap ResizeTo(Bitmap source, int edge)
    {
        var resized = new Bitmap(edge, edge, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(source, new Rectangle(0, 0, edge, edge));
        return resized;
    }

    /// <summary>
    /// Pulls one icon out of a shell system image list, or null if that size is unavailable.
    /// </summary>
    private static Bitmap? TryGetShellIconBitmap(string filePath, int imageListSize)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfoW(filePath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
        if (result == IntPtr.Zero)
            return null;

        // IImageList and HIMAGELIST are documented as interchangeable, so the out-param can be
        // taken as an opaque handle and driven through comctl32's flat exports. That avoids
        // introducing the first COM interop in this codebase for a single GetIcon call.
        var iid = IID_IImageList;
        if (SHGetImageList(imageListSize, ref iid, out var imageList) != 0 || imageList == IntPtr.Zero)
            return null;

        var hIcon = IntPtr.Zero;
        try
        {
            hIcon = ImageList_GetIcon(imageList, info.iIcon, ILD_TRANSPARENT);
            if (hIcon == IntPtr.Zero)
                return null;

            // NOT a `using`: CropParkedCanvas either returns this same instance untouched or
            // disposes it and returns the crop, so ownership passes to the caller either way.
            // Disposing it here hands back a dead Bitmap whose first Save throws, which the catch
            // in EncodeIcon then swallows — the icon silently degrades to the 32px fallback with
            // nothing in the log to say why.
            var raw = BitmapFromHIcon(hIcon);
            return raw is null ? null : CropParkedCanvas(raw);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
                DestroyIcon(hIcon);
            Marshal.Release(imageList);
        }
    }

    /// <summary>
    /// Converts an HICON to a 32bpp ARGB bitmap with its alpha channel intact.
    /// </summary>
    /// <remarks>
    /// The colour bitmap's bits are copied directly rather than going through GDI+ drawing. Any
    /// route that composites the icon through a GDI device context discards the destination alpha,
    /// which turns a transparent icon background into black once it reaches the launcher tile.
    /// Icons that carry no 32bpp colour data (mask-only, 8bpp and below) fall through to
    /// <see cref="Icon.ToBitmap"/>, which handles the mask correctly for them.
    /// </remarks>
    private static Bitmap? BitmapFromHIcon(IntPtr hIcon)
    {
#pragma warning disable CA1416 // Validate platform compatibility
        if (!GetIconInfo(hIcon, out var iconInfo))
            return null;

        try
        {
            if (iconInfo.hbmColor == IntPtr.Zero)
                return CloneFromHandle(hIcon);

            var bitmapHeader = new BITMAP();
            if (GetObjectW(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), ref bitmapHeader) == 0)
                return CloneFromHandle(hIcon);

            if (bitmapHeader.bmBitsPixel != 32 || bitmapHeader.bmWidth <= 0 || bitmapHeader.bmHeight <= 0)
                return CloneFromHandle(hIcon);

            var strideBytes = bitmapHeader.bmWidthBytes;
            var totalBytes = strideBytes * bitmapHeader.bmHeight;
            var pixels = new byte[totalBytes];
            if (GetBitmapBits(iconInfo.hbmColor, totalBytes, pixels) == 0)
                return CloneFromHandle(hIcon);

            // Some 32bpp icons carry an all-zero alpha channel and rely on the AND mask instead.
            // Copying those verbatim yields a fully transparent — i.e. invisible — tile.
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha)
                return CloneFromHandle(hIcon);

            var bitmap = new Bitmap(bitmapHeader.bmWidth, bitmapHeader.bmHeight, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < bitmapHeader.bmHeight; y++)
                {
                    Marshal.Copy(pixels, y * strideBytes, data.Scan0 + (y * data.Stride), strideBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
        }
#pragma warning restore CA1416
    }

    private static Bitmap? CloneFromHandle(IntPtr hIcon)
    {
#pragma warning disable CA1416 // Validate platform compatibility
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1416
    }

    /// <summary>
    /// Crops the transparent filler off an icon the shell parked in the corner of a larger canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When an executable has no 256px variant, SHIL_JUMBO still returns a 256x256 bitmap — with the
    /// 48px artwork sitting in a corner and the remaining ~96% transparent. Stored as-is the launcher
    /// scales the whole canvas into its 80px tile, so the artwork lands at about 15px in the corner
    /// of an otherwise empty card. That looks worse than the blur it replaced.
    /// </para>
    /// <para>
    /// THE TEST IS HOW MUCH OF THE CANVAS THE ARTWORK USES, not where it sits. An earlier version
    /// keyed on the content starting at exactly (0,0) and did nothing for 7-Zip, whose icon carries
    /// a 3px margin of its own — the artwork was parked in the corner as expected, three pixels off
    /// the anchor the check demanded. Ordinary icons fill most of their canvas, so a content box
    /// under three fifths of the bitmap is oversized filler no matter where it is anchored.
    /// </para>
    /// <para>
    /// The crop is a square centred on the artwork with about 8% padding, which is roughly what a
    /// normal icon carries — so a cropped tile sits at the same visual weight as an uncropped one
    /// beside it. It never goes below <see cref="LowResolutionIconEdge"/>, because a stored icon
    /// under that threshold is one the launcher re-extracts on every single load.
    /// </para>
    /// </remarks>
    internal static Bitmap CropParkedCanvas(Bitmap source)
    {
#pragma warning disable CA1416 // Validate platform compatibility
        if (source.Width < LowResolutionIconEdge || source.Height < LowResolutionIconEdge)
            return source;

        if (!TryGetOpaqueBounds(source, out var bounds) || bounds.IsEmpty)
            return source;

        var contentEdge = Math.Max(bounds.Width, bounds.Height);
        if (contentEdge > source.Width * 3 / 5)
            return source;

        var padded = contentEdge + (2 * (contentEdge / 12));
        var edge = Math.Clamp(padded, LowResolutionIconEdge, Math.Min(source.Width, source.Height));
        if (edge >= source.Width && edge >= source.Height)
            return source;

        var x = Math.Clamp(bounds.Left + (bounds.Width / 2) - (edge / 2), 0, source.Width - edge);
        var y = Math.Clamp(bounds.Top + (bounds.Height / 2) - (edge / 2), 0, source.Height - edge);

        try
        {
            var cropped = new Bitmap(edge, edge, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, new Rectangle(0, 0, edge, edge), new Rectangle(x, y, edge, edge), GraphicsUnit.Pixel);
            }

            source.Dispose();
            return cropped;
        }
        catch
        {
            return source;
        }
#pragma warning restore CA1416
    }

    private static bool TryGetOpaqueBounds(Bitmap source, out Rectangle bounds)
    {
#pragma warning disable CA1416 // Validate platform compatibility
        bounds = Rectangle.Empty;

        BitmapData? data = null;
        try
        {
            data = source.LockBits(
                new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            var row = new byte[data.Stride];
            int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;

            for (var y = 0; y < source.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, data.Stride);

                for (var x = 0; x < source.Width; x++)
                {
                    if (row[(x * 4) + 3] == 0)
                        continue;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0 || maxY < 0)
                return false;

            bounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (data != null)
                source.UnlockBits(data);
        }
#pragma warning restore CA1416
    }

    // ── Win32 interop ────────────────────────────────────────────────────────────────────────────

    private const int ShilLarge = 0;        // SHIL_LARGE      — 32x32
    private const int ShilExtraLarge = 2;   // SHIL_EXTRALARGE — 48x48
    private const int ShilJumbo = 4;        // SHIL_JUMBO      — 256x256
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int ILD_TRANSPARENT = 1;

    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetBitmapBits(IntPtr hbmp, int cbBuffer, [Out] byte[] lpvBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
