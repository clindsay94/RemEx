using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using SkiaSharp;

namespace Remex.Agent.Tests;

/// <summary>
/// P1-15: revisiting a folder re-requested and re-encoded every thumbnail with no cache on the host
/// side. The cache is keyed on path + mtime + size, not path alone, so a changed file never serves a
/// stale thumbnail (C15).
/// </summary>
public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private (FileTransferService service, string filePath) CreateServiceWithFile(byte[] initialContent)
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-thumb-" + Guid.NewGuid().ToString("N"));
        var rootDir = Path.Combine(baseTemp, "root");
        Directory.CreateDirectory(rootDir);
        _tempDirs.Add(baseTemp);

        var filePath = Path.Combine(rootDir, "pic.jpg");
        File.WriteAllBytes(filePath, initialContent);

        var configPath = Path.Combine(baseTemp, "roots.json");
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, configPath);
        service.SeedRootsForTests(("root-1", "Test Root", rootDir, false, false, false, false, false));

        return (service, filePath);
    }

    private static byte[] SolidColorJpeg(SKColor color, int size = 32)
    {
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public async Task RevisitingTheSameUnchangedFileServesTheCachedThumbnailNotAFreshEncode()
    {
        var (service, filePath) = CreateServiceWithFile(SolidColorJpeg(SKColors.Red));

        var first = await service.GetThumbnailBase64Async("root-1", "pic.jpg", 64, CancellationToken.None);
        Assert.NotNull(first);

        // Overwrite with DIFFERENT pixel content but the SAME byte count and restore the ORIGINAL
        // mtime exactly — the cache key (path+mtime+size) is unchanged, so this proves an actual
        // cache HIT rather than an idempotent re-encode: without the cache, the second call would
        // decode the new (blue) file and return a genuinely different result.
        var original = File.GetLastWriteTimeUtc(filePath);
        var sameSizeDifferentContent = SolidColorJpeg(SKColors.Blue);
        Assert.Equal(new FileInfo(filePath).Length, sameSizeDifferentContent.Length);
        File.WriteAllBytes(filePath, sameSizeDifferentContent);
        File.SetLastWriteTimeUtc(filePath, original);

        var second = await service.GetThumbnailBase64Async("root-1", "pic.jpg", 64, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task AChangedFileNeverServesAStaleThumbnail()
    {
        var (service, filePath) = CreateServiceWithFile(SolidColorJpeg(SKColors.Red));

        var before = await service.GetThumbnailBase64Async("root-1", "pic.jpg", 64, CancellationToken.None);
        Assert.NotNull(before);

        // Overwrite with a genuinely different image (different color -> different encoded bytes) and
        // force the mtime forward, exactly what a real file edit between two folder visits looks like.
        // A path-only cache key (the bug C15 warns about) would incorrectly return `before` here.
        File.WriteAllBytes(filePath, SolidColorJpeg(SKColors.Blue));
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(5));

        var after = await service.GetThumbnailBase64Async("root-1", "pic.jpg", 64, CancellationToken.None);

        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
