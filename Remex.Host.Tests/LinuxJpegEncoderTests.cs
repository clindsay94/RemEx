using System;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Host.Services.RemoteDesktop.Linux.Capture;
using SkiaSharp;

namespace Remex.Host.Tests;

/// <summary>
/// Tests for <see cref="LinuxJpegEncoder"/>. All tests are Linux-only because
/// the encoder and <see cref="LinuxFrameSnapshot"/> are annotated
/// <see cref="SupportedOSPlatformAttribute"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxJpegEncoderTests
{
    private static readonly ILogger NullLogger = NullLogger<LinuxJpegEncoderTests>.Instance;

    // ── Helpers ──────────────────────────────────────────────────────────

    private static LinuxFrameSnapshot MakeBgraSnapshot(int width, int height, uint format = 12u)
    {
        // 12 = SPA_VIDEO_FORMAT_BGRA
        var stride = width * 4;
        var data = new byte[stride * height];
        // Fill with a gradient so the JPEG encoder has real content to compress.
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                data[i + 0] = (byte)(x & 0xFF);       // B
                data[i + 1] = (byte)(y & 0xFF);       // G
                data[i + 2] = (byte)((x + y) & 0xFF); // R
                data[i + 3] = 255;                    // A
            }

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

    private static bool IsValidJpeg(byte[] bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0xFF && bytes[1] == 0xD8 &&   // SOI
        bytes[^2] == 0xFF && bytes[^1] == 0xD9;   // EOI

    // ── Test 1 ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_ValidBgra_ProducesValidJpeg()
    {
        var frame = MakeBgraSnapshot(256, 256);

        var jpeg = LinuxJpegEncoder.Encode(frame, quality: 75, scale: 1.0, NullLogger, out _);

        Assert.NotEmpty(jpeg);
        Assert.True(IsValidJpeg(jpeg),
            "Expected a valid JPEG (SOI=0xFFD8, EOI=0xFFD9).");
    }

    // ── Test 2 ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_WithScale_ProducesCorrectDimensions()
    {
        var frame = MakeBgraSnapshot(256, 256);

        var jpeg = LinuxJpegEncoder.Encode(frame, quality: 75, scale: 0.5, NullLogger, out _);

        Assert.NotEmpty(jpeg);
        Assert.True(IsValidJpeg(jpeg));

        // Decode and verify dimensions
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.NotNull(decoded);
        Assert.Equal(128, decoded!.Width);
        Assert.Equal(128, decoded.Height);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_UnsupportedFormatNV12_ReturnsEmpty()
    {
        var frame = new LinuxFrameSnapshot
        {
            Width = 64,
            Height = 64,
            Stride = 64 * 4,
            Format = 22u, // SPA_VIDEO_FORMAT_NV12
            TimestampNs = 0,
            Seq = 0,
            BufferKind = LinuxBufferKind.Memfd,
            Data = new byte[64 * 64 * 4],
        };

        var result = LinuxJpegEncoder.Encode(frame, quality: 75, scale: 1.0, NullLogger, out _);

        Assert.Empty(result);
    }

    // ── Test 4 ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_UnknownFormat_ReturnsEmpty()
    {
        var frame = new LinuxFrameSnapshot
        {
            Width = 64,
            Height = 64,
            Stride = 64 * 4,
            Format = 0xDEADBEEFu,
            TimestampNs = 0,
            Seq = 0,
            BufferKind = LinuxBufferKind.Memfd,
            Data = new byte[64 * 64 * 4],
        };

        var result = LinuxJpegEncoder.Encode(frame, quality: 75, scale: 1.0, NullLogger, out _);

        Assert.Empty(result);
    }

    // ── Test 5 ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_ZeroFormat_TreatedAsBgraProducesValidJpeg()
    {
        // Format 0 is the native bridge stub default (memset-to-zero).
        // Encoder assumes BGRA for this case.
        var frame = MakeBgraSnapshot(128, 128, format: 0u);

        var jpeg = LinuxJpegEncoder.Encode(frame, quality: 75, scale: 1.0, NullLogger, out var tag);

        Assert.NotEmpty(jpeg);
        Assert.True(IsValidJpeg(jpeg));
        Assert.Contains("assumed", tag, StringComparison.Ordinal);
    }

    // ── Test 6 ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_NullDataAndZeroRawData_ReturnsEmpty()
    {
        var frame = new LinuxFrameSnapshot
        {
            Width = 64,
            Height = 64,
            Stride = 64 * 4,
            Format = 12u, // BGRA
            TimestampNs = 0,
            Seq = 0,
            BufferKind = LinuxBufferKind.DmaBuf,
            Data = null,         // no managed copy
            RawData = IntPtr.Zero, // no raw pointer either
        };

        var result = LinuxJpegEncoder.Encode(frame, quality: 75, scale: 1.0, NullLogger, out _);

        Assert.Empty(result);
    }
}
