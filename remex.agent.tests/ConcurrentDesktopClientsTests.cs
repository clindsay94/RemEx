using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Two concurrent /ws/desktop clients served by ONE capture service instance that hands both of them
/// the SAME array (RemEx-c5an — acceptance item 3 of RemEx-13se, split out rather than claimed).
///
/// WHY THIS SCENARIO. <c>IScreenCaptureService</c> is registered <c>AddSingleton</c> on both platform
/// branches (HostBootstrapper.cs:118 Windows, :130 Linux) and resolved per /ws/desktop request
/// (:484), so two connected phones run two independent stream loops against one capture instance.
/// The backends then compound it: DxgiDesktopCapture assigns <c>_lastRawFrame</c> once
/// (DxgiDesktopCapture.cs:659) and returns that same reference on later ticks (:551/:577/:583/:599/
/// :628/:637/:653/:662), so on a static desktop BOTH clients can be handed the identical array.
///
/// This is the finding that ruled out "Design B" in RemEx-lcp8's second investigation and constrains
/// the rest, yet nothing in the suite exercised it — so the reasoning about it was unverified. These
/// tests make it concrete: today the sharing is harmless because it is READ-ONLY, and that is the
/// property a pooling change would destroy.
///
/// WHAT THIS DOES NOT DO — read this before citing the file as evidence for anything.
///
///   • It does not prove production registers the service as a singleton. A test that registers its
///     own double cannot pin the bootstrapper's lifetime without duplicating it, so that fact is
///     cited from HostBootstrapper above rather than asserted.
///   • Whether it mirrors production depends on WHICH MJPEG TIER is active, and the answer is not
///     uniform. These sessions run MJPEG (the default codec, which is what lets this harness avoid
///     ffmpeg entirely, as the bead requires), and WindowsScreenCaptureService.CaptureScreenCore has
///     three tiers. WGC and the GDI fallback JPEG-encode into a FRESH MemoryStream per call, so they
///     do not alias. The DXGI tier DOES: DxgiDesktopCapture.TryCapture caches the encoded frame in
///     _lastFrame (assigned once, at :829) and returns that identical memory on eight other paths,
///     including the static-desktop replay at :764 and the AccumulatedFrames==0 replay at :786-789,
///     both with isLive=true. On a static desktop that is the common case, so two concurrent clients
///     really are handed the same array today — which is exactly what the double below does.
///     (Do not read ScreenCaptureResult's "every producer allocates a fresh stream per capture" as
///     contradicting this. It is about stream ALLOCATION; the replay path allocates nothing and
///     hands back the cached memory.)
///   • The raw BGRA to H.264 path, which aliases _lastRawFrame the same way, is NOT exercised here:
///     the double stubs raw capture to null and the handler only calls it when the active codec is
///     H.264. Covering that means putting the encoder in the loop, which needs ffmpeg or an
///     injection seam the handler does not have (it constructs its encoder directly).
///
/// So what IS earned: when one capture instance serves two concurrent clients and hands both the
/// same buffer, the host itself neither mutates that buffer nor lets one client's stream corrupt the
/// other's. That is the property a pooling change has to preserve, and the negative control below
/// proves these assertions notice when it is violated.
/// </summary>
public sealed class ConcurrentDesktopClientsTests
{
    private const int FramesPerClient = 4;

    /// <summary>
    /// Builds a small JPEG-shaped payload whose middle is a single repeated value, so a frame that
    /// picked up another client's content — or a recycled buffer's — is identifiable by value.
    /// </summary>
    private static byte[] MakeJpeg(byte fill)
    {
        var jpeg = new byte[64];
        jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF; jpeg[3] = 0xE0;
        for (var i = 4; i < jpeg.Length - 2; i++) jpeg[i] = fill;
        jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
        return jpeg;
    }

    /// <summary>
    /// Hands EVERY caller the SAME array instance and never mutates it.
    ///
    /// This models the DXGI MJPEG tier faithfully — it caches its encoded frame and returns the same
    /// memory on every static-desktop tick, so both clients get one array. It is NOT how the WGC or
    /// GDI tiers behave (fresh stream per call), and it is also the shape a pooled backend would
    /// present on any tier. See the class header for the tier split.
    /// </summary>
    private sealed class SharedBufferCaptureService : IScreenCaptureService
    {
        private readonly byte[] _shared;
        private int _captures;

