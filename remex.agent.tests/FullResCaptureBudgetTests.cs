using System;
using Remex.Agent.Handlers;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Cover for the byte budget that decides whether the H.264 path captures at full resolution
/// (RemEx-zs7l).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-evzv made this path capture full-res and let ffmpeg downscale, which cut capture-thread time
/// from 20.7 ms to 1.7 ms at 2560x1440 — but it moved the cost onto the pipe. The ENCODED size is
/// still clamped to the 4096px hardware limit; the RAW frame is not, so a very large virtual desktop
/// pushes several times the bytes for identical output and the encoder's 3-deep input channel holds
/// that multiple in Large Object Heap on top.
/// </para>
/// <para>
/// HALF OF THESE TESTS EXIST TO STOP THE FIX BEING WORSE THAN THE PROBLEM. A budget set even slightly
/// too low silently demotes configurations that work today — 4K at 120 fps is 3.98 GB/s, under the
/// ~5.2 GB/s pipe ceiling measured on the reference machine — and demotion is not a small penalty: it
/// puts capture back on the GDI+ bilinear path this optimisation existed to escape. The failure would
/// be invisible in a test that only checked the pathological case, which is why the ordinary sizes are
/// asserted explicitly rather than assumed.
/// </para>
/// <para>
/// The budget is expressed per SECOND, not per frame, because the constraint is pipe bandwidth. The
/// same surface can be comfortable at 60 fps and impossible at 120, and the pair of dual-4K cases
/// below is what pins that distinction.
/// </para>
/// </remarks>
public class FullResCaptureBudgetTests
{
    [Theory]
    // The size that motivated full-res capture in the first place.
    [InlineData(2560, 1440, 120)]
    [InlineData(2560, 1440, 60)]
    // Ultrawide: a wide surface is not by itself a problem.
    [InlineData(5120, 1440, 120)]
    // 4K at the maximum frame rate. DELIBERATELY inside the budget — it is under the measured pipe
    // ceiling and works today, so a budget that excluded it would be a silent regression.
    [InlineData(3840, 2160, 120)]
    [InlineData(3840, 2160, 60)]
    // A huge surface at a modest rate: the raw throughput is what matters, not the pixel count.
    [InlineData(7680, 2160, 60)]
    public void OrdinaryConfigurationsKeepFullResolutionCapture(int width, int height, int fps)
    {
        Assert.True(
            RemoteDesktopHandler.FullResCaptureFitsBudget(width, height, fps),
            $"{width}x{height} @{fps}fps is "
            + $"{RemoteDesktopHandler.FullResCaptureBytesPerSecond(width, height, fps) / 1e9:F2} GB/s "
            + "and must stay on the full-resolution fast path");
    }

    [Theory]
    // THE BEAD'S CASE: dual 4K side by side at the maximum frame rate is 7.96 GB/s of raw frames into
    // a pipe measured at about 5.2 GB/s. The stream cannot keep up and the input channel holds
    // hundreds of MB of LOH while it fails to.
    [InlineData(7680, 2160, 120)]
    // Even more extreme, so the boundary is not the only thing covered.
    [InlineData(7680, 4320, 120)]
    public void SurfacesThatCannotFitThroughThePipeFallBack(int width, int height, int fps)
    {
        Assert.False(
            RemoteDesktopHandler.FullResCaptureFitsBudget(width, height, fps),
            $"{width}x{height} @{fps}fps is "
            + $"{RemoteDesktopHandler.FullResCaptureBytesPerSecond(width, height, fps) / 1e9:F2} GB/s "
            + "and must fall back to capturing at the encoded size");
    }

    [Fact]
    public void TheBudgetIsAboutThroughputNotSurfaceSize()
    {
        // The same desktop, two frame rates, opposite answers. This is the property that makes a
        // per-frame cap the wrong shape: it would have to reject this surface outright, penalising a
        // 60 fps stream that the pipe handles perfectly well.
        Assert.True(RemoteDesktopHandler.FullResCaptureFitsBudget(7680, 2160, 60));
        Assert.False(RemoteDesktopHandler.FullResCaptureFitsBudget(7680, 2160, 120));
    }

    [Fact]
    public void BytesPerSecondScalesWithBothAreaAndFrameRate()
    {
        // Pins the arithmetic the threshold was chosen against, so a future change to the formula
        // cannot quietly move every boundary at once.
        Assert.Equal(2560L * 1440 * 4 * 120, RemoteDesktopHandler.FullResCaptureBytesPerSecond(2560, 1440, 120));
        Assert.Equal(
            2 * RemoteDesktopHandler.FullResCaptureBytesPerSecond(2560, 1440, 60),
            RemoteDesktopHandler.FullResCaptureBytesPerSecond(2560, 1440, 120));
    }

