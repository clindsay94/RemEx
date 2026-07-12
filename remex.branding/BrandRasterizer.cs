using SkiaSharp;

namespace Remex.Branding;

/// <summary>Headless rasterizer: renders the brand mark to PNG bytes at any size.</summary>
public static class BrandRasterizer
{
    public static byte[] RenderPng(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Asset dimensions must be positive.");

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Could not create a {width}x{height} Skia surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        SplashBrand.DrawInto(canvas, width, height);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
