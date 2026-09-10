using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// The host must not announce <c>file_transfer_complete</c> before the peer has acked the bytes it just
/// streamed (RemEx-zd8ws).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS NEEDS ITS OWN TESTS RATHER THAN AN ASSERTION BOLTED ONTO THE HAPPY PATH. The property here
/// is an ORDERING between two sockets — the bulk data leaves on /ws/files and the completion on the
/// control /ws — and TCP orders bytes only within one connection. A test that merely checks "a complete
/// was sent, and it named the right hash" passes just as happily when the complete overtakes the data,
/// which is precisely the bug: every push smaller than <c>MaxUnackedBytes</c> (8 MB) shipped its
/// completion before a single byte was acknowledged, and the phone finalized a zero-byte transfer.
/// </para>
/// <para>
/// So the discriminating assertion is negative and time-shaped: with the final ack withheld, the sender
/// must still be blocked. That is why these tests watch the sender TASK rather than only its output —
/// deleting the drain makes the task run to completion with no ack in sight, and the first assertion
/// below goes red. Asserting only on the final ordering would not have caught it: once the ack is
/// delivered, the fixed and the broken sender produce byte-identical control traffic.
/// </para>
/// </remarks>
public sealed class HostSendDrainTests
{
    private const string ClientId = "paired-android-device";

    /// <summary>Long enough that a blocked sender stays blocked for the observation window below.</summary>
    private static readonly TimeSpan PatientDrain = TimeSpan.FromSeconds(30);

    /// <summary>Short enough that the gave-up branch is reachable without stalling the suite.</summary>
    private static readonly TimeSpan ImpatientDrain = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a sender is watched for a completion it must not send yet. A broken sender reaches the
    /// complete within microseconds of the last frame — every socket here answers from memory — so this
    /// is orders of magnitude more than it needs, and dead time in only one test.
    /// </summary>
    private static readonly TimeSpan NoCompleteWindow = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ASmallPush_SendsNoCompleteUntilThePeerHasAckedEveryByte()
    {
        // Deliberately under MaxUnackedBytes: this is the size class that never trips the backpressure
        // wait, and so never forced the ack round trip that was accidentally hiding the bug on large
        // transfers. A screenshot is exactly this shape.
        var payload = RandomBytes((256 * 1024) + 4096);
        Assert.True(payload.Length < FileTransferLimits.MaxUnackedBytes, "the point of this test is a size that never trips backpressure");

        using var fixture = new SendFixture(PatientDrain);
        var sender = fixture.StartSend(payload);

        // The source has reported end-of-stream, so every data frame is on the wire and the sender is at
        // the drain.
        await fixture.SourceDrained.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // THE ASSERTION THAT KILLS THE MUTANT. No ack has been delivered, so the sender must still be
        // waiting — and must not have said the transfer is complete.
        var finishedEarly = await Task.WhenAny(sender, Task.Delay(NoCompleteWindow)) == sender;
        Assert.False(finishedEarly, "the sender completed while the peer had acked nothing");
        Assert.Empty(fixture.Control.MessagesOfType(MessageTypes.FileTransferComplete));

        // Now play the peer's final ack, which both receivers send unconditionally on the final frame.
        fixture.Session.OnAck(payload.Length);

        await sender.WaitAsync(TimeSpan.FromSeconds(10));

        var completes = fixture.Control.MessagesOfType(MessageTypes.FileTransferComplete);
        var complete = Assert.Single(completes);
        Assert.Equal(fixture.TransferId, complete.FileTransferComplete!.TransferId);
        Assert.Equal(Sha256B64(payload), complete.FileTransferComplete!.Sha256Base64);
    }

