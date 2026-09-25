using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// P1-13, end to end through the real handler: with a backend that promises buffer identity
/// (<see cref="IScreenCaptureService.UnchangedFramesShareBuffer"/>), a static screen stops re-sending
/// the same MJPEG frame every tick, a changing screen still streams, and an unchanged frame is still
/// forced out on the refresh cadence.
/// </summary>
public sealed class MjpegUnchangedFrameSkipTests
{
    private static byte[] MakeJpeg(byte fill) => [0xFF, 0xD8, 0xFF, 0xE0, fill, fill, fill, fill, 0xFF, 0xD9];

    /// <summary>
    /// Static mode hands back one array forever, exactly as the DXGI/WGC tiers do on an unchanged
    /// desktop. Changing mode hands back a fresh array per capture.
    /// </summary>
    private sealed class IdentityCaptureService(bool changing) : IScreenCaptureService
    {
        private readonly byte[] _static = MakeJpeg(0x3C);
        private int _captures;

        public int CaptureCount => Volatile.Read(ref _captures);

        public bool UnchangedFramesShareBuffer => true;

        public Task<ReadOnlyMemory<byte>> CaptureScreenAsync(
            int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _captures);
            return Task.FromResult<ReadOnlyMemory<byte>>(changing ? MakeJpeg((byte)n) : _static);
        }

        public Task<byte[]?> CaptureRawScreenAsync(
            double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);

