using System;
using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Remex.Core.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// The launcher decides whether a stored icon is worth re-extracting by reading its PNG header
/// (RemEx-u4244).
/// </summary>
/// <remarks>
/// <para>
/// Every entry created before the Windows extractor was fixed carries a baked 32x32 bitmap, because
/// <c>Icon.ExtractAssociatedIcon</c> could not produce anything larger. The savefile is the source of
/// truth and nothing re-reads the executable, so fixing the extractor alone leaves those entries
/// blurry forever. The load path checks the stored size and refreshes what is too small.
/// </para>
/// <para>
/// The check is header-only and that is the part worth guarding. It runs over every entry on every
/// launcher load; decoding each bitmap in full just to read two integers would put image decoding on
/// the UI startup path. Reading IHDR is a fixed 24-byte prefix regardless of how large the image is.
/// </para>
/// <para>
/// A malformed or non-PNG value must come back as null rather than throwing. The icon field is
/// free-form base64 that has, at various times, been round-tripped through JSON, an Android client
/// and a restored savefile — and a launcher page that throws on load is a worse failure than a soft
/// icon.
/// </para>
/// </remarks>
public sealed class LauncherIconResolutionTests
{
    /// <summary>Builds the first 24 bytes of a PNG — signature, chunk length, IHDR, width, height.</summary>
    private static string PngHeaderBase64(int width, int height)
    {
        var bytes = new byte[24];

        // 8-byte PNG signature.
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        // IHDR chunk: 4-byte big-endian length (always 13), then the type.
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        return Convert.ToBase64String(bytes);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]  // what the old ExtractAssociatedIcon path always produced
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void ReadsTheStoredPngDimensions(int edge)
    {
        AppLauncherViewModel.TryGetPngEdge(PngHeaderBase64(edge, edge)).Should().Be(edge);
    }

    [Fact]
    public void ReportsTheShorterEdgeOfANonSquareIcon()
    {
        // The tile draws the icon into a square box, so the short edge is what determines whether it
        // has to be upscaled. Taking the long edge would let a 256x24 banner pass as high-resolution.
        AppLauncherViewModel.TryGetPngEdge(PngHeaderBase64(256, 24)).Should().Be(24);
    }

    [Fact]
    public void TheFallbackIconReadsAsLowResolution()
    {
        // The shared fallback is a 32x32 placeholder. It must be seen as low-resolution, otherwise an
        // entry that once failed extraction would never be retried.
        var edge = AppLauncherViewModel.TryGetPngEdge(IconExtractionService.FallbackBase64Icon);

        edge.Should().NotBeNull();
        edge!.Value.Should().BeLessThan(64,
            "an entry holding the placeholder is exactly the case the refresh pass exists for");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!!")]
    [InlineData("QUJD")]                                  // valid base64, far too short to be a PNG
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // valid base64, wrong signature
    public void MalformedValuesReturnNullRatherThanThrowing(string? stored)
    {
        var read = () => AppLauncherViewModel.TryGetPngEdge(stored);

        read.Should().NotThrow("the launcher page loads on the UI thread and must survive a bad savefile");
        read().Should().BeNull();
    }

    [Fact]
    public void AJpegIsNotMistakenForAPng()
    {
        // JPEG SOI + APP0/JFIF. Correctly sized image data in the wrong container still has no IHDR,
        // and guessing at offsets would produce a nonsense dimension rather than a null.
        var jpeg = new byte[24];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00 }.CopyTo(jpeg, 0);

        AppLauncherViewModel.TryGetPngEdge(Convert.ToBase64String(jpeg)).Should().BeNull();
    }

    [Fact]
    public void APngWithATruncatedHeaderReturnsNull()
    {
        // A savefile clipped mid-write leaves a valid PNG signature with nothing behind it.
        var truncated = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        AppLauncherViewModel.TryGetPngEdge(truncated).Should().BeNull();
    }

    // ═══════════════ NeedsSharperIcon ═══════════════

    /// <summary>Builds a base64 PNG header padded out to a chosen encoded byte count.</summary>
    private static string PngOfDensity(int edge, double bytesPerPixel)
    {
        var target = Math.Max(24, (int)(edge * (double)edge * bytesPerPixel));
        var bytes = Convert.FromBase64String(PngHeaderBase64(edge, edge));
        Array.Resize(ref bytes, target);
        return Convert.ToBase64String(bytes);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    public void AnIconSmallerThanTheTileIsRefreshed(int edge)
    {
        AppLauncherViewModel.NeedsSharperIcon(PngOfDensity(edge, 0.5)).Should().BeTrue();
    }

    [Fact]
    public void AFullSizeIconWithRealArtworkIsLeftAlone()
    {
        // 0.053 bytes/pixel was the leanest genuine 256px icon measured on a real launcher.
        AppLauncherViewModel.NeedsSharperIcon(PngOfDensity(256, 0.053)).Should().BeFalse(
            "re-extracting an icon that is already correct writes the savefile on every load");
    }

    [Fact]
    public void AParkedCanvasIsRefreshedDespiteMeasuringFullSize()
    {
        // THE CASE A DIMENSION CHECK MISSES. SHIL_JUMBO returns a full 256x256 bitmap for a file with
        // no 256px icon variant, parking the small artwork in a corner and leaving the rest
        // transparent. It measures 256 wide and renders as a thumbnail adrift in an empty tile.
        // Real examples from a 50-entry launcher: 7-Zip at 0.010 bytes/pixel, FastCopy at 0.011.
        AppLauncherViewModel.NeedsSharperIcon(PngOfDensity(256, 0.010)).Should().BeTrue(
            "a 256px icon encoding to almost nothing is transparent filler, not artwork");

        AppLauncherViewModel.NeedsSharperIcon(PngOfDensity(256, 0.011)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a png")]
    public void AnUnreadableIconIsRefreshed(string? stored)
    {
        AppLauncherViewModel.NeedsSharperIcon(stored).Should().BeTrue(
            "an entry whose icon cannot be read has nothing to lose by being re-extracted");
    }

    [Fact]
    public void TheFallbackPlaceholderIsRefreshed()
    {
        AppLauncherViewModel.NeedsSharperIcon(IconExtractionService.FallbackBase64Icon).Should().BeTrue(
            "an entry that once failed extraction should be retried, not left holding the placeholder");
    }

    [Fact]
    public void AZeroSizedPngReturnsNull()
    {
        // A zero dimension is not a measurement, it is a corrupt header. Returning 0 would be
        // reporting a size the image does not have; null says "could not read this", which is the
        // truthful answer and lands on the same refresh path anyway.
        AppLauncherViewModel.TryGetPngEdge(PngHeaderBase64(0, 0)).Should().BeNull();
    }
}
