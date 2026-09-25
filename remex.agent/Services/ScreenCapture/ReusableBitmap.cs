using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Remex.Agent.Services.ScreenCapture;

/// <summary>
/// One GDI scratch bitmap, kept across capture ticks and replaced only when the requested size
/// changes (perf audit P4-11). The GDI tier used to allocate a full-screen bitmap, plus a second one
/// when scaling, on every frame.
/// </summary>
/// <remarks>
/// This is scratch space only: every GDI-tier frame is fully overwritten into it (CopyFromScreen /
/// DrawImage with SourceCopy) and then copied OUT into a fresh array or stream before the capture
/// call returns. The buffer the encoder and the socket see is never this bitmap and never pooled,
/// which is the line RemEx-lcp8 drew (RG, BgraFrameConverter). Not thread-safe: the owner holds a
/// lock for the whole capture-and-copy.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ReusableBitmap : IDisposable
{
    private Bitmap? _bitmap;

    /// <summary>Bitmaps allocated so far; a test seam.</summary>
    internal int AllocationCount { get; private set; }

    public Bitmap Get(int width, int height)
    {
        if (_bitmap is { } current && current.Width == width && current.Height == height)
        {
            return current;
        }

        _bitmap?.Dispose();
        _bitmap = null;
        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        AllocationCount++;
        return _bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
