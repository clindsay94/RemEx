using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// A captured headless frame's raw BGRA8888 bytes, copied out of the framebuffer once so the
/// assertions below can read arbitrary pixels without holding <see cref="ILockedFramebuffer"/>
/// open (RemEx-0e9eq). This is the piece that turns "a bitmap was produced" into "the bitmap is
/// not one flat colour" - the assertion that would have caught RemEx-b8dxy.
/// </summary>
internal sealed class FramePixels
{
    private readonly byte[] _bytes;
    private readonly int _rowBytes;

    private FramePixels(byte[] bytes, int rowBytes, PixelSize size)
    {
        _bytes = bytes;
        _rowBytes = rowBytes;
        Size = size;
    }

    public PixelSize Size { get; }

    /// <summary>
    /// Copies the current framebuffer out of <paramref name="bitmap"/>. Throws if the harness's
    /// requested <c>FrameBufferFormat</c> (Bgra8888, set in <see cref="RenderTestApp"/>) is not
    /// what actually came back, since every offset below assumes 4 bytes per pixel in B,G,R,A order.
    /// </summary>
    public static FramePixels From(WriteableBitmap bitmap)
    {
        using var locked = bitmap.Lock();

        if (locked.Format != PixelFormat.Bgra8888)
        {
            throw new InvalidOperationException(
                $"Expected the headless framebuffer in Bgra8888 (set on AvaloniaHeadlessPlatformOptions), "
                + $"but the captured frame is {locked.Format}. Every pixel offset below assumes 4 BGRA bytes.");
        }

        var length = locked.RowBytes * locked.Size.Height;
        var bytes = new byte[length];
        Marshal.Copy(locked.Address, bytes, 0, length);
        return new FramePixels(bytes, locked.RowBytes, locked.Size);
    }

    public Color ColorAt(int x, int y)
    {
        var offset = (y * _rowBytes) + (x * 4);
        return Color.FromArgb(_bytes[offset + 3], _bytes[offset + 2], _bytes[offset + 1], _bytes[offset]);
    }

    /// <summary>
    /// Count of distinct colours inside <paramref name="rect"/>, after shifting each channel right
    /// by 4 bits. Anti-aliasing means two pixels that read as "the same colour" to a person almost
    /// never have byte-identical channels, so a raw distinct-colour count is dominated by AA noise
    /// at every edge; quantising collapses that noise while still separating genuinely different
    /// colours (a 16-level-per-channel palette).
    /// </summary>
    public int DistinctQuantisedColours(PixelRect rect)
    {
        var seen = new HashSet<int>();
        ForEachPixel(rect, c => seen.Add(QuantisedKey(c)));
        return seen.Count;
    }

    /// <summary>
    /// Fraction of pixels in <paramref name="rect"/> that share the single most common quantised
    /// colour - 1.0 means the region is effectively one flat colour, which is exactly the shape of
    /// RemEx-b8dxy (an opaque SideSheet painting the whole shell #303030).
    /// </summary>
    public double DominantCoverage(PixelRect rect)
    {
        var counts = new Dictionary<int, int>();
        var total = 0;
        ForEachPixel(rect, c =>
        {
            var key = QuantisedKey(c);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            total++;
        });

        return total == 0 ? 0 : counts.Values.Max() / (double)total;
    }

    private void ForEachPixel(PixelRect rect, Action<Color> visit)
    {
        var maxX = Math.Min(rect.X + rect.Width, Size.Width);
        var maxY = Math.Min(rect.Y + rect.Height, Size.Height);
        for (var y = Math.Max(rect.Y, 0); y < maxY; y++)
        {
            for (var x = Math.Max(rect.X, 0); x < maxX; x++)
            {
                visit(ColorAt(x, y));
            }
        }
    }

    private static int QuantisedKey(Color c)
        => ((c.A >> 4) << 12) | ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);

    /// <summary>
    /// The framebuffer-pixel rectangle a visual occupies inside <paramref name="window"/>, scaled
    /// by <see cref="TopLevel.RenderScaling"/> - the same conversion the renderer itself applies, so
    /// a control's DIP <see cref="Visual.Bounds"/> lines up with the raw pixels captured above.
    /// </summary>
    public static PixelRect RectOf(Visual visual, Window window)
    {
        var origin = visual.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException(
                $"{visual} did not resolve a translated point into {window} - it is not attached to "
                + "that window's visual tree (or is not effectively visible).");

        var scaling = window.RenderScaling;
        var x = (int)Math.Round(origin.X * scaling);
        var y = (int)Math.Round(origin.Y * scaling);
        var width = (int)Math.Round(visual.Bounds.Width * scaling);
        var height = (int)Math.Round(visual.Bounds.Height * scaling);
        return new PixelRect(x, y, width, height);
    }
}