    [Fact]
    public void AnUnknownScreenSizeDoesNotClaimToFit()
    {
        // GetScreenSize can report 0 before the first capture. Reporting "fits" there would take the
        // full-res branch on a surface nobody has measured yet.
        Assert.False(RemoteDesktopHandler.FullResCaptureFitsBudget(0, 0, 120));
        Assert.False(RemoteDesktopHandler.FullResCaptureFitsBudget(1920, 0, 120));
    }

    [Fact]
    public void AnOrdinaryDesktopCapturesAtFullResolution()
    {
        // THE WIRING, not just the predicate. A budget the caller never consults is the shape of
        // RemEx-y6x6 — every test green while the behaviour is gone.
        var (w, h, scale) = RemoteDesktopHandler.ChooseH264CaptureSize(
            screenWidth: 2560, screenHeight: 1440, targetWidth: 2560, targetHeight: 1440,
            encodeScale: 1.0, targetFps: 120);

        Assert.Equal((2560, 1440, 1.0), (w, h, scale));
    }

    [Fact]
    public void AnOversizedSurfaceCapturesAtTheEncodedSizeInstead()
    {
        // Dual 4K at 120 fps. The encoded size is already clamped to the 4096px hardware limit, so
        // capture drops to that rather than pushing 66 MB frames the pipe cannot carry.
        var (w, h, scale) = RemoteDesktopHandler.ChooseH264CaptureSize(
            screenWidth: 7680, screenHeight: 2160, targetWidth: 4096, targetHeight: 1152,
            encodeScale: 0.5333, targetFps: 120);

        Assert.Equal(4096, w);
        Assert.Equal(1152, h);
        Assert.Equal(0.5333, scale);
    }

    [Fact]
    public void AnOddDimensionStillDeclinesFullResolutionRegardlessOfBudget()
    {
        // The pre-existing guard, kept honest: rounding an odd width DOWN means capturing at 1.0 is
        // still a resample, which would put the GDI+ path back at the LARGEST possible size — slower
        // than the scaled capture it replaced. Comfortably inside the byte budget, so this can only
        // pass if the odd-dimension condition survived the refactor.
        var (w, h, _) = RemoteDesktopHandler.ChooseH264CaptureSize(
            screenWidth: 1921, screenHeight: 1080, targetWidth: 1920, targetHeight: 1080,
            encodeScale: 1.0, targetFps: 60);

        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Theory]
    [InlineData(2560, 1440, 120)]   // full-res branch
    [InlineData(7680, 2160, 120)]   // budget fallback branch
    [InlineData(1921, 1080, 60)]    // odd-dimension fallback branch
    public void TheReturnedScaleAlwaysReproducesTheReturnedSize(int screenWidth, int screenHeight, int fps)
    {
        // THE INVARIANT THE PIPE DEPENDS ON. The raw buffer must match ffmpeg's -s WxH input exactly;
        // the encoder comments record that a ONE PIXEL mismatch makes nvenc emit zero frames, which
        // presents as a black stream rather than an error. The scale and the size are returned
        // together, so nothing stops a future refactor returning a consistent-looking pair that does
        // not actually reproduce itself — this asserts they do, across every branch.
        var clamped = Math.Min(1.0, Math.Min(4096.0 / screenWidth, 4096.0 / screenHeight));
        int targetWidth = Remex.Core.Services.CaptureScaling.ScaledEven(screenWidth, clamped);
        int targetHeight = Remex.Core.Services.CaptureScaling.ScaledEven(screenHeight, clamped);

        var (w, h, scale) = RemoteDesktopHandler.ChooseH264CaptureSize(
            screenWidth, screenHeight, targetWidth, targetHeight, clamped, fps);

        Assert.Equal(w, Remex.Core.Services.CaptureScaling.ScaledEven(screenWidth, scale));
        Assert.Equal(h, Remex.Core.Services.CaptureScaling.ScaledEven(screenHeight, scale));
    }

    [Fact]
    public void AZeroFrameRateIsTreatedAsOneRatherThanAsUnlimited()
    {
        // fps is clamped to >= 1 upstream, but the budget must not divide-by-zero its way into
        // declaring an arbitrarily large surface free if that ever changes.
        Assert.Equal(
            RemoteDesktopHandler.FullResCaptureBytesPerSecond(1920, 1080, 1),
            RemoteDesktopHandler.FullResCaptureBytesPerSecond(1920, 1080, 0));
    }
}
