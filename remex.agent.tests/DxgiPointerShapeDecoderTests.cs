using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

public sealed class DxgiPointerShapeDecoderTests
{
    [Fact]
    public void TryDecode_ColorShape_NormalizesPitch()
    {
        var info = new DxgiPointerShapeDecoder.PointerShapeInfo(
            DxgiPointerShapeDecoder.PointerShapeTypeColor,
            Width: 2,
            Height: 2,
            Pitch: 12,
            HotspotX: 3,
            HotspotY: 4);

        var buffer = new byte[]
        {
            1, 2, 3, 4,
            5, 6, 7, 8,
            99, 99, 99, 99,
            9, 10, 11, 12,
            13, 14, 15, 16,
            88, 88, 88, 88,
        };

        var decoded = DxgiPointerShapeDecoder.TryDecode(info, buffer, out var snapshot);

        Assert.True(decoded);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Width);
        Assert.Equal(2, snapshot.Height);
        Assert.Equal(3, snapshot.HotspotX);
        Assert.Equal(4, snapshot.HotspotY);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            snapshot.ShapeBytes);
    }

    [Fact]
    public void TryDecode_MonochromeShape_DecodesTransparentBlackAndWhitePixels()
    {
        var info = new DxgiPointerShapeDecoder.PointerShapeInfo(
            DxgiPointerShapeDecoder.PointerShapeTypeMonochrome,
            Width: 4,
            Height: 1,
            Pitch: 1,
            HotspotX: 0,
            HotspotY: 0);

        var buffer = new byte[]
        {
            0b1000_0000,
            0b0010_0000,
        };

        var decoded = DxgiPointerShapeDecoder.TryDecode(info, buffer, out var snapshot);

        Assert.True(decoded);
        Assert.NotNull(snapshot);
        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 0,
                0, 0, 0, 255,
                255, 255, 255, 255,
                0, 0, 0, 255,
            },
            snapshot.ShapeBytes);
    }

    [Fact]
    public void TryDecode_MonochromeInvertShape_ReturnsFalse()
    {
        var info = new DxgiPointerShapeDecoder.PointerShapeInfo(
            DxgiPointerShapeDecoder.PointerShapeTypeMonochrome,
            Width: 1,
            Height: 1,
            Pitch: 1,
            HotspotX: 0,
            HotspotY: 0);

        var buffer = new byte[]
        {
            0b1000_0000,
            0b1000_0000,
        };

        var decoded = DxgiPointerShapeDecoder.TryDecode(info, buffer, out var snapshot);

        Assert.False(decoded);
        Assert.Null(snapshot);
    }

    [Fact]
    public void TryDecode_MaskedColorShape_ConvertsReplacePixelsToOpaque()
    {
        var info = new DxgiPointerShapeDecoder.PointerShapeInfo(
            DxgiPointerShapeDecoder.PointerShapeTypeMaskedColor,
            Width: 2,
            Height: 1,
            Pitch: 8,
            HotspotX: 1,
            HotspotY: 2);

        var buffer = new byte[]
        {
            10, 20, 30, 0,
            40, 50, 60, 0,
        };

        var decoded = DxgiPointerShapeDecoder.TryDecode(info, buffer, out var snapshot);

        Assert.True(decoded);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.HotspotX);
        Assert.Equal(2, snapshot.HotspotY);
        Assert.Equal(
            new byte[]
            {
                10, 20, 30, 255,
                40, 50, 60, 255,
            },
            snapshot.ShapeBytes);
    }

    [Fact]
    public void TryDecode_MaskedColorXorShape_ReturnsFalse()
    {
        var info = new DxgiPointerShapeDecoder.PointerShapeInfo(
            DxgiPointerShapeDecoder.PointerShapeTypeMaskedColor,
            Width: 1,
            Height: 1,
            Pitch: 4,
            HotspotX: 0,
            HotspotY: 0);

        var buffer = new byte[]
        {
            10, 20, 30, 255,
        };

        var decoded = DxgiPointerShapeDecoder.TryDecode(info, buffer, out var snapshot);

        Assert.False(decoded);
        Assert.Null(snapshot);
    }
}