    [Fact]
    public async Task APeerThatStopsAcking_GetsNoCompleteAndIsToldToLetGo()
    {
        var payload = RandomBytes(8192);
        var logger = new WarningCapturingLogger<TransferSessionManager>();

        using var fixture = new SendFixture(ImpatientDrain, logger);
        var sender = fixture.StartSend(payload);

        // No ack is ever delivered. The idle window expires and the transfer is abandoned.
        await sender.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(fixture.Control.MessagesOfType(MessageTypes.FileTransferComplete));

        // The peer accepted, so it is holding a receive session, a sink and a staging partial. Leaving
        // quietly would strand all three until its 7-day sweep (RemEx-cc30z).
        var control = Assert.Single(fixture.Control.MessagesOfType(MessageTypes.FileTransferControl));
        Assert.Equal(FileTransferControlActions.Cancel, control.FileTransferControl!.Action);
        Assert.Equal(fixture.TransferId, control.FileTransferControl!.TransferId);

        Assert.Contains(logger.Warnings, w => w.Contains("gave up waiting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AZeroByteSend_CompletesWithoutWaitingForAnAckThatWillNeverCome()
    {
        // A zero-byte source sends no frames at all, so no final frame is ever marked — and therefore no
        // ack is ever sent. A drain written as "wait for at least one ack" instead of "wait until the
        // committed offset covers what we sent" would hang here until the idle window expired and then
        // report a working transfer as stalled. The impatient timeout makes that failure fast, not
        // invisible.
        using var fixture = new SendFixture(ImpatientDrain);
        var sender = fixture.StartSend([]);

        await sender.WaitAsync(TimeSpan.FromSeconds(10));

        var complete = Assert.Single(fixture.Control.MessagesOfType(MessageTypes.FileTransferComplete));
        Assert.Equal(Sha256B64([]), complete.FileTransferComplete!.Sha256Base64);
        Assert.Empty(fixture.Control.MessagesOfType(MessageTypes.FileTransferControl));
    }

    // ── Fixture ────────────────────────────────────────────────────────────────

    private static string Sha256B64(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));

    private static byte[] RandomBytes(int length)
    {
        var b = new byte[length];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>
    /// Stands up just enough of the manager to run one host-sent transfer: a data socket to swallow the
    /// frames, a control socket to record what was announced, and the send session the test acks through.
    /// </summary>
    private sealed class SendFixture : IDisposable
    {
        private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory();
        private readonly DirectoryInfo _dest = Directory.CreateTempSubdirectory();
        private readonly TransferSessionManager _manager;

        public SendFixture(TimeSpan drainTimeout, ILogger<TransferSessionManager>? logger = null)
        {
            var files = new FakeFileTransferService(_dest.FullName);
            var resolver = new SharedRootReadResolver(
                files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
            _manager = new TransferSessionManager(
                logger ?? NullLogger<TransferSessionManager>.Instance, files, resolver, _staging.FullName)
            {
                AckDrainIdleTimeout = drainTimeout,
            };

            TransferId = Guid.NewGuid().ToString("N");
            Session = new TransferSessionManager.SendSession(TransferId, ClientId);
        }

        public string TransferId { get; }
        public TransferSessionManager.SendSession Session { get; }
        public RecordingControlSocket Control { get; } = new();
        public TaskCompletionSource SourceDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartSend(byte[] payload)
        {
            var channel = new TransferSessionManager.FileChannel(new SwallowingSocket());
            var source = new EndOfStreamSignallingStream(new MemoryStream(payload), SourceDrained);
            return _manager.StreamSenderAsync(channel, Session, source, payload.Length, Control, CancellationToken.None);
        }

        public void Dispose()
        {
            _manager.Dispose();
            try { _staging.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
            try { _dest.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// A source that reports when the sender has read it to the end — the exact instant the send loop
    /// ends and the drain begins. Counting frames on the data socket would tie the test to the frame
    /// layout; this ties it to the loop.
    /// </summary>
    private sealed class EndOfStreamSignallingStream(Stream inner, TaskCompletionSource drained) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            if (read <= 0) drained.TrySetResult();
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read <= 0) drained.TrySetResult();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The sender disposes its source in its finally; end-of-stream may never have been reached
            // on an abandoned transfer, so unblock anyone waiting rather than leaving a hung await.
            if (disposing)
            {
                drained.TrySetResult();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The /ws/files side. The frames' contents are covered by FileChannelSendFramingTests.</summary>
    private sealed class SwallowingSocket : WebSocket
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
            => throw new NotSupportedException("the send path never reads");

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// The control /ws side, decoding each completed message so the test can ask what was announced and
    /// — more to the point — what was not.
    /// </summary>
    private sealed class RecordingControlSocket : WebSocket
    {
        private readonly Lock _gate = new();
        private readonly List<byte> _pending = [];
        private readonly List<RemexMessage> _messages = [];

        public IReadOnlyList<RemexMessage> MessagesOfType(string type)
        {
            lock (_gate)
            {
                return [.. _messages.Where(m => string.Equals(m.Type, type, StringComparison.Ordinal))];
            }
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            lock (_gate)
            {
                _pending.AddRange(buffer.AsSpan().ToArray());
                if (end)
                {
                    var json = Encoding.UTF8.GetString([.. _pending]);
                    _pending.Clear();
                    var message = JsonSerializer.Deserialize<RemexMessage>(json);
                    if (message is not null) _messages.Add(message);
                }
            }

            return Task.CompletedTask;
        }

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
    }

    private sealed class WarningCapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, ex));
        }
    }
}