        public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);
    }

    /// <summary>
    /// Starts non-live (as if the capture backend were mid-recovery) and switches to live on the SAME
    /// buffer once told to — exercising the force-on-non-live-to-live-transition path the resend gate
    /// must take regardless of the 2 s refresh cadence.
    /// </summary>
    private sealed class RecoveringCaptureService : IScreenCaptureService
    {
        private readonly byte[] _frame = MakeJpeg(0x7A);
        private volatile bool _live;

        public bool UnchangedFramesShareBuffer => true;

        public void GoLive() => _live = true;

        public Task<ReadOnlyMemory<byte>> CaptureScreenAsync(
            int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult<ReadOnlyMemory<byte>>(_frame);

        public Task<ScreenCaptureResult> CaptureScreenLiveAsync(
            int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(new ScreenCaptureResult(_frame, _live));

        public Task<byte[]?> CaptureRawScreenAsync(
            double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);

        public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);
    }

    private static RemexHostFactory FactoryWith(IScreenCaptureService capture) =>
        new RemexHostFactory().WithServices(services =>
        {
            services.AddSingleton(capture);
            services.Configure<Microsoft.Extensions.Hosting.HostOptions>(opts =>
            {
                opts.BackgroundServiceExceptionBehavior =
                    Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore;
            });
        });

    private static async Task<WebSocket> StartAsync(RemexHostFactory factory, CancellationToken ct)
    {
        var ws = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/desktop"), ct);
        await MessageSerializer.SendAsync(
            ws,
            new RemexMessage
            {
                Type = MessageTypes.DesktopStart,
                DesktopConfig = new DesktopConfig { Quality = 50, Scale = 1.0, TargetFps = 20 },
            },
            ct);
        return ws;
    }

    /// <summary>
    /// Reads until the next binary (pixel) frame; text messages (meta, cursor) are skipped. Returns
    /// false when <paramref name="ct"/> expires first.
    /// </summary>
    private static async Task<bool> NextBinaryFrameAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return false;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            // A cancelled receive can abort the test socket rather than throw OCE.
            return false;
        }
    }

    [Fact]
    public async Task AStaticScreenIsSentOnceNotEveryTick()
    {
        var capture = new IdentityCaptureService(changing: false);
        await using var factory = FactoryWith(capture);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ws = await StartAsync(factory, cts.Token);

        Assert.True(await NextBinaryFrameAsync(ws, cts.Token), "The first frame of a stream must always be sent.");
        var capturesAtFirstFrame = capture.CaptureCount;

        // Well inside the forced-refresh interval: capture keeps ticking, nothing new should ship.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        window.CancelAfter(TimeSpan.FromMilliseconds(1000));
        var gotAnother = await NextBinaryFrameAsync(ws, window.Token);

        Assert.True(
            capture.CaptureCount - capturesAtFirstFrame >= 5,
            $"The capture loop only ran {capture.CaptureCount - capturesAtFirstFrame} time(s) in the window, " +
            "so the absence of frames proves nothing.");
        Assert.False(gotAnother, "An unchanged frame was re-sent inside the forced-refresh interval.");
    }

    [Fact]
    public async Task AChangingScreenStillStreams()
    {
        var capture = new IdentityCaptureService(changing: true);
        await using var factory = FactoryWith(capture);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ws = await StartAsync(factory, cts.Token);

        for (var i = 0; i < 4; i++)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            window.CancelAfter(TimeSpan.FromMilliseconds(1500));
            Assert.True(await NextBinaryFrameAsync(ws, window.Token), $"Changed frame {i} was not sent.");
        }
    }

    [Fact]
    public async Task AnUnchangedFrameIsStillForcedOutOnTheRefreshCadence()
    {
        var capture = new IdentityCaptureService(changing: false);
        await using var factory = FactoryWith(capture);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ws = await StartAsync(factory, cts.Token);

        Assert.True(await NextBinaryFrameAsync(ws, cts.Token));
        var clock = Stopwatch.StartNew();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        window.CancelAfter(TimeSpan.FromSeconds(8));
        Assert.True(await NextBinaryFrameAsync(ws, window.Token), "No forced refresh arrived for an unchanged screen.");

        // Lower bound with slack for scheduling: the refresh is 2 s after the PUBLISH, which precedes
        // this client's receive of the first frame.
        Assert.True(
            clock.ElapsedMilliseconds >= 1500,
            $"The refresh arrived after {clock.ElapsedMilliseconds} ms; an unchanged frame is being re-sent early.");
    }

    /// <summary>
    /// The resend gate is per-connection (a local in the stream method), not shared across clients — a
    /// second viewer must not have its own screen change masked just because a FIRST viewer's gate
    /// already saw and forwarded it. Reviewer-requested regression test for P1-13 round 1.
    /// </summary>
    [Fact]
    public async Task ASecondClientStillReceivesAChangeTheFirstClientAlreadySaw()
    {
        var capture = new IdentityCaptureService(changing: true);
        await using var factory = FactoryWith(capture);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var wsA = await StartAsync(factory, cts.Token);
        Assert.True(await NextBinaryFrameAsync(wsA, cts.Token), "Client A's first frame was not sent.");
        // Let client A observe a couple more changes on its own before client B joins, so client B's
        // gate genuinely starts from a stale baseline rather than an empty one.
        Assert.True(await NextBinaryFrameAsync(wsA, cts.Token));

        using var wsB = await StartAsync(factory, cts.Token);
        Assert.True(await NextBinaryFrameAsync(wsB, cts.Token), "Client B's first frame was not sent.");
        Assert.True(
            await NextBinaryFrameAsync(wsB, cts.Token),
            "Client B never received a screen change that client A's gate had already forwarded.");
    }

    /// <summary>
    /// A frame that transitions from a stale replay (IsLive: false) to a live frame on the SAME buffer
    /// must go out immediately — it is a real recovery event, not an "unchanged" frame to hold for the
    /// 2 s refresh. Reviewer-requested regression test for P1-13 round 1.
    /// </summary>
    [Fact]
    public async Task ARecoveryFrameOnTheSameBufferIsSentImmediatelyNotOnTheRefreshCadence()
    {
        var capture = new RecoveringCaptureService();
        await using var factory = FactoryWith(capture);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ws = await StartAsync(factory, cts.Token);

        // Non-live: RemEx-ltd says a stale replay must never count as a send-worthy frame.
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        quiet.CancelAfter(TimeSpan.FromMilliseconds(500));
        Assert.False(
            await NextBinaryFrameAsync(ws, quiet.Token),
            "A stale (non-live) replay must not be sent as a frame.");

        var clock = Stopwatch.StartNew();
        capture.GoLive();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        window.CancelAfter(TimeSpan.FromSeconds(2));
        Assert.True(
            await NextBinaryFrameAsync(ws, window.Token),
            "The live transition on the same buffer was not sent.");
        Assert.True(
            clock.ElapsedMilliseconds < 1500,
            $"The recovery frame took {clock.ElapsedMilliseconds} ms — it waited for the 2 s refresh " +
            "instead of being forced out on the non-live-to-live transition.");
    }
}
