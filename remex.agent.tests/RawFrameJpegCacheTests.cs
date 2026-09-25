using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// P1-13: the WGC MJPEG tier's encode cache. An unchanged raw frame (same array, or same pixels in a new
/// array) must skip the JPEG encode and return the previous JPEG memory, so the handler can recognise it
/// as unchanged; anything else must be encoded.
/// </summary>
public class RawFrameJpegCacheTests
{
    private sealed class CountingEncoder
    {
        public int Calls { get; private set; }

        public ReadOnlyMemory<byte> Encode(byte[] bgra, int width, int height, int quality)
        {
            Calls++;
            return new byte[] { 0xFF, 0xD8, bgra[0], (byte)quality, 0xFF, 0xD9 };
        }
    }

    private static byte[] Raw(byte fill, int width = 4, int height = 2)
    {
        var raw = new byte[width * height * 4];
        Array.Fill(raw, fill);
        return raw;
    }

    [Fact]
    public void TheSameRawArraySkipsTheEncodeAndReturnsTheSameJpeg()
    {
        var cache = new RawFrameJpegCache();
        var encoder = new CountingEncoder();
        var raw = Raw(7);

        var first = cache.GetOrEncode(raw, 4, 2, 50, encoder.Encode);
        var second = cache.GetOrEncode(raw, 4, 2, 50, encoder.Encode);

        Assert.Equal(1, encoder.Calls);
        Assert.True(first.Equals(second), "An unchanged frame must come back as the same JPEG memory.");
    }

    [Fact]
    public void IdenticalPixelsInANewArraySkipTheEncode()
    {
        var cache = new RawFrameJpegCache();
        var encoder = new CountingEncoder();

        var first = cache.GetOrEncode(Raw(7), 4, 2, 50, encoder.Encode);
        var second = cache.GetOrEncode(Raw(7), 4, 2, 50, encoder.Encode);

        Assert.Equal(1, encoder.Calls);
        Assert.True(first.Equals(second));
    }

    [Fact]
    public void ChangedPixelsAreEncoded()
    {
        var cache = new RawFrameJpegCache();
        var encoder = new CountingEncoder();
        var changed = Raw(7);
        changed[^1] = 8; // one byte, at the very end: the compare must not stop short

        var first = cache.GetOrEncode(Raw(7), 4, 2, 50, encoder.Encode);
        var second = cache.GetOrEncode(changed, 4, 2, 50, encoder.Encode);

        Assert.Equal(2, encoder.Calls);
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void AQualityOrSizeChangeIsEncoded()
    {
        var cache = new RawFrameJpegCache();
        var encoder = new CountingEncoder();
        var raw = Raw(7);

        cache.GetOrEncode(raw, 4, 2, 50, encoder.Encode);
        cache.GetOrEncode(raw, 4, 2, 70, encoder.Encode);
        cache.GetOrEncode(raw, 2, 4, 70, encoder.Encode);

        Assert.Equal(3, encoder.Calls);
    }

    [Fact]
    public void AFailedEncodeIsNotCached()
    {
        var cache = new RawFrameJpegCache();
        var calls = 0;
        ReadOnlyMemory<byte> Failing(byte[] bgra, int w, int h, int q)
        {
            calls++;
            return ReadOnlyMemory<byte>.Empty;
        }

        var raw = Raw(7);
        Assert.True(cache.GetOrEncode(raw, 4, 2, 50, Failing).IsEmpty);
        Assert.True(cache.GetOrEncode(raw, 4, 2, 50, Failing).IsEmpty);
        Assert.Equal(2, calls);
    }
}
