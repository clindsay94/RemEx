using Remex.Core.Models;
using Remex.Core.Services;
using SkiaSharp;

namespace Remex.Agent.Services.Screenshot;

/// <summary>
/// Turns raw BGRA capture bytes into a PNG (RemEx-tjve).
/// </summary>
/// <remarks>
/// <para>
/// **PNG, NOT JPEG, AND THE DIFFERENCE IS THE POINT.** The remote-desktop path encodes JPEG because
/// it is streaming and every byte costs latency. A screenshot is the opposite case: it is taken once,
/// kept, and very often taken so somebody can read text off it. JPEG's ringing and chroma subsampling
/// land hardest on exactly that — small high-contrast glyphs — so the format that makes the stream
/// fast makes the screenshot worse at its job.
/// </para>
/// <para>
/// Separated from the capture itself so it can be tested without a screen. Everything here is a pure
/// function of bytes and dimensions.
/// </para>
/// </remarks>
public static class ScreenshotEncoder
{
    /// <summary>Bytes per pixel in the BGRA buffers the capture backends produce.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>The exact buffer size a capture of these dimensions must have.</summary>
    public static long ExpectedByteCount(int width, int height) =>
        (long)width * height * BytesPerPixel;

    /// <summary>
    /// Encodes 32-bit BGRA pixels as a PNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The length check refuses a buffer that cannot possibly be this many pixels. It is necessary
    /// and it is NOT sufficient, which review demonstrated: 1920x1080 and 1080x1920 are the same
    /// number of bytes, so a display ROTATION passes this check and would shear the image. The caller
    /// closes that hole by re-reading the geometry after the capture; a byte count structurally
    /// cannot. What this catches is the mismatched-scale case, where the counts genuinely differ.
    /// </para>
    /// <para>
    /// OPAQUE, NOT UNPREMUL. Desktop capture fills the alpha channel inconsistently — DXGI leaves it
    /// undefined for layered-window regions, and a zero there under Unpremul would encode a fully
    /// transparent PNG that looks blank in a gallery. The screen has no meaningful transparency, so
    /// declaring it opaque is both truthful and the only reading that cannot produce an invisible
    /// image.
    /// </para>
    /// </remarks>
    public static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"A screenshot needs positive dimensions; got {width}x{height}.");
        }

        var expected = ExpectedByteCount(width, height);
        if (bgra.Length != expected)
        {
            throw new ArgumentException(
                $"The captured buffer is {bgra.Length} bytes but {width}x{height} BGRA needs {expected}. "
                    + "The pixels and the dimensions came from separate calls, so this means the capture "
                    + "was scaled or the display changed between them - encoding anyway would save a "
                    + "sheared image that looks real.",
                nameof(bgra));
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

        // COPIES RATHER THAN WRAPS. Wrapping the caller's buffer needs a pinned pointer, and this
        // project does not compile unsafe - turning that on repo-wide to save one copy on a
        // once-per-screenshot path would be a poor trade. FromPixelCopy is the safe equivalent, and
        // it reads rows at info.RowBytes (width * 4), so no stride padding can creep in here.
        using var image = SKImage.FromPixelCopy(info, bgra)
            ?? throw new InvalidOperationException(
                $"Skia refused a {width}x{height} BGRA buffer for the screenshot.");

        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("PNG encoding produced nothing.");
        return data.ToArray();
    }
}

/// <summary>Captures the screen to a PNG on this PC.</summary>
public interface IScreenshotService
{
    /// <summary>Captures the screen and returns the full path of the PNG that was written.</summary>
    Task<string> CaptureAsync(string? displayLabel = null, CancellationToken ct = default);
}

