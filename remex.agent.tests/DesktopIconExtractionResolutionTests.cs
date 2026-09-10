using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Remex.Agent.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The Windows icon extractor must return the high-resolution icon, not a 32x32 one (RemEx-u4244).
/// </summary>
/// <remarks>
/// <para>
/// This used to be a single call to <c>Icon.ExtractAssociatedIcon</c>, which always hands back
/// 32x32 no matter what the executable actually contains. The launcher then drew it in an 80px
/// tile — 2.5x at 100% scaling, 5x on a 4K display at 200%. Every tile was soft.
/// </para>
/// <para>
/// NOTHING FAILED, which is why it survived. There was no exception, no fallback, no log line: the
/// extraction succeeded and simply produced a small image. Only looking at the rendered window
/// showed it, and remex.desktop.tests has no headless render. Asserting the stored pixel dimensions
/// is the check that would have caught it, so that is what this does.
/// </para>
/// <para>
/// The targets are Windows system executables that ship 256px icon variants. If a future Windows
/// release stops shipping them the assertion becomes wrong rather than the code — the tests name the
/// file they used so that is diagnosable rather than mysterious.
/// </para>
/// </remarks>
public sealed class DesktopIconExtractionResolutionTests
{
    private const string WindowsOnlyBecause =
        "the shell system image list (SHGetImageList/SHIL_JUMBO) is the mechanism under test, and on "
        + "Linux ExtractIconAsBase64 takes an entirely separate .desktop-file path";

    private static string SystemExecutable(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    /// <summary>Reads width/height out of a base64 PNG's IHDR chunk.</summary>
    private static (int Width, int Height) PngSize(string base64)
    {
        var bytes = Convert.FromBase64String(base64);

        Assert.True(bytes.Length >= 24, "a PNG shorter than its own header is not a PNG");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(bytes, 12, 4));

        return (
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void ExtractsAnIconLargerThanTheOld32PixelCeiling()
    {
        var target = SystemExecutable("notepad.exe");
        Assert.True(File.Exists(target), $"{target} is a stock Windows binary; if it is gone this guard needs a new target rather than a silent pass");

        var (width, height) = PngSize(new DesktopIconExtractionService().ExtractIconAsBase64(target));

        Assert.True(width > 32,
            $"{target} ships a large icon variant, but extraction produced {width}px. 32 means the "
            + "shell image list path did not run and it fell back to ExtractAssociatedIcon, which is "
            + "the bug this replaced");
        Assert.Equal(width, height); // launcher icons are drawn in a square tile
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void ExtractsAtLeastTheResolutionTheLauncherTileDraws()
    {
        var target = SystemExecutable("notepad.exe");
        Assert.True(File.Exists(target), $"{target} is a stock Windows binary; if it is gone this guard needs a new target rather than a silent pass");

        var (width, _) = PngSize(new DesktopIconExtractionService().ExtractIconAsBase64(target));

        Assert.True(width >= DesktopIconExtractionService.LowResolutionIconEdge,
            $"extraction produced {width}px. AppLauncherViewModel re-extracts anything below "
            + $"{DesktopIconExtractionService.LowResolutionIconEdge}px on every load, so an extractor "
            + "that cannot clear its own threshold makes that pass run forever without ever improving");
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void NeverExceedsTheStoredSizeCap()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Assert.True(File.Exists(target), $"{target} is a stock Windows binary; if it is gone this guard needs a new target rather than a silent pass");

        var (width, height) = PngSize(new DesktopIconExtractionService().ExtractIconAsBase64(target));

        // The whole launcher list travels in one WebSocket message and MessageSerializer caps that
        // at 4 MB. Unbounded icons would spend that budget on a handful of entries.
        Assert.True(width <= 256, $"stored icon was {width}px wide");
        Assert.True(height <= 256, $"stored icon was {height}px tall");
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void TheIconIsNotAnEmptyCanvas()
    {
        var target = SystemExecutable("notepad.exe");
        Assert.True(File.Exists(target), $"{target} is a stock Windows binary; if it is gone this guard needs a new target rather than a silent pass");

        var encoded = new DesktopIconExtractionService().ExtractIconAsBase64(target);
        var (width, height) = PngSize(encoded);

        // SHIL_JUMBO returns a 256x256 bitmap even for a file with no 256px variant, parking the
        // smaller artwork in the top-left and leaving the rest transparent. Stored uncropped that is
        // a tiny icon adrift in a big empty tile — visually worse than the blur it replaced. The
        // encoded byte count is the cheap proxy: a mostly-empty PNG compresses to almost nothing.
        var byteCount = Convert.FromBase64String(encoded).Length;
        var pixels = width * (long)height;

        Assert.True(byteCount > pixels / 400,
            $"a {width}x{height} icon encoding to {byteCount} bytes is transparent filler, not artwork");
    }

    [Fact]
    public void AMissingFileFallsBackRatherThanThrowing()
    {
        // The launcher refresh pass calls this for entries whose target may have been uninstalled.
        var absent = Path.Combine(Path.GetTempPath(), $"remex-absent-{Guid.NewGuid():N}.exe");

        var result = new DesktopIconExtractionService().ExtractIconAsBase64(absent);

        Assert.False(string.IsNullOrWhiteSpace(result),
            "callers expect a usable placeholder, not an empty string");
    }
}
