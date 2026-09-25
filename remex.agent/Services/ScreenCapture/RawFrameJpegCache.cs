namespace Remex.Agent.Services.ScreenCapture;

/// <summary>
/// Remembers the last raw-BGRA frame the MJPEG path encoded and its JPEG, so an unchanged frame skips
/// the encode and comes back as the SAME JPEG memory (P1-13).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. The WGC tier hands back raw BGRA and the MJPEG path JPEG-encodes it itself. On a
/// static desktop WGC returns its cached array on every tick, and that encode (a GDI+ JPEG of the whole
/// frame) was being paid again per tick for identical pixels. The DXGI tier needs no equivalent: it
/// caches its own encoded frame and replays it without re-encoding.
/// </para>
/// <para>
/// THE COMPARE IS CHEAP BY CONSTRUCTION. Reference equality first — the static-desktop case, free.
/// Only a DIFFERENT array of the same size falls through to <see cref="MemoryExtensions.SequenceEqual{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>,
/// which is vectorised and exits at the first differing byte; that is a small fraction of the encode
/// it can save, and it is exact, unlike a sampled compare that could miss a caret blink.
/// </para>
/// <para>
/// Returning the cached memory again is read-only aliasing, the same thing the DXGI tier already does:
/// the JPEG is never written after it is produced (see ScreenCaptureResult.Pixels, RemEx-lcp8).
/// Thread-safe: the entry is immutable and swapped as one reference; racing callers may both encode,
/// and either result is correct.
/// </para>
/// </remarks>
internal sealed class RawFrameJpegCache
{
    private sealed record Entry(byte[] Raw, int Width, int Height, int Quality, ReadOnlyMemory<byte> Jpeg);

    private volatile Entry? _last;

    public delegate ReadOnlyMemory<byte> JpegEncoder(byte[] bgra, int width, int height, int quality);

    /// <summary>
    /// Returns the cached JPEG when <paramref name="raw"/> matches the last encoded frame (same array,
    /// or same contents) at the same size and quality; otherwise encodes and caches the result.
    /// An empty encode result is returned but not cached.
    /// </summary>
    public ReadOnlyMemory<byte> GetOrEncode(byte[] raw, int width, int height, int quality, JpegEncoder encode)
    {
        var last = _last;
        if (last is not null &&
            last.Width == width && last.Height == height && last.Quality == quality)
        {
            if (ReferenceEquals(last.Raw, raw))
            {
                return last.Jpeg;
            }

            if (last.Raw.Length == raw.Length && raw.AsSpan().SequenceEqual(last.Raw))
            {
                // Same pixels in a new array: keep the JPEG, but track the new array so the next
                // replay of it hits the free reference check and the old one can be collected.
                _last = last with { Raw = raw };
                return last.Jpeg;
            }
        }

        var jpeg = encode(raw, width, height, quality);
        if (!jpeg.IsEmpty)
        {
            _last = new Entry(raw, width, height, quality, jpeg);
        }

        return jpeg;
    }
}