        public SharedBufferCaptureService(byte[] shared) => _shared = shared;

        public int CaptureCount => Volatile.Read(ref _captures);

        public string? BackendName => "shared-buffer-fake";

        public Task<ReadOnlyMemory<byte>> CaptureScreenAsync(
            int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _captures);
            // Deliberately the same reference every time, to both clients.
            return Task.FromResult<ReadOnlyMemory<byte>>(_shared);
        }

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

    /// <summary>
    /// One client's frames plus when its first and last arrived, measured on a clock shared with the
    /// other client so the two runs can be shown to have OVERLAPPED. Without those timestamps
    /// nothing fails if a future change quietly serialises the sessions, and "concurrent" becomes a
    /// claim in a test name rather than a property under test.
    /// </summary>
    /// <param name="FramesAfterJoin">
    /// How many frames arrived AFTER <c>joinSignal</c> completed. Zero when no signal was supplied.
    /// This is the ordering fact the staggered test needs, and it replaces comparing wall-clock
    /// stamps taken on the same loaded machine (RemEx-4vdm).
    /// </param>
    private readonly record struct ClientRun(
        byte[][] Frames, long FirstFrameMs, long LastFrameMs, int FramesAfterJoin);

    /// <param name="announceFirstFrame">
    /// Completed when this client's first binary frame lands, so another client can be held open
    /// until this one has genuinely joined.
    /// </param>
    /// <param name="joinSignal">
    /// When supplied, this client keeps reading past <paramref name="frameCount"/> until the signal
    /// has completed AND at least one further frame has arrived — which is what makes "the late
    /// client joined an IN-PROGRESS stream" a guarantee of the arrangement rather than something the
    /// assertion hopes to observe (RemEx-4vdm).
    /// </param>
    private static async Task<ClientRun> RunClientAsync(
        RemexHostFactory factory, int frameCount, Stopwatch clock, CancellationToken ct,
        TaskCompletionSource? announceFirstFrame = null, Task? joinSignal = null)
    {
        var ws = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/desktop"), ct);

        long firstFrameMs = -1;
        long lastFrameMs = -1;
        var framesAfterJoin = 0;

        try
        {
            await MessageSerializer.SendAsync(
                ws,
                new RemexMessage
                {
                    Type = MessageTypes.DesktopStart,
                    DesktopConfig = new DesktopConfig { Quality = 50, Scale = 1.0, TargetFps = 10 },
                },
                ct);

            var frames = new List<byte[]>();
            var buffer = new byte[64 * 1024];

            // The second clause is the whole fix: with a joinSignal this client refuses to stop
            // until the other one has actually joined and it has streamed on past that point. The
            // 30-second token is the bound.
            while (frames.Count < frameCount || (joinSignal is not null && framesAfterJoin < 1))
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return new ClientRun(frames.ToArray(), firstFrameMs, lastFrameMs, framesAfterJoin);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                // Text messages here are desktop_meta, the stream descriptor and the cursor stream;
                // only the binary frames carry pixels, which is what this test is about.
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    frames.Add(ms.ToArray());
                    lastFrameMs = clock.ElapsedMilliseconds;
                    if (firstFrameMs < 0)
                    {
                        firstFrameMs = lastFrameMs;
                        announceFirstFrame?.TrySetResult();
                    }

                    if (joinSignal is { IsCompleted: true }) framesAfterJoin++;
                }
            }

