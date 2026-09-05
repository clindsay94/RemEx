using System;
using System.IO;
using FluentAssertions;
using Remex.Desktop.Services;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>Copying a picked image under the app's own directory, downscaled to 2560 px (spec section 6).</summary>
public class WallpaperImageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"remex-wpstore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string WriteImage(int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"remex-wpsrc-{Guid.NewGuid():N}.png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(0x20, 0x60, 0xA0));
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void ALargeImageIsCopiedWithItsLongestEdgeAtMostTwentyFiveSixty()
    {
        var source = WriteImage(4000, 2000);
        try
        {
            WallpaperImageStore.TryCopyDownscaled(source, _dir, out var copy).Should().BeTrue();

            copy.Should().StartWith(_dir).And.NotBe(source, "the app stores its own copy, never the original's path");
            using var decoded = SKBitmap.Decode(copy!);
            decoded.Width.Should().Be(2560);
            decoded.Height.Should().Be(1280);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void ASmallImageIsCopiedWithoutUpscaling()
    {
        var source = WriteImage(640, 480);
        try
        {
            WallpaperImageStore.TryCopyDownscaled(source, _dir, out var copy).Should().BeTrue();
            using var decoded = SKBitmap.Decode(copy!);
            (decoded.Width, decoded.Height).Should().Be((640, 480));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void AnUnreadableFileFailsWithoutThrowingAndWritesNothing()
    {
        var source = Path.Combine(Path.GetTempPath(), $"remex-wpsrc-{Guid.NewGuid():N}.png");
        File.WriteAllText(source, "not an image");
        try
        {
            WallpaperImageStore.TryCopyDownscaled(source, _dir, out var copy).Should().BeFalse();
            copy.Should().BeNull();
            (Directory.Exists(_dir) ? Directory.GetFiles(_dir).Length : 0).Should().Be(0);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void AMissingFileFailsWithoutThrowing()
    {
        WallpaperImageStore.TryCopyDownscaled(Path.Combine(_dir, "nope.png"), _dir, out var copy).Should().BeFalse();
        copy.Should().BeNull();
    }

    [Fact]
    public void TheDirectoryIsAWallpapersFolderUnderThePerUserRoot()
    {
        WallpaperImageStore.DirectoryFor(@"C:\Users\x\AppData\Local\RemEx")
            .Should().Be(Path.Combine(@"C:\Users\x\AppData\Local\RemEx", "wallpapers"));
    }
}
