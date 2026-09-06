using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// RemEx-xefvb: <see cref="TransferSessionManager.MaxUnackedBytes"/> (the sender's backpressure cap in
/// <c>StreamSenderAsync</c>) was a <c>const</c> until now, so no test could reach the wait without
/// literally streaming 8 MB. These tests drive it through the same test-only override shape
/// <c>HostSendDrainTests</c> uses for the other seams on <see cref="TransferSessionManager"/>, sized down
/// so the branch is reachable in milliseconds.
/// </summary>
/// <remarks>
/// Mirrors the Kotlin coverage added for the equivalent guard in <c>FileHostHandler</c>
/// (<c>FileHostHandlerTest.downloadSend_stopsReadingOnceTooMuchIsUnacked</c>, RemEx-68wwl) plus its
/// control.
/// </remarks>
public sealed class HostSendBackpressureTests
{
    private const string ClientId = "paired-android-device";
    private const int FrameBytes = FileTransferLimits.DataPayloadBytes;

    private static readonly TimeSpan PatientDrain = TimeSpan.FromSeconds(30);

    /// <summary>How long a plateau is watched for a frame that must not arrive yet.</summary>
    private static readonly TimeSpan NoProgressWindow = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ASourceBiggerThanTheCap_StopsAfterTheFrameThatBreachesItThenResumesOnAck()
    {
        // Cap = 2 frames. A MemoryStream source hands back a full DataPayloadBytes on every read while
        // bytes remain, so frame boundaries are exact: frames 1-3 land (the check is sentOffset-so-far vs
        // the cap, evaluated before the frame that would push it over is sent — sending #3 takes
        // sentOffset to 3 frames, which is what actually breaches a 2-frame cap), and #4 must wait.
        var cap = 2L * FrameBytes;
        var payload = RandomBytes(4 * FrameBytes);

        using var fixture = new BackpressureFixture(PatientDrain, maxUnackedBytesOverride: cap);
        var sender = fixture.StartSend(payload);

        for (var i = 0; i < 3; i++)
            await fixture.Data.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // THE ASSERTION THAT KILLS THE MUTANT. With no ack delivered, the 4th frame must not arrive —
        // the sender must be blocked in the backpressure wait, not merely slow — and the send must not
        // have run to completion around it.
        var fourthFrame = fixture.Data.Frames.Reader.ReadAsync().AsTask();
        var sentPastTheCap = await Task.WhenAny(fourthFrame, Task.Delay(NoProgressWindow)) == fourthFrame;
        Assert.False(sentPastTheCap, "the sender sent past the cap with no ack in sight");
        var finishedWithoutWaiting = await Task.WhenAny(sender, Task.Delay(NoProgressWindow)) == sender;
        Assert.False(finishedWithoutWaiting, "the sender finished without ever waiting for an ack");

        // Acking just the first frame brings outstanding bytes (3 frames - 1 frame = 2) back to exactly
        // the cap, which is not OVER it, so the sender must resume and send the 4th (final) frame.
        fixture.Session.OnAck(FrameBytes);
        await fourthFrame.WaitAsync(TimeSpan.FromSeconds(10));

        // Ack the rest so the post-loop drain (WaitForFinalAckAsync) is satisfied too and the transfer
        // can actually finish.
        fixture.Session.OnAck(payload.Length);
        await sender.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, fixture.Data.FrameCount);
    }

    [Fact]
    public async Task TheSameSourceUnderTheDefaultCap_StreamsAllFramesWithoutAnInterimAck()
    {
        // Control: identical source, but left at the real 8 MB default, which this 1 MB source never
        // approaches. Every frame should land back-to-back with no ack needed until the final drain —
        // proving the plateau above is the cap's doing and not some other stall.
        var payload = RandomBytes(4 * FrameBytes);
        Assert.True(payload.Length < FileTransferLimits.MaxUnackedBytes, "the point of this test is a source the default cap never throttles");

        using var fixture = new BackpressureFixture(PatientDrain, maxUnackedBytesOverride: null);
        var sender = fixture.StartSend(payload);

        for (var i = 0; i < 4; i++)
            await fixture.Data.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Still waiting on the final drain ack, which no test in this file has sent yet.
        var finishedEarly = await Task.WhenAny(sender, Task.Delay(NoProgressWindow)) == sender;
        Assert.False(finishedEarly, "the sender completed before the final ack");

        fixture.Session.OnAck(payload.Length);
        await sender.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, fixture.Data.FrameCount);
    }

    // ── Fixture ────────────────────────────────────────────────────────────────

    private static byte[] RandomBytes(int length)
    {
        var b = new byte[length];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>
    /// Stands up just enough of the manager to run one host-sent transfer, the same shape as
    /// <c>HostSendDrainTests.SendFixture</c> but with a data socket that counts frames instead of
    /// swallowing them, since these tests need to watch progress stall rather than only the outcome.
    /// </summary>
    private sealed class BackpressureFixture : IDisposable
    {
        private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory();
        private readonly DirectoryInfo _dest = Directory.CreateTempSubdirectory();
        private readonly TransferSessionManager _manager;

        public BackpressureFixture(TimeSpan drainTimeout, long? maxUnackedBytesOverride)
        {
            var files = new FakeFileTransferService(_dest.FullName);
            var resolver = new SharedRootReadResolver(
                files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
            _manager = new TransferSessionManager(
                NullLogger<TransferSessionManager>.Instance, files, resolver, _staging.FullName)
            {
                AckDrainIdleTimeout = drainTimeout,
                MaxUnackedBytes = maxUnackedBytesOverride ?? FileTransferLimits.MaxUnackedBytes,
            };

            TransferId = Guid.NewGuid().ToString("N");
            Session = new TransferSessionManager.SendSession(TransferId, ClientId);
        }

        public string TransferId { get; }
        public TransferSessionManager.SendSession Session { get; }
        public CountingDataSocket Data { get; } = new();

        public Task StartSend(byte[] payload)
        {
            var channel = new TransferSessionManager.FileChannel(Data);
            return _manager.StreamSenderAsync(
                channel, Session, new MemoryStream(payload), payload.Length, new DiscardingControlSocket(), CancellationToken.None);
        }

        public void Dispose()
        {
            _manager.Dispose();
            try { _staging.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
            try { _dest.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// The /ws/files side. Counts whole frames — one <see cref="SendAsync"/> call each, always
    /// <c>endOfMessage: true</c> per <c>FileChannel.SendFrameAsync</c> — via a channel the test can await,
    /// so it can tell "the sender stalled" from "the sender is merely slow" without polling.
    /// </summary>
    private sealed class CountingDataSocket : WebSocket
    {
        private int _frameCount;
        public Channel<int> Frames { get; } = Channel.CreateUnbounded<int>();
        public int FrameCount => Volatile.Read(ref _frameCount);

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("the send path never reads");

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            Frames.Writer.TryWrite(Interlocked.Increment(ref _frameCount));
            return Task.CompletedTask;
        }
    }

    /// <summary>The control /ws side. Nothing under test here reads the control channel, so it only needs
    /// to accept sends without throwing.</summary>
    private sealed class DiscardingControlSocket : WebSocket
    {
        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("nothing here reads");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
            => Task.CompletedTask;
    }
}