            await MessageSerializer.SendAsync(ws, new RemexMessage { Type = MessageTypes.DesktopStop }, ct);
            return new ClientRun(frames.ToArray(), firstFrameMs, lastFrameMs, framesAfterJoin);
        }
        finally
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // Best effort: a teardown failure here would mask the assertion the test makes.
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch { /* see above */ }
            }
            ws.Dispose();
        }
    }

    /// <summary>
    /// Finds <paramref name="needle"/> inside <paramref name="haystack"/>. The binary frame is an
    /// envelope (header + payload), and the header's exact size has changed before, so this asserts
    /// the payload is PRESENT AND CONTIGUOUS rather than pinning an offset that is not this test's
    /// business.
    /// </summary>
    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            var match = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (haystack[start + i] != needle[i]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [Fact]
    public async Task TwoConcurrentClientsBothReceiveIntactFramesFromOneSharedCaptureBuffer()
    {
        var payload = MakeJpeg(0x3C);
        var capture = new SharedBufferCaptureService(payload);
        await using var factory = FactoryWith(capture);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();

        // Both clients streaming at once against the one capture instance — the whole point. That
        // they CAN coexist rests on DesktopSessionRegistry giving an empty clientId a synthetic
        // per-connection key; were it to key on the empty string, the second connection would evict
        // the first and this test would fail rather than silently degrade.
        var both = await Task.WhenAll(
            RunClientAsync(factory, FramesPerClient, clock, cts.Token),
            RunClientAsync(factory, FramesPerClient, clock, cts.Token));

        for (var client = 0; client < both.Length; client++)
        {
            Assert.True(
                both[client].Frames.Length >= FramesPerClient,
                $"Client {client} received only {both[client].Frames.Length} binary frame(s); " +
                $"expected at least {FramesPerClient}.");

            for (var frame = 0; frame < both[client].Frames.Length; frame++)
            {
                Assert.True(
                    Contains(both[client].Frames[frame], payload),
                    $"Client {client} frame {frame} did not carry the captured payload intact. The " +
                    "capture service hands both clients the SAME array, so this is what goes wrong " +
                    "the moment that array is recycled rather than merely aliased — see RemEx-lcp8.");
            }
        }

        // The clients must have been streaming AT THE SAME TIME, not one after the other. Every
        // assertion above is satisfied by two sequential sessions too, so without this the word
        // "concurrent" in the test name would be decoration.
        Assert.True(
            both[0].FirstFrameMs <= both[1].LastFrameMs && both[1].FirstFrameMs <= both[0].LastFrameMs,
            $"The two clients did not overlap: client 0 streamed {both[0].FirstFrameMs}-" +
            $"{both[0].LastFrameMs}ms, client 1 streamed {both[1].FirstFrameMs}-" +
            $"{both[1].LastFrameMs}ms. They were serialised, so nothing about concurrent access to " +
            "the shared capture instance was actually exercised.");

        Assert.True(
            capture.CaptureCount >= FramesPerClient * 2,
            $"Expected the single capture instance to serve both clients, but it was called only " +
            $"{capture.CaptureCount} time(s).");
    }

    [Fact]
    public async Task AClientStartingWhileAnotherIsStreamingDoesNotDisturbTheFirst()
    {
        // The staggered case: a second client issues desktop_start against the shared instance while
        // the first is already mid-stream. The simultaneous-start test above cannot distinguish
        // "both started cleanly" from "joining is safe at any point", and a pooled backend's window
        // for handing one client a buffer the other is still sending is widest exactly here.
        //
        // Note the double holds no capture-target state, so this does not exercise re-targeting on
        // start; it exercises a late join against a shared buffer.
        var payload = MakeJpeg(0x77);
        var capture = new SharedBufferCaptureService(payload);
        await using var factory = FactoryWith(capture);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();

        // THE OVERLAP IS ARRANGED, NOT MEASURED (RemEx-4vdm). This used to run both clients to a
        // frame quota and then compare wall-clock stamps to see whether they happened to overlap —
        // so on a loaded machine the first client simply FINISHED before the second connected, and
        // the test failed for a scenario that never set itself up. That teaches people to re-run
        // rather than to read, which is how a real failure eventually gets waved through.
        var secondJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = RunClientAsync(
            factory, FramesPerClient * 2, clock, cts.Token, joinSignal: secondJoined.Task);

        // Let the first client get going before the second one starts. `first` is already running on
        // the thread pool, so awaiting the second client below does not stall it.
        while (capture.CaptureCount < 2 && !cts.IsCancellationRequested) await Task.Delay(20, cts.Token);

        var second = await RunClientAsync(
            factory, FramesPerClient, clock, cts.Token, announceFirstFrame: secondJoined);
        var firstRun = await first;

        Assert.True(firstRun.Frames.Length >= FramesPerClient * 2, "The first client stopped early.");
        Assert.True(second.Frames.Length >= FramesPerClient, "The late-joining client received too few frames.");

        // ORDERING, NOT CLOCK TIME — AND MEASURED, NOT ASSUMED, INCLUDING WHAT IT CANNOT DO. The
        // loop above will not exit until a frame has arrived after the join, so this assertion reads
        // the very counter that gated the exit: inverting that counter leaves this test green, which
        // was checked rather than reasoned about. It is a backstop, not an independent check.
        //
        // The VALUE is in the arrangement, not here. The old form compared two elapsed-millisecond
        // stamps taken on the same busy machine, so a scheduling stall moved the goalposts and
        // reported a defect that was not there — the first client had simply finished before the
        // second connected. Now the first client is held open until the second has genuinely joined,
        // so the scenario cannot fail to set itself up; if it truly cannot overlap, the 30-second
        // token ends the test with a cancellation, which is a real fault or a dead host rather than
        // a busy CI box.
        Assert.True(
            firstRun.FramesAfterJoin > 0,
            "The first client received no frames after the late client's first one, so the late " +
            "client did not actually join an in-progress stream.");

        foreach (var frame in firstRun.Frames.Concat(second.Frames))
        {
            Assert.True(
                Contains(frame, payload),
                "A frame lost its payload when a second client joined an in-progress stream.");
        }
    }

    [Fact]
    public async Task ARecyclingCaptureServiceIsDetected()
    {
        // THE NEGATIVE CONTROL, kept rather than thrown away with a scratch probe. The two tests
        // above are only worth their runtime if they would actually go red when the shared buffer is
        // recycled instead of merely aliased, and an assertion nobody has ever seen fail is a
        // liability — RemEx-lcp8's third attempt shipped a buffer pool whose own tests passed
        // perfectly while the real path corrupted video.
        //
        // So: same host, same two-client scenario, same Contains() assertion, but a capture service
        // that refills the shared array in place on every capture, exactly as a pool would. This
        // pins the SENSITIVITY of the checks above; if a future change made frames arrive
        // content-independent, this goes red and says so.
        var payload = MakeJpeg(0x3C);
        var capture = new RecyclingCaptureService(payload);
        await using var factory = FactoryWith(capture);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();
        var expected = MakeJpeg(0x3C);

        var both = await Task.WhenAll(
            RunClientAsync(factory, FramesPerClient, clock, cts.Token),
            RunClientAsync(factory, FramesPerClient, clock, cts.Token));

        var intact = both.SelectMany(run => run.Frames).Count(frame => Contains(frame, expected));
        Assert.True(
            intact == 0,
            $"{intact} frame(s) still carried the ORIGINAL payload even though the capture service " +
            "overwrites its buffer on every capture. The intactness checks in the tests above are " +
            "therefore not sensitive to a recycled buffer, which is the only failure they exist to " +
            "catch.");
    }

    /// <summary>
    /// The defect, as a permanent control: one array, refilled in place per capture — a pool handing
    /// the same buffer to whichever client asks next while the previous client's send may still be
    /// in flight. Used only by <see cref="ARecyclingCaptureServiceIsDetected"/>.
    /// </summary>
    private sealed class RecyclingCaptureService : IScreenCaptureService
    {
        private readonly byte[] _shared;
        private int _captures;

        public RecyclingCaptureService(byte[] shared) => _shared = shared;

        public string? BackendName => "recycling-fake";

        public Task<ReadOnlyMemory<byte>> CaptureScreenAsync(
            int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _captures);
            // Keep the JPEG markers so only the payload body changes — the frame stays well-formed,
            // which is what makes the corruption silent rather than obviously broken.
            for (var i = 4; i < _shared.Length - 2; i++) _shared[i] = (byte)(0x80 + (n % 16));
            return Task.FromResult<ReadOnlyMemory<byte>>(_shared);
        }

        public Task<byte[]?> CaptureRawScreenAsync(
            double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);

        public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);
    }
}
