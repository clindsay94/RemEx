using System;
using System.Runtime.InteropServices;

namespace Remex.Core.Services;

/// <summary>
/// Fast, allocation-lean conversion of a mapped BGRA32 surface (a WGC/DXGI staging texture) into a
/// tightly-packed BGRA byte buffer — WITHOUT System.Drawing / GDI+.
///
/// REGRESSION GUARD (RD-C): the previous capture path wrapped every frame in <c>System.Drawing.Bitmap</c>
/// objects and ran a GDI+ <c>Graphics.DrawImage</c> software blit (DXGI did this unconditionally; both
/// backends did a second blit to downscale), then a separate <c>LockBits</c> + <c>Marshal.Copy</c> into a
/// fresh multi-MB <c>byte[]</c> — every frame. That CPU + GC cost, not the (hardware NVENC) encoder, is
/// what capped the stream near 30 FPS. This type collapses the no-resample case to a single row-wise
/// <c>Marshal.Copy</c>: no Bitmap, no blit, one allocation. The output length MUST equal
/// <c>CaptureScaling.ScaledEven(w) * ScaledEven(h) * 4</c> or the rawvideo pipe desyncs and NVENC emits
/// zero frames. Pure arithmetic + a managed copy, so it is NativeAOT-safe for the Android JNI build even
/// though only the Windows capture backends call it. See docs/REMOTE_DESKTOP_PERFORMANCE.md.
///
/// Downscaling (scale &lt; 1) is intentionally NOT done here: a scalar managed bilinear would likely be
/// slower than GDI+'s native resampler, so the capture backends keep GDI+ for that case. The right
/// high-FPS configuration on a capable GPU is to capture at full resolution (scale 1.0, this fast path)
/// and let the phone downscale for display.
/// </summary>
public static class BgraFrameConverter
{
    /// <summary>
    /// When <paramref name="scale"/> does not shrink the surface (the even-aligned target equals the
    /// source size), reads the mapped surface directly into a tightly-packed BGRA <c>byte[]</c> via
    /// row-wise <see cref="Marshal.Copy(IntPtr,byte[],int,int)"/> — honoring <paramref name="rowPitch"/>
    /// (the GPU row stride can exceed <c>width*4</c>). Returns <c>null</c> when a downscale is required;
    /// the caller should fall back to its GDI+ bilinear path for that case.
    /// </summary>
    /// <param name="src">Pointer to the mapped BGRA32 surface (top-left origin).</param>
    /// <param name="rowPitch">Source row stride in bytes (>= width*4).</param>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels.</param>
    /// <param name="scale">Requested capture scale (0.25–1.0).</param>
    public static byte[]? TryConvertNoScale(IntPtr src, int rowPitch, int width, int height, double scale)
    {
        if (src == IntPtr.Zero || width <= 0 || height <= 0 || rowPitch < width * 4)
        {
            return null;
        }

        // Only the no-resample case is handled here. A shrinking scale falls back to the caller's scaler.
        if (CaptureScaling.ScaledEven(width, scale) != width ||
            CaptureScaling.ScaledEven(height, scale) != height)
        {
            return null;
        }

        int rowBytes = width * 4;
        var dest = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(src + y * rowPitch, dest, y * rowBytes, rowBytes);
        }

        return dest;
    }
}
