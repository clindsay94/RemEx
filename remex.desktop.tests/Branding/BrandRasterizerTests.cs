using FluentAssertions;
using Remex.Branding;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

public class BrandRasterizerTests
{
    [Fact]
    public void RenderPng_Square_ProducesDecodableImageOfExactSize()
    {
        byte[] png = BrandRasterizer.RenderPng(64, 64);
        png.Should().NotBeNullOrEmpty();
        using var bmp = SKBitmap.Decode(png);
        bmp.Width.Should().Be(64);
        bmp.Height.Should().Be(64);
    }

    [Fact]
    public void RenderPng_NonSquare_HonorsBothDimensions()
    {
        using var bmp = SKBitmap.Decode(BrandRasterizer.RenderPng(310, 150));
        bmp.Width.Should().Be(310);
        bmp.Height.Should().Be(150);
    }

    [Fact]
    public void RenderPng_DrawsContent_NotBlankTransparent()
    {
        using var bmp = SKBitmap.Decode(BrandRasterizer.RenderPng(48, 48));
        // The gradient fills the whole square, so the center pixel must be opaque.
        bmp.GetPixel(24, 24).Alpha.Should().Be(255);
    }
}
