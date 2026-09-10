using Remex.Agent.Services.Screenshot;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the screenshot capture path: the encoding, its guard, and what lands on disk (RemEx-tjve).
/// </summary>
public sealed class ScreenshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remex-shot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A capture backend that reports a size and hands back a buffer, with no screen involved.</summary>
    /// <remarks>
    /// <paramref name="sizeAfter"/> lets a test move the display DURING the capture, which is the
    /// rotation case: the backend re-initialises to the new mode and replays the cached frame at the
    /// old one. Byte counts cannot see it - 1920x1080 and 1080x1920 are the same number of bytes.
    /// </remarks>
    private sealed class FakeCapture(
        int width, int height, byte[]? pixels,
        bool isLive = true, (int W, int H)? sizeAfter = null) : IScreenCaptureService
    {
        public int SizeCalls { get; private set; }
        public double? LastScale { get; private set; }
        public bool? LastDrawCursor { get; private set; }

        public (int Width, int Height, int Left, int Top) GetScreenSize()
        {
            SizeCalls++;
            return SizeCalls > 1 && sizeAfter is { } later
                ? (later.W, later.H, 0, 0)
                : (width, height, 0, 0);
        }

        public Task<ReadOnlyMemory<byte>> CaptureScreenAsync(
            int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default) =>
            throw new InvalidOperationException("a screenshot must not go through the JPEG path");

        public Task<byte[]?> CaptureRawScreenAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "a screenshot must use the liveness-aware overload, or a stale replayed frame is saved");

        public Task<ScreenCaptureResult> CaptureRawScreenLiveAsync(
            double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        {
            LastScale = scale;
            LastDrawCursor = drawCursor;
            return Task.FromResult(new ScreenCaptureResult(pixels ?? ReadOnlyMemory<byte>.Empty, isLive));
        }
    }

    /// <summary>A different colour in every pixel, so a flip or a transpose cannot pass unnoticed.</summary>
    private static byte[] DistinctBgra(int width, int height)
    {
        var buffer = new byte[ScreenshotEncoder.ExpectedByteCount(width, height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                buffer[i] = (byte)(10 + x);        // B varies across
                buffer[i + 1] = (byte)(80 + y);    // G varies down
                buffer[i + 2] = (byte)(200 - x);   // R varies the other way across
                buffer[i + 3] = 0xFF;
            }
        }

        return buffer;
    }

    private static byte[] Bgra(int width, int height)
    {
        var buffer = new byte[ScreenshotEncoder.ExpectedByteCount(width, height)];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 0x20;      // B
            buffer[i + 1] = 0x40;  // G
            buffer[i + 2] = 0x80;  // R
            buffer[i + 3] = 0xFF;  // A
        }

        return buffer;
    }

    private ScreenshotService Service(IScreenCaptureService capture, DateTimeOffset? at = null) =>
        new(capture, () => _root, () => at ?? new DateTimeOffset(2026, 8, 2, 14, 5, 9, TimeSpan.Zero));

    // ── The encoder ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEncoderProducesSomethingThatIsActuallyAPng()
    {
        // The 8-byte PNG signature. Asserting "it returned bytes" would pass on a JPEG, and the
        // format is the one decision this whole type exists to make.
        var png = ScreenshotEncoder.EncodePng(Bgra(4, 3), 4, 3);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png.Take(8));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1920, 1080)]
    public void TheEncoderRoundTripsTheDimensionsItWasGiven(int width, int height)
    {
        // A PNG that decodes at the wrong size is the shape a mishandled stride produces, and it
        // would look plausible until someone compared it with the screen.
        var png = ScreenshotEncoder.EncodePng(Bgra(width, height), width, height);

        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
    }

    [Fact]
    public void TheEncoderPreservesGEOMETRYAsWellAsColour()
    {
        // REVIEW CAUGHT THE FIRST VERSION USING A UNIFORM BUFFER and reading one pixel: a vertical
        // flip, a 90-degree transpose or an off-by-one-row shear all passed it unchanged - and shear
        // is the exact failure this whole type exists to refuse. A 3x2 with a different colour in
        // every pixel catches all of them, and the non-square shape catches transposition too.
        var png = ScreenshotEncoder.EncodePng(DistinctBgra(3, 2), 3, 2);

        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);

        foreach (var (x, y) in new[] { (0, 0), (2, 0), (0, 1), (2, 1) })
        {
            var pixel = decoded.GetPixel(x, y);
            Assert.Equal((byte)(200 - x), pixel.Red);
            Assert.Equal((byte)(80 + y), pixel.Green);
            Assert.Equal((byte)(10 + x), pixel.Blue);
        }
    }

    [Fact]
    public void TheEncoderKeepsTheCHANNELOrder()
    {
        // BGRA IS NOT RGBA, and getting the channel order wrong produces a perfectly valid PNG with
        // the red and blue swapped - a picture that is the right size, the right shape, and the
        // wrong colour. Nothing about the file would look broken.
        var png = ScreenshotEncoder.EncodePng(Bgra(2, 2), 2, 2);

        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        var pixel = decoded.GetPixel(0, 0);

        Assert.Equal(0x80, pixel.Red);
        Assert.Equal(0x40, pixel.Green);
        Assert.Equal(0x20, pixel.Blue);
        Assert.Equal(0xFF, pixel.Alpha);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ABufferThatDoesNotMATCHTheDimensionsIsRefused(int delta)
    {
        // THE GUARD THIS TYPE EXISTS FOR. The pixels come from CaptureRawScreenAsync and the size
        // from GetScreenSize - two separate calls with nothing tying them together. A scaled capture
        // or a resolution change between them leaves them disagreeing, and Skia will read whatever
        // is there and emit a diagonally sheared image rather than fail. A wrong screenshot that
        // looks real is worse than no screenshot.
        var pixels = new byte[ScreenshotEncoder.ExpectedByteCount(8, 8) + delta];

        var ex = Assert.Throws<ArgumentException>(() => ScreenshotEncoder.EncodePng(pixels, 8, 8));
        Assert.Contains("256", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-4, 4)]
    public void NonPositiveDimensionsAreRefused(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenshotEncoder.EncodePng([], width, height));
    }

    // ── The capture path ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACaptureWritesAPngNamedByTheSharedRule()
    {
        var at = new DateTimeOffset(2026, 8, 2, 14, 5, 9, TimeSpan.Zero);
        var service = Service(new FakeCapture(6, 4, Bgra(6, 4)), at);

        var path = await service.CaptureAsync(ct: CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_root, Path.GetDirectoryName(path));

        // The name comes from the shared helper rather than being composed here, so the colon rule,
        // the zero padding and the invariant culture all keep applying.
        Assert.Equal(Remex.Core.Models.ScreenshotFileName.ForTimestamp(at), Path.GetFileName(path));

        using var decoded = SkiaSharp.SKBitmap.Decode(File.ReadAllBytes(path));
        Assert.Equal(6, decoded.Width);
        Assert.Equal(4, decoded.Height);
    }

    [Fact]
    public async Task TheCursorIsNOTDrawn()
    {
        // A screenshot records what was on screen. The pointer is where the person taking it happened
        // to be, and it lands on top of whatever they were trying to capture. The live stream draws
        // it because it is a control surface; this is not.
        var capture = new FakeCapture(4, 4, Bgra(4, 4));
        await Service(capture).CaptureAsync();

        Assert.False(capture.LastDrawCursor);
    }

    [Fact]
    public async Task TheCaptureIsUNSCALED()
    {
        // Any scale other than 1.0 makes the buffer disagree with GetScreenSize, which the encoder
        // then refuses - so this is not merely a quality preference, it is what keeps the two calls
        // consistent. It is also what a screenshot should be: the real pixels.
        var capture = new FakeCapture(4, 4, Bgra(4, 4));
        await Service(capture).CaptureAsync();

        Assert.Equal(1.0, capture.LastScale);
    }

    [Fact]
    public async Task NoPixelsMeansNoFile_AndAClearFailure()
    {
        // The backend genuinely can return null - a lost DXGI duplication, a display asleep. Writing
        // a zero-byte or half-written PNG would be worse than saying nothing happened.
        var service = Service(new FakeCapture(4, 4, pixels: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());

        Assert.Contains("no pixels", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public async Task ADisplayThatCHANGEDSizeMidCaptureIsRefusedRatherThanSheared()
    {
        // The race the encoder's length check exists for, driven end to end: the backend reports one
        // size and hands back a buffer for another. Nothing else in the system would notice.
        var service = Service(new FakeCapture(1920, 1080, Bgra(1280, 720)));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CaptureAsync());
    }

    [Fact]
    public async Task TheFolderIsCreatedIfItIsNotThere()
    {
        Assert.False(Directory.Exists(_root));

        var path = await Service(new FakeCapture(2, 2, Bgra(2, 2))).CaptureAsync();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task TwoScreensInTheSameSecondKeepTheirOwnNames()
    {
        // Why the display label exists at all: without it both captures resolve to one name, the
        // second is silently renamed to "(2)", and which screen each came from is lost.
        var at = new DateTimeOffset(2026, 8, 2, 14, 5, 9, TimeSpan.Zero);
        var service = Service(new FakeCapture(2, 2, Bgra(2, 2)), at);

        var first = await service.CaptureAsync("DISPLAY1");
        var second = await service.CaptureAsync("DISPLAY2");

        Assert.NotEqual(Path.GetFileName(first), Path.GetFileName(second));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    // ── The four things review found, each pinned ────────────────────────────────────────────

    [Theory]
    [InlineData(1365, 768)]
    [InlineData(1920, 1081)]
    [InlineData(1367, 769)]
    public async Task AnODDSizedDisplayCanStillTakeAScreenshot(int rawWidth, int rawHeight)
    {
        // THE BUG THAT WOULD HAVE BROKEN THESE MACHINES COMPLETELY. Every raw producer sizes its
        // buffer with CaptureScaling.ScaledEven, which floors to EVEN even at scale 1.0 - so a panel
        // reporting 1365x768 delivers 1364x768 pixels. Using the raw number made the length check
        // throw on every single capture, forever, blaming a resolution change that never happened.
        var even = (Width: CaptureScaling.ScaledEven(rawWidth, 1.0), Height: CaptureScaling.ScaledEven(rawHeight, 1.0));
        var service = Service(new FakeCapture(rawWidth, rawHeight, Bgra(even.Width, even.Height)));

        var path = await service.CaptureAsync();

        using var decoded = SkiaSharp.SKBitmap.Decode(File.ReadAllBytes(path));
        Assert.Equal(even.Width, decoded.Width);
        Assert.Equal(even.Height, decoded.Height);
    }

    [Fact]
    public async Task ASTALEReplayedFrameIsRefused()
    {
        // The backends replay their last frame when duplication is lost, and report it through
        // IsLive (RemEx-ltd). The stream tolerates that because the next frame corrects it; a saved
        // file is never corrected, so an arbitrarily old desktop would be kept forever under a
        // timestamp claiming it is now.
        var service = Service(new FakeCapture(4, 4, Bgra(4, 4), isLive: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public async Task AROTATIONIsCaughtEvenThoughTheByteCountMatches()
    {
        // THE HOLE A LENGTH CHECK CANNOT CLOSE, and the reason the geometry is re-read. 1920x1080 and
        // 1080x1920 are both 8,294,400 bytes: rotating a monitor raises ACCESS_LOST, the backend
        // re-initialises to the new mode and hands back the CACHED frame at the old one, and the
        // length check passes on a buffer whose rows are the wrong length. That is the sheared image
        // that looks real.
        var service = Service(new FakeCapture(1920, 1080, Bgra(1920, 1080), sizeAfter: (1080, 1920)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());

        Assert.Contains("changed from", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing half-written was left behind.
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public async Task TwoCapturesInTheSAMESecondBothSurvive()
    {
        // REVIEW CAUGHT THE COMMENT CLAIMING A COLLISION WOULD BE RENAMED TO "(2)". That is Explorer's
        // behaviour, not the file system's - WriteAllBytes truncates and overwrites. The name carries
        // whole seconds, so a double-tap silently destroyed the first screenshot and reported success.
        var at = new DateTimeOffset(2026, 8, 2, 14, 5, 9, TimeSpan.Zero);
        var service = Service(new FakeCapture(2, 2, Bgra(2, 2)), at);

        var first = await service.CaptureAsync();
        var second = await service.CaptureAsync();

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, Directory.GetFiles(_root, "*.png").Length);
    }

    [Fact]
    public async Task ARelativeFolderIsRefusedRatherThanWrittenToTheWorkingDirectory()
    {
        // GetFolderPath returns "" when Pictures cannot be resolved, and Path.Combine("", x) is
        // RELATIVE - which an elevated process resolves against a working directory it did not
        // choose. The write would succeed somewhere like System32 BECAUSE of the elevation, and the
        // user would never find the file.
        var service = new ScreenshotService(
            new FakeCapture(2, 2, Bgra(2, 2)),
            () => "RemEx Screenshots",
            () => DateTimeOffset.UnixEpoch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());

        Assert.Contains("not a full path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoPartialFileSurvivesAFailedWrite()
    {
        // The bytes go to a temporary name and are moved into place, so a cancelled or failed write
        // cannot leave a truncated PNG where a complete one should be. A half-written file that still
        // opens is worse than none, because nothing about it looks wrong.
        var service = Service(new FakeCapture(2, 2, Bgra(2, 2)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAsync(ct: cts.Token));

        Assert.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root) : []);
    }
}
