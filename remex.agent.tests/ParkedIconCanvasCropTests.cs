using System;
using System.Drawing;
using System.Drawing.Imaging;
using Remex.Agent.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A jumbo icon whose artwork does not fill its canvas must be cropped down to the artwork
/// (RemEx-u4244).
/// </summary>
/// <remarks>
/// <para>
/// SHIL_JUMBO returns a 256x256 bitmap for every file, including files whose icon resource tops out
/// at 48px. For those it parks the small artwork in a corner and leaves the other ~96% transparent.
/// The launcher scales the whole canvas into its 80px tile, so a 48px icon lands at about 15px in
/// the corner of an empty card — worse than the blur the high-resolution extractor replaced.
/// </para>
/// <para>
/// THE FIRST VERSION OF THIS CROP DID NOTHING FOR 7-ZIP. It keyed on the artwork starting at exactly
/// (0,0), and 7-Zip's icon carries a 3px margin of its own: parked in the corner as expected, three
/// pixels off the anchor the check demanded. The condition is now how much of the canvas the artwork
/// occupies rather than where it is pinned, which is why the off-origin cases below are here.
/// </para>
/// <para>
/// Synthetic bitmaps rather than real executables on purpose. The real trigger is whether a
/// particular file on a particular Windows build happens to ship a 256px variant — not something to
/// pin a regression test to.
/// </para>
/// </remarks>
public sealed class ParkedIconCanvasCropTests
{
    private const string WindowsOnlyBecause =
        "System.Drawing bitmap construction and the crop it feeds are the Windows-only half of the "
        + "extractor; Linux resolves icons through .desktop files and never sees a jumbo canvas";

    /// <summary>A transparent canvas with an opaque block of <paramref name="art"/> px at (x, y).</summary>
    private static Bitmap Canvas(int size, int x, int y, int art)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.FillRectangle(Brushes.OrangeRed, x, y, art, art);
        return bmp;
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void CropsArtworkParkedExactlyInTheCorner()
    {
        using var result = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 0, 0, 48));

        Assert.True(result.Width < 256, $"a 48px block on a 256px canvas was left at {result.Width}px");
        Assert.Equal(result.Width, result.Height);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void CropsArtworkParkedNearButNotAtTheOrigin()
    {
        // The 7-Zip case exactly: parked in the corner, three pixels off the anchor.
        using var result = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 3, 3, 48));

        Assert.True(result.Width < 256,
            $"artwork three pixels off the origin was left at {result.Width}px — this is the case the "
            + "first version of the crop missed, and 7-Zip rendered as a corner thumbnail because of it");
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void CropsSmallArtworkWhereverItSits()
    {
        using var centred = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 104, 104, 48));
        using var bottomRight = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 200, 200, 48));

        Assert.True(centred.Width < 256, "sparse artwork is filler regardless of where it is anchored");
        Assert.True(bottomRight.Width < 256, "including the far corner");
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void NeverCropsBelowTheThresholdThatTriggersARefresh()
    {
        // AppLauncherViewModel re-extracts anything under this on every load. Cropping below it would
        // make the extractor produce icons its own caller immediately rejects, so the refresh pass
        // would re-run forever and never settle.
        using var result = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 0, 0, 16));

        Assert.True(result.Width >= DesktopIconExtractionService.LowResolutionIconEdge,
            $"cropped to {result.Width}px, under the {DesktopIconExtractionService.LowResolutionIconEdge}px refresh threshold");
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void LeavesAnIconThatFillsItsCanvasAlone()
    {
        // A normal icon carries some transparent padding. Trimming that would make cropped tiles sit
        // at a different visual weight from uncropped ones beside them in the grid.
        using var result = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 12, 12, 232));

        Assert.Equal(256, result.Width);
        Assert.Equal(256, result.Height);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void LeavesAFullyTransparentBitmapAlone()
    {
        // No opaque bounds to centre on. Guessing a crop here would invent artwork that is not there.
        using var blank = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using var result = DesktopIconExtractionService.CropParkedCanvas(blank);

        Assert.Equal(256, result.Width);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void LeavesAnAlreadySmallBitmapAlone()
    {
        using var small = Canvas(32, 0, 0, 16);
        using var result = DesktopIconExtractionService.CropParkedCanvas(small);

        Assert.Equal(32, result.Width);
    }

    [WindowsOnlyFact(WindowsOnlyBecause)]
    public void TheCroppedResultStillContainsTheArtwork()
    {
        using var result = DesktopIconExtractionService.CropParkedCanvas(Canvas(256, 3, 3, 48));

        var opaque = 0;
        for (var y = 0; y < result.Height; y++)
            for (var x = 0; x < result.Width; x++)
                if (result.GetPixel(x, y).A != 0)
                    opaque++;

        Assert.Equal(48 * 48, opaque); // the block survived the crop intact, nothing clipped
    }
}