/// <summary>
/// Captures the screen and writes it to a PNG file on this PC (RemEx-tjve).
/// </summary>
/// <remarks>
/// <para>
/// Reuses the already-negotiated capture backend rather than opening a second one: on Windows that is
/// WGC or DXGI duplication, on Linux the PipeWire or X11 path the host already chose. A screenshot
/// taken through a different route than the live stream would be a second thing to keep working.
/// </para>
/// <para>
/// **THIS SAVES THE FILE AND STOPS THERE.** Getting it to the phone is a separate piece of work with
/// its own consent question, because a screenshot carries whatever was on the screen.
/// </para>
/// </remarks>
public sealed class ScreenshotService(
    IScreenCaptureService capture,
    Func<string> resolveFolder,
    Func<DateTimeOffset> clock) : IScreenshotService
{
    /// <summary>How many times a same-second collision will try a new suffix before giving up.</summary>
    /// <remarks>
    /// The name carries a whole second, so a collision means two captures inside one second - a
    /// double-tap, or two monitors. A handful of attempts covers that; a large number would only
    /// mask a clock that had stopped.
    /// </remarks>
    private const int MaxNameAttempts = 8;

    /// <inheritdoc/>
    public async Task<string> CaptureAsync(string? displayLabel = null, CancellationToken ct = default)
    {
        // EVEN DIMENSIONS, VIA THE SHARED RULE. Every raw producer sizes its output buffer with
        // CaptureScaling.ScaledEven, which floors to even EVEN AT SCALE 1.0 - so on a panel reporting
        // an odd dimension (1365x768 is a real size) the buffer is 1364 wide while GetScreenSize says
        // 1365. Using the raw number made every screenshot on such a machine fail permanently, with a
        // message blaming a resolution change that never happened. CaptureScaling's own summary says
        // all capture sites must derive dimensions this way; this one now does.
        var (rawWidth, rawHeight, _, _) = capture.GetScreenSize();
        var width = CaptureScaling.ScaledEven(rawWidth, 1.0);
        var height = CaptureScaling.ScaledEven(rawHeight, 1.0);

        // LIVENESS-AWARE, because a stale frame is not a screenshot. The backends replay their last
        // frame when duplication is lost - a session disconnect, a mode change - and report it
        // through IsLive (RemEx-ltd). The stream tolerates that because the next frame corrects it;
        // a saved file does not get corrected, so an arbitrarily old desktop would be kept forever
        // under a timestamp that says "now".
        var result = await capture.CaptureRawScreenLiveAsync(scale: 1.0, drawCursor: false, ct);

        // NO CURSOR, deliberately. A screenshot records what was on screen; the pointer is where the
        // person taking it happened to be, and it lands on top of whatever they were capturing. The
        // live stream draws it because it is a control surface - this is not.
        if (!result.IsLive)
        {
            throw new InvalidOperationException(
                "The capture backend replayed a stale frame, so this would be a picture of an earlier "
                    + "moment rather than the screen now.");
        }

        if (result.Pixels.IsEmpty)
        {
            throw new InvalidOperationException(
                "The capture backend returned no pixels, so there is no screenshot to save.");
        }

        // RE-READ THE GEOMETRY, because the byte count cannot catch a rotation. 1920x1080 and
        // 1080x1920 are the same number of bytes: rotating a monitor raises ACCESS_LOST, the backend
        // re-initialises to the new mode and hands back the CACHED frame at the old one, and the
        // length check passes on a buffer whose rows are the wrong length. That is precisely the
        // sheared-image-that-looks-real this code exists to refuse, and only comparing the dimensions
        // before and after finds it.
        var after = capture.GetScreenSize();
        if (after.Width != rawWidth || after.Height != rawHeight)
        {
            throw new InvalidOperationException(
                $"The display changed from {rawWidth}x{rawHeight} to {after.Width}x{after.Height} during "
                    + "the capture, so the pixels and their shape no longer agree.");
        }

        var png = ScreenshotEncoder.EncodePng(result.Pixels.Span, width, height);

        var folder = resolveFolder();
        if (!Path.IsPathFullyQualified(folder))
        {
            // A RELATIVE PATH HERE WOULD RESOLVE AGAINST THE PROCESS WORKING DIRECTORY, and this
            // process is elevated with a working directory it did not choose - so the write would
            // succeed somewhere like System32 precisely BECAUSE of the elevation, and the user would
            // never find the file.
            throw new InvalidOperationException(
                $"'{folder}' is not a full path, so a screenshot would be written somewhere the user "
                    + "cannot find it.");
        }

        Directory.CreateDirectory(folder);
        return await WriteWithoutOverwritingAsync(folder, displayLabel, png, ct);
    }

    /// <summary>
    /// Writes the PNG under a name that is not already taken, and never truncates an existing file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **CreateNew, NOT WriteAllBytes.** Review caught the first version assuming a collision would be
    /// renamed to "(2)" — that is Explorer's behaviour, not the file system's. WriteAllBytes truncates
    /// and overwrites, and the name carries only whole seconds, so two captures in the same second
    /// silently destroyed the first one and reported success.
    /// </para>
    /// <para>
    /// WRITTEN TO A TEMPORARY NAME AND MOVED, so a cancelled or failed write cannot leave a truncated
    /// PNG sitting where a complete one should be. A half-written file that still opens is worse than
    /// none, because nothing about it looks wrong.
    /// </para>
    /// </remarks>
    private async Task<string> WriteWithoutOverwritingAsync(
        string folder, string? displayLabel, byte[] png, CancellationToken ct)
    {
        var takenAt = clock();

        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            // The first attempt uses the shared name exactly; later ones disambiguate through the
            // label, which keeps every name going through the same sanitising and length rules.
            var label = attempt == 0 ? displayLabel : $"{displayLabel}{attempt + 1}";
            var path = Path.Combine(folder, ScreenshotFileName.ForTimestamp(takenAt, label));
            var temporary = path + ".part";

            try
            {
                using (var stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Claimed the name; the real bytes go to the temporary file and replace it.
                }

                await File.WriteAllBytesAsync(temporary, png, ct);
                File.Move(temporary, path, overwrite: true);
                return path;
            }
            catch (IOException) when (File.Exists(path) && attempt < MaxNameAttempts - 1)
            {
                TryDelete(temporary);
            }
            catch
            {
                TryDelete(temporary);
                TryDelete(path);
                throw;
            }
        }

        throw new IOException(
            $"Could not find a free screenshot name in '{folder}' after {MaxNameAttempts} attempts.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
