using System;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop.Linux.Capture;
using Remex.Agent.Services.ScreenCapture;
using SkiaSharp;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Perf audit P4-13: the Linux raw H.264 path's BGRA passthrough must produce exactly the bytes the
/// Skia path did. A mismatch here would desync the rawvideo pipe or shift the picture, which on a
/// live stream shows up as garbage or silence rather than an error, so every case is compared
/// against the Skia path itself rather than a hand-written expectation.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxRawPassthroughTests
{
    private const uint SpaBgra = 12u;
    private const uint SpaBgrx = 8u;
    private const uint SpaRgba = 11u;

    private static LinuxFrameSnapshot Frame(int width, int height, int stride, uint format, int seed = 7)
    {
        // Random bytes, alpha included: BGRx carries garbage in the X byte, and the passthrough must
        // copy it untouched exactly as Skia's same-alpha-type copy does.
        var data = new byte[stride * height + 64];
        new Random(seed).NextBytes(data);
        return new LinuxFrameSnapshot
        {
            Width = width,
            Height = height,
            Stride = stride,
            Format = format,
            TimestampNs = 0,
            Seq = 0,
            BufferKind = LinuxBufferKind.Memfd,
            Data = data,
        };
    }

    private static byte[] Encode(LinuxFrameSnapshot frame, double scale, int cx, int cy, int cw, int ch, bool passthrough) =>
        LinuxScreenCaptureService.EncodeRaw(frame, scale, NullLogger.Instance, cx, cy, cw, ch, passthrough);

    [Theory]
    [InlineData(SpaBgra, 0, 0, 0, 0)]        // full frame, no crop
    [InlineData(SpaBgrx, 0, 0, 0, 0)]        // X byte is garbage
    [InlineData(SpaBgra, 16, 8, 64, 32)]     // monitor crop inside a larger virtual desktop
    [InlineData(SpaBgrx, 32, 16, 96, 48)]    // crop touching the right/bottom edges
    public void Passthrough_MatchesSkia_ByteForByte(uint format, int cx, int cy, int cw, int ch)
    {
        var frame = Frame(128, 64, 128 * 4 + 32, format); // padded stride

        Assert.True(LinuxScreenCaptureService.TryCopyBgraPassthrough(
            frame, SKColorType.Bgra8888, 1.0, cx, cy, cw, ch, out _));

        var fast = Encode(frame, 1.0, cx, cy, cw, ch, passthrough: true);
        var skia = Encode(frame, 1.0, cx, cy, cw, ch, passthrough: false);

        Assert.NotEmpty(skia);
        Assert.Equal(skia.Length, fast.Length);
        Assert.True(skia.AsSpan().SequenceEqual(fast), "passthrough bytes differ from the Skia path");
    }

    [Fact]
    public void Scaling_OddSize_Rgba_AndOutOfBoundsCrop_StayOnTheSkiaPath()
    {
        var frame = Frame(128, 64, 128 * 4, SpaBgra);
        Assert.False(LinuxScreenCaptureService.TryCopyBgraPassthrough(frame, SKColorType.Bgra8888, 0.5, 0, 0, 0, 0, out _));
        Assert.False(LinuxScreenCaptureService.TryCopyBgraPassthrough(frame, SKColorType.Rgba8888, 1.0, 0, 0, 0, 0, out _));
        Assert.False(LinuxScreenCaptureService.TryCopyBgraPassthrough(frame, SKColorType.Bgra8888, 1.0, 100, 0, 64, 32, out _));

        var odd = Frame(127, 63, 127 * 4, SpaBgra);
        Assert.False(LinuxScreenCaptureService.TryCopyBgraPassthrough(odd, SKColorType.Bgra8888, 1.0, 0, 0, 0, 0, out _));

        var rgba = Frame(128, 64, 128 * 4, SpaRgba);
        var viaEncode = Encode(rgba, 1.0, 0, 0, 0, 0, passthrough: true);
        var viaSkia = Encode(rgba, 1.0, 0, 0, 0, 0, passthrough: false);
        Assert.True(viaSkia.AsSpan().SequenceEqual(viaEncode));
    }

    [Fact]
    public void Passthrough_ReturnsAFreshArray_NotTheSource()
    {
        var frame = Frame(64, 32, 64 * 4, SpaBgra);
        Assert.True(LinuxScreenCaptureService.TryCopyBgraPassthrough(frame, SKColorType.Bgra8888, 1.0, 0, 0, 0, 0, out var result));
        Assert.NotSame(frame.Data, result);
        Assert.Equal(64 * 32 * 4, result.Length);
    }
}
