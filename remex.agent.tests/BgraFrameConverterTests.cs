using System;
using System.Runtime.InteropServices;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// RD-C guard tests. The H.264 rawvideo pipe desyncs (NVENC emits 0 frames / black screen) if the
/// converted buffer is not EXACTLY ScaledEven(w)*ScaledEven(h)*4 bytes, so these assert size-exactness
/// and content correctness (including a padded GPU row stride) of the no-resample fast path.
/// </summary>
public class BgraFrameConverterTests
{
    [Fact]
    public void TryConvertNoScale_FullScale_NoPadding_CopiesExactly()
    {
        const int w = 4, h = 4; // even dims — the fast path declines odd dims to the even-aligning GDI+ path
        int rowPitch = w * 4;
        var src = BuildContiguousSurface(w, h, out var ptr);
        try
        {
            var result = BgraFrameConverter.TryConvertNoScale(ptr, rowPitch, w, h, 1.0);

            Assert.NotNull(result);
            Assert.Equal(w * h * 4, result!.Length);
            Assert.Equal(src, result);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [Fact]
    public void TryConvertNoScale_PaddedRowPitch_StripsStridePadding()
    {
        const int w = 4, h = 4; // even dims — the fast path declines odd dims to the even-aligning GDI+ path
        int rowBytes = w * 4;
        int rowPitch = rowBytes + 16; // GPU stride padding
        BuildPaddedSurface(w, h, rowPitch, out var ptr);
        try
        {
            var result = BgraFrameConverter.TryConvertNoScale(ptr, rowPitch, w, h, 1.0);

            Assert.NotNull(result);
            Assert.Equal(rowBytes * h, result!.Length);
            for (int i = 0; i < result.Length; i++)
            {
                Assert.Equal((byte)(i & 0xFF), result[i]); // tightly packed, no padding bytes leaked
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void TryConvertNoScale_ShrinkingScale_DeclinesToGdiPath(double scale)
    {
        const int w = 100, h = 100;
        BuildContiguousSurface(w, h, out var ptr);
        try
        {
            // A shrinking scale must return null so the caller falls back to its bilinear scaler.
            Assert.Null(BgraFrameConverter.TryConvertNoScale(ptr, w * 4, w, h, scale));
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [Fact]
    public void TryConvertNoScale_InvalidArgs_ReturnNull()
    {
        Assert.Null(BgraFrameConverter.TryConvertNoScale(IntPtr.Zero, 16, 4, 4, 1.0));

        BuildContiguousSurface(4, 4, out var ptr);
        try
        {
            Assert.Null(BgraFrameConverter.TryConvertNoScale(ptr, 8, 4, 4, 1.0)); // rowPitch < width*4
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static byte[] BuildContiguousSurface(int w, int h, out IntPtr ptr)
    {
        var bytes = new byte[w * h * 4];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return bytes;
    }

    private static void BuildPaddedSurface(int w, int h, int rowPitch, out IntPtr ptr)
    {
        int rowBytes = w * 4;
        ptr = Marshal.AllocHGlobal(rowPitch * h);
        var row = new byte[rowBytes];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < rowBytes; x++) row[x] = (byte)((y * rowBytes + x) & 0xFF);
            Marshal.Copy(row, 0, ptr + y * rowPitch, rowBytes);
        }
    }
}
