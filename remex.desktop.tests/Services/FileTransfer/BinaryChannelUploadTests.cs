using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services.FileTransfer;

/// <summary>
/// Uploads to the PC's own host travel as raw frames on the binary <c>/ws/files</c> channel, not as
/// Base64-in-JSON on the control socket (perf audit P1-5) — and still never announce completion
/// before the host has acked every byte.
/// </summary>
/// <remarks>
/// <para>
/// THE ORDERING TESTS ARE THE POINT. The frames and <c>file_transfer_complete</c> travel on two
/// separate sockets, so a sender that completes the moment its last frame is SENT lets the completion
/// overtake data still in flight, and the host finalizes a short file ("Transfer incomplete."). Every
/// other sender of this protocol drains first (docs/REGRESSION-GUARDS.md, "Never announce
/// <c>file_transfer_complete</c> before the peer has acked the data"), and this one is the fourth.
/// The fakes below never ack on their own in those tests, so a completion sent before the ack is
/// visible on the wire rather than hidden behind a fast peer.
/// </para>
/// <para>
/// The legacy path's own behaviour — cancel ordering, leak reaping — is pinned by
/// <c>FileTransferCancelOrderingTests</c> and <c>FileTransferClientLeakTests</c>, which construct the
/// client without a connector and so still reach it unchanged.
/// </para>
/// </remarks>
public sealed class BinaryChannelUploadTests : IDisposable
{
    private const string RootId = "root-1";
    private const int Frame = FileTransferLimits.DataPayloadBytes;

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "remex-binary-upload-" + Guid.NewGuid().ToString("N"));

    public BinaryChannelUploadTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort test cleanup */ }
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The control connection: records what was sent and answers like the host.</summary>
    private sealed class FakeHost : IFileTransferConnection
    {
        private readonly List<RemexMessage> _sent = [];

        public event Action<RemexMessage>? FileTransferMessageReceived;

        public bool AdvertiseBinary { get; init; } = true;

        /// <summary>Answer an offer with this; null accepts.</summary>
        public string? DeclineReason { get; init; }

        /// <summary>Answer a completion with this; null verifies.</summary>
        public string? ResultError { get; init; }

        public TaskCompletionSource CompleteSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RemexMessage> Sent
        {
            get { lock (_sent) { return _sent.ToList(); } }
        }

        public Task SendAsync(RemexMessage message)
        {
            lock (_sent) { _sent.Add(message); }

            switch (message.Type)
            {
                case MessageTypes.FileRootsRequest:
                    Deliver(new RemexMessage
                    {
                        Type = MessageTypes.FileRootsResponse,
                        FileRootsResponse = new FileRootsResponse
                        {
                            Roots = [new FileSharedRoot { RootId = RootId, DisplayName = "Shared", IsWritable = true }],
                            FileCapabilities = new FileCapabilities { Protocol = 3, Binary = AdvertiseBinary, Ops = [] },
                        },
                    });
                    break;

                case MessageTypes.FileTransferOffer:
                    Deliver(new RemexMessage
                    {
                        Type = MessageTypes.FileTransferReady,
                        FileTransferReady = new FileTransferReady
                        {
                            TransferId = message.FileTransferOffer!.TransferId,
                            Accepted = DeclineReason is null,
                            DeclineReason = DeclineReason,
                        },
                    });
                    break;

                case MessageTypes.FileTransferComplete:
                    CompleteSent.TrySetResult();
                    Deliver(new RemexMessage
                    {
                        Type = MessageTypes.FileTransferResult,
                        FileTransferResult = new FileTransferResult
                        {
                            TransferId = message.FileTransferComplete!.TransferId,
                            Verified = ResultError is null,
                            Error = ResultError,
                        },
                    });
                    break;
            }

            return Task.CompletedTask;
        }

        public void Deliver(RemexMessage message) => FileTransferMessageReceived?.Invoke(message);
    }

    /// <summary>
    /// The binary channel. Records every frame with a private copy of its payload (the sender reuses
    /// its buffers), and lets the test decide when — and whether — the host acks.
    /// </summary>
    private sealed class FakeChannel : IFileFrameChannel
    {
        private readonly object _lock = new();
        private readonly List<(FileFrameEnvelope Envelope, byte[] Payload)> _frames = [];
        private readonly Dictionary<string, (Action<FileFrameEnvelope> OnFrame, Action OnClosed)> _subs = [];
        private bool _closed;

        /// <summary>Runs after each frame is recorded, on the sender's thread.</summary>
        public Action<FakeChannel, FileFrameEnvelope>? OnFrameSent { get; set; }

        public TaskCompletionSource FinalFrameSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(FileFrameEnvelope Envelope, byte[] Payload)> Frames
        {
            get { lock (_lock) { return _frames.ToList(); } }
        }

        public int SubscriberCount
        {
            get { lock (_lock) { return _subs.Count; } }
        }

        public IDisposable Subscribe(string transferId, Action<FileFrameEnvelope> onFrame, Action onClosed)
        {
            bool closed;
            lock (_lock)
            {
                _subs[transferId] = (onFrame, onClosed);
                closed = _closed;
            }

            if (closed) onClosed();
            return new Unsubscriber(() => { lock (_lock) { _subs.Remove(transferId); } });
        }

        public Task SendAsync(FileFrameEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_closed) throw new IOException("closed");
                _frames.Add((envelope, payload.ToArray()));
            }

            OnFrameSent?.Invoke(this, envelope);
            if (envelope.Final) FinalFrameSent.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>The host acks <paramref name="committed"/> bytes of <paramref name="transferId"/>.</summary>
        public void Ack(string transferId, long committed) => DeliverFrame(new FileFrameEnvelope
        {
            Kind = FileFrameKinds.Ack,
            TransferId = transferId,
            CommittedOffset = committed,
        });

        public void Error(string transferId, string error) => DeliverFrame(new FileFrameEnvelope
        {
            Kind = FileFrameKinds.Error,
            TransferId = transferId,
            Error = error,
        });

        public void Close()
        {
            List<Action> closers;
            lock (_lock)
            {
                _closed = true;
                closers = _subs.Values.Select(s => s.OnClosed).ToList();
            }

            foreach (var close in closers) close();
        }

        private void DeliverFrame(FileFrameEnvelope envelope)
        {
            Action<FileFrameEnvelope>? onFrame = null;
            lock (_lock)
            {
                if (_subs.TryGetValue(envelope.TransferId, out var sub)) onFrame = sub.OnFrame;
            }

            onFrame?.Invoke(envelope);
        }

        private sealed class Unsubscriber(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    /// <summary>Acks every final frame at once, as the host does: committed = end of that frame.</summary>
    private static void AckFinalFrames(FakeChannel channel, FileFrameEnvelope frame)
    {
        if (frame.Final) channel.Ack(frame.TransferId, frame.Offset + frame.Length);
    }

    private sealed class FakeConnector(IFileFrameChannel? channel) : IFileChannelConnector
    {
        public int Calls { get; private set; }

        public Task<IFileFrameChannel?> TryAcquireAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(channel);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private string WriteSource(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        var path = Path.Combine(_tempDir, "source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<FileTransferClient> ConnectedClientAsync(FakeHost host, IFileChannelConnector connector)
    {
        var client = new FileTransferClient(host, connector);

        // Roots come first in the real UI too: they carry the capability handshake and the ids the
        // binary path is allowed to write into.
        await client.ListRemoteRootsAsync(CancellationToken.None);
        return client;
    }

    private static void AssertNoLegacyTraffic(FakeHost host)
    {
        host.Sent.Select(m => m.Type).Should().NotContain(
            new[] { MessageTypes.FileTransferStart, MessageTypes.FileTransferChunk, MessageTypes.FileTransferEnd },
            "a binary-channel upload must not also speak the Base64 protocol on the control socket");
    }

    private static void AssertReaped(FileTransferClient client)
    {
        client.PendingUploadRegistrationCount.Should().Be(0, "the ready and result waiters must be reaped");
        client.ActiveTransferCount.Should().Be(0, "the idle-watchdog lease must be reaped");
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChunksTravelAsBinaryFramesAndNotAsBase64Messages()
    {
        var host = new FakeHost();
        var channel = new FakeChannel { OnFrameSent = AckFinalFrames };
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));

        var length = (2 * Frame) + 12345;
        var source = WriteSource(length);
        var expected = await File.ReadAllBytesAsync(source);

        await client.UploadAsync(source, RootId, "remote/dir/file.bin", progress: null, CancellationToken.None);

        AssertNoLegacyTraffic(host);

        var frames = channel.Frames;
        frames.Should().HaveCount(3);
        frames.Select(f => f.Envelope.Kind).Should().OnlyContain(k => k == FileFrameKinds.Data);
        frames.Select(f => f.Envelope.Offset).Should().Equal(0L, Frame, 2L * Frame);
        frames.Select(f => f.Envelope.Length).Should().Equal(Frame, Frame, 12345);
        frames.Select(f => f.Envelope.Final).Should().Equal(new[] { false, false, true },
            "only the last frame is final — the host acks the tail only on the final flag");
        frames.SelectMany(f => f.Payload).ToArray().Should().Equal(expected,
            "the raw bytes, in order, with no encoding");

        var offer = host.Sent.Single(m => m.Type == MessageTypes.FileTransferOffer).FileTransferOffer!;
        offer.Mode.Should().Be(FileTransferModes.Upload);
        offer.DestRoot.Should().Be(RootId);
        offer.DestRelativePath.Should().Be("remote/dir", "an upload's DestRelativePath is the DIRECTORY");
        offer.FileName.Should().Be("file.bin");
        offer.Size.Should().Be(length);
        frames.Should().OnlyContain(f => f.Envelope.TransferId == offer.TransferId);

        var complete = host.Sent.Single(m => m.Type == MessageTypes.FileTransferComplete).FileTransferComplete!;
        complete.TransferId.Should().Be(offer.TransferId);
        complete.Sha256Base64.Should().Be(Convert.ToBase64String(SHA256.HashData(expected)));

        AssertReaped(client);
    }

    [Fact]
    public async Task CompletionIsHeldBackUntilTheHostHasAckedTheLastByte()
    {
        // THE GUARD (RG:403). No ack arrives on its own here. A sender that completes as soon as the
        // last frame is sent puts file_transfer_complete on the wire while this test waits; one that
        // drains cannot, however long it waits, because nothing here will ever satisfy it except the
        // test itself. A partial ack is delivered first, so "any ack" is not mistaken for "the ack".
        var host = new FakeHost();
        var channel = new FakeChannel();
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));

        var length = Frame + 1000;
        var source = WriteSource(length);

        var upload = client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None);

        await channel.FinalFrameSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var transferId = channel.Frames[0].Envelope.TransferId;

        // Long enough for a sender that does not wait to have sent its completion many times over.
        await Task.Delay(300);
        host.CompleteSent.Task.IsCompleted.Should().BeFalse(
            "completion must not be announced while the host has acked nothing");

        channel.Ack(transferId, length - 1);
        await Task.Delay(300);
        host.CompleteSent.Task.IsCompleted.Should().BeFalse(
            "an ack short of the last byte still leaves data the completion could overtake");
        upload.IsCompleted.Should().BeFalse();

        channel.Ack(transferId, length);
        await host.CompleteSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await upload.WaitAsync(TimeSpan.FromSeconds(10));

        AssertNoLegacyTraffic(host);
        AssertReaped(client);
    }

    [Fact]
    public async Task AChannelThatDropsBeforeTheFinalAckFailsInsteadOfCompleting()
    {
        // The other way the drain ends. Completing anyway here would ask the host to verify a file
        // whose tail it may never have received.
        var host = new FakeHost();
        var channel = new FakeChannel();
        channel.OnFrameSent = (ch, frame) => { if (frame.Final) ch.Close(); };
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));

        var source = WriteSource(Frame + 10);

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None));

        failure.Message.Should().Contain("acknowledged");
        host.Sent.Select(m => m.Type).Should().NotContain(MessageTypes.FileTransferComplete);
        AssertReaped(client);
    }

    [Fact]
    public async Task AnErrorFrameStopsTheUploadAndRetractsItOnTheControlPlane()
    {
        var host = new FakeHost();
        var channel = new FakeChannel();
        channel.OnFrameSent = (ch, frame) => ch.Error(frame.TransferId, "disk full");
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));

        // Several frames, so a sender that ignores the error would keep going.
        var source = WriteSource(4 * Frame);

        var failure = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None));

        failure.Message.Should().Contain("disk full", "the host's reason is what makes the error actionable");
        channel.Frames.Should().HaveCount(1, "the error must stop the loop before the next frame");
        host.Sent.Select(m => m.Type).Should().NotContain(MessageTypes.FileTransferComplete);

        var control = host.Sent.Where(m => m.Type == MessageTypes.FileTransferControl).ToList();
        control.Should().ContainSingle("the host keeps a staged partial until it is told to drop it");
        control[0].FileTransferControl!.Action.Should().Be(FileTransferControlActions.Cancel);
        control[0].FileTransferControl!.TransferId.Should().Be(channel.Frames[0].Envelope.TransferId);
        AssertReaped(client);
    }

    [Fact]
    public async Task CancellingMidUploadRetractsItOnceWithTheV3ControlMessage()
    {
        // The host accepted the offer and holds a staged partial, so the user's cancel must reach it -
        // exactly once, and as file_transfer_control (what a v3 receive session answers), never as the
        // legacy file_transfer_cancel, which that session does not listen for.
        var host = new FakeHost();
        var channel = new FakeChannel();
        using var cts = new CancellationTokenSource();
        channel.OnFrameSent = (_, frame) => { if (frame.Offset == 0) cts.Cancel(); };
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));

        // Several frames, so a sender that ignores the cancel would keep going.
        var source = WriteSource(4 * Frame);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.UploadAsync(source, RootId, "file.bin", progress: null, cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        channel.Frames.Should().HaveCount(1, "the cancel must stop the loop before the next frame");

        var types = host.Sent.Select(m => m.Type).ToList();
        types.Should().NotContain(MessageTypes.FileTransferComplete);
        types.Should().NotContain(MessageTypes.FileTransferCancel,
            "the v3 receive session is retracted with file_transfer_control, not the legacy message");

        var control = host.Sent.Where(m => m.Type == MessageTypes.FileTransferControl).ToList();
        control.Should().ContainSingle("the cancel gate sends the retraction once, however it is reached");
        control[0].FileTransferControl!.Action.Should().Be(FileTransferControlActions.Cancel);
        control[0].FileTransferControl!.TransferId.Should().Be(channel.Frames[0].Envelope.TransferId);

        channel.SubscriberCount.Should().Be(0, "the upload's channel subscription must be released");
        AssertReaped(client);
    }

    [Fact]
    public async Task SendingStopsAtTheUnackedCapUntilTheHostCatchesUp()
    {
        var host = new FakeHost();
        var channel = new FakeChannel();
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));
        client.UploadMaxUnackedBytes = Frame;

        var length = 4 * Frame;
        var source = WriteSource(length);
        var upload = client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None);

        // Two frames out, nothing acked: 2 x Frame unacked is over the cap, so the third must wait.
        await WaitUntilAsync(() => channel.Frames.Count == 2);
        await Task.Delay(200);
        channel.Frames.Should().HaveCount(2, "the sender must not run past the unacked cap");

        var transferId = channel.Frames[0].Envelope.TransferId;
        channel.OnFrameSent = (ch, frame) => ch.Ack(frame.TransferId, frame.Offset + frame.Length);
        channel.Ack(transferId, 2L * Frame);

        await upload.WaitAsync(TimeSpan.FromSeconds(10));
        channel.Frames.Should().HaveCount(4);
        AssertReaped(client);
    }

    [Fact]
    public async Task AnEmptyFileSendsNoFramesAndStillCompletes()
    {
        // Nothing sent, so nothing to drain: the wait is bounded on what was SENT, not on an ack
        // that an empty stream would never produce.
        var host = new FakeHost();
        var channel = new FakeChannel();
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));
        var source = WriteSource(0);

        await client.UploadAsync(source, RootId, "empty.bin", progress: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        channel.Frames.Should().BeEmpty();
        host.Sent.Select(m => m.Type).Should().Contain(MessageTypes.FileTransferComplete);
        AssertReaped(client);
    }

    [Fact]
    public async Task ADeclinedOfferSendsNoDataAndSurfacesTheReason()
    {
        // A reason the real host's TransferSessionManager.BeginReceiveAsync actually returns at offer
        // time (its size-range refusal), not one it can only produce later or not at all.
        var host = new FakeHost { DeclineReason = "File size out of range (max 5000000000 bytes)." };
        var channel = new FakeChannel();
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));
        var source = WriteSource(1000);

        var failure = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None));

        failure.Message.Should().Contain("out of range");
        channel.Frames.Should().BeEmpty();
        host.Sent.Select(m => m.Type).Should().NotContain(MessageTypes.FileTransferControl,
            "a declined offer left nothing on the host to retract");
        AssertReaped(client);
    }

    [Fact]
    public async Task AFailedVerificationIsReportedWithTheHostsReason()
    {
        var host = new FakeHost { ResultError = "SHA-256 mismatch — file corrupted in transit." };
        var channel = new FakeChannel { OnFrameSent = AckFinalFrames };
        using var client = await ConnectedClientAsync(host, new FakeConnector(channel));
        var source = WriteSource(1000);

        var failure = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None));

        failure.Message.Should().Contain("SHA-256 mismatch");
        AssertReaped(client);
    }

    [Fact]
    public async Task WithNoChannelTheUploadFallsBackToTheLegacyPath()
    {
        // A remote host, or a channel that would not open: the connector answers null and the upload
        // must still happen, over the path every host understands.
        var host = new FakeHost();
        var connector = new FakeConnector(channel: null);
        using var client = await ConnectedClientAsync(host, connector);
        var source = WriteSource(1000);

        var upload = client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None);
        await WaitUntilAsync(() => host.Sent.Any(m => m.Type == MessageTypes.FileTransferEnd));

        connector.Calls.Should().Be(1);
        host.Sent.Select(m => m.Type).Should().Contain(new[] { MessageTypes.FileTransferStart, MessageTypes.FileTransferChunk });
        host.Sent.Select(m => m.Type).Should().NotContain(MessageTypes.FileTransferOffer);

        var transferId = host.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId;
        host.Deliver(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true },
        });
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ARefusedLoopbackChannelFallsBackCleanlyToTheLegacyPath()
    {
        // The REAL connector, not a fake answering null: the "falls back cleanly" claim rests on
        // TryAcquireAsync turning a failed connect into null rather than an exception, and on the
        // upload having put nothing on the wire before it found out.
        int port;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }

        using var connector = new LoopbackFileChannelConnector(
            () => $"wss://127.0.0.1:{port}/ws",
            loadPins: () => Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>()));

        var direct = await connector.TryAcquireAsync(CancellationToken.None)
            .WaitAsync(LoopbackFileChannelConnector.ConnectTimeout + TimeSpan.FromSeconds(5));
        direct.Should().BeNull("a refused connect is a fallback, not a failure");

        var host = new FakeHost();
        using var client = await ConnectedClientAsync(host, connector);
        var source = WriteSource(1000);

        var upload = client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None);
        await WaitUntilAsync(() => host.Sent.Any(m => m.Type == MessageTypes.FileTransferEnd));

        host.Sent.Select(m => m.Type).Should().NotContain(MessageTypes.FileTransferOffer,
            "the binary path must not have announced a transfer it then abandoned");
        host.Sent.Select(m => m.Type).Should().Contain(new[] { MessageTypes.FileTransferStart, MessageTypes.FileTransferChunk });

        var transferId = host.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId;
        host.Deliver(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true },
        });
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
        AssertReaped(client);
    }

    [Fact]
    public async Task AHostThatDoesNotAdvertiseTheBinaryChannelIsNeverAskedForOne()
    {
        var host = new FakeHost { AdvertiseBinary = false };
        var connector = new FakeConnector(new FakeChannel());
        using var client = await ConnectedClientAsync(host, connector);
        var source = WriteSource(1000);

        var upload = client.UploadAsync(source, RootId, "file.bin", progress: null, CancellationToken.None);
        await WaitUntilAsync(() => host.Sent.Any(m => m.Type == MessageTypes.FileTransferEnd));

        connector.Calls.Should().Be(0, "a v2-style host gets the legacy path without a connect attempt");
        var transferId = host.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId;
        host.Deliver(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true },
        });
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnUnknownRootKeepsTheLegacyPath()
    {
        // The v3 receive path resolves only configured roots; the legacy one also re-maps a volume
        // target inside a pinned root (RemEx-hb1t.3). An id the listing did not name stays legacy.
        var host = new FakeHost();
        var connector = new FakeConnector(new FakeChannel());
        using var client = await ConnectedClientAsync(host, connector);
        var source = WriteSource(1000);

        var upload = client.UploadAsync(source, "volume-C", "file.bin", progress: null, CancellationToken.None);
        await WaitUntilAsync(() => host.Sent.Any(m => m.Type == MessageTypes.FileTransferEnd));

        connector.Calls.Should().Be(0);
        var transferId = host.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId;
        host.Deliver(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true },
        });
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("file.bin", "", "file.bin")]
    [InlineData("a/file.bin", "a", "file.bin")]
    [InlineData("a/b/file.bin", "a/b", "file.bin")]
    [InlineData(@"a\b\file.bin", "a/b", "file.bin")]
    [InlineData("/a/file.bin", "a", "file.bin")]
    public void TheRemoteFilePathSplitsIntoDirectoryAndName(string remote, string directory, string name)
    {
        FileTransferClient.SplitRemoteFilePath(remote).Should().Be((directory, name));
    }

    [Theory]
    [InlineData("wss://localhost:5005/ws", "wss://localhost:5005/ws/files?protocolVersion=3")]
    [InlineData("wss://127.0.0.1:5005/ws/", "wss://127.0.0.1:5005/ws/files?protocolVersion=3")]
    [InlineData("wss://localhost:5005", "wss://localhost:5005/ws/files?protocolVersion=3")]
    public void ALoopbackHostGetsTheFilesEndpoint(string hostAddress, string expected)
    {
        LoopbackFileChannelConnector.TryBuildFilesUri(hostAddress, out var uri).Should().BeTrue();
        uri.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("wss://192.168.1.20:5005/ws")]
    [InlineData("wss://my-pc.local:5005/ws")]
    [InlineData("not a uri")]
    public void ARemoteHostGetsNoBinaryChannel(string hostAddress)
    {
        // The host demands proof-of-possession on /ws/files from anything but loopback, which this
        // UI does not implement - so a remote host stays on the legacy path.
        LoopbackFileChannelConnector.TryBuildFilesUri(hostAddress, out _).Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never became true");
            await Task.Delay(10);
        }
    }
}
