using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of <c>media_seek</c>: what a scrubber drag on the phone actually does to the PC
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// DRIVEN THROUGH THE REAL <c>switch</c> RATHER THAN BY CALLING A METHOD, which is the difference
/// between this file and <c>ClipboardPushHandlerTests</c> next door — that one says plainly that
/// deleting its case from <c>HandleAsync</c> would leave every test in it green. That is the
/// RemEx-y6x6 shape, and it is worth closing here because this feature has NO reply on the wire: a
/// seek that never reaches the monitor produces exactly the same observable traffic as one that did,
/// so a routing mistake would present as a scrubber that snaps back with nothing in any log.
/// </para>
/// <para>
/// The connection is loopback because <c>isPaired</c> is seeded from it — <c>media_seek</c> is
/// pairing-gated by the default-true <c>RequiresPairing</c>, which is the correct gate for a message
/// that reaches into the user's media player, and a test that had to complete a PIN handshake to
/// prove routing would be testing pairing instead. Loopback also skips the media stream, so the
/// mocked monitor is asked for nothing except the seek.
/// </para>
/// <para>
/// WHAT THESE DO NOT PROVE: that any player moves. <c>TrySeekAsync</c> is mocked here; the platform
/// readers are the ones that talk to SMTC and MPRIS, and nothing in this assembly can observe them.
/// </para>
/// </remarks>
public class MediaSeekDispatchTests
{
    /// <summary>Delivers a scripted list of messages, one per receive, then closes.</summary>
    /// <remarks>
    /// Each message is written whole into the caller's buffer and reported as a complete text frame,
    /// which is what <c>HandleAsync</c>'s receive loop expects. The close after the last one is what
    /// makes the handler unwind so the test can assert instead of hanging.
    /// </remarks>
    private sealed class ScriptedWebSocket(params RemexMessage[] script) : WebSocket
    {
        private int _next;

        public List<RemexMessage> Sent { get; } = [];

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
            {
                lock (Sent) Sent.Add(message);
            }

            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            if (_next >= script.Length)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var payload = MessageSerializer.Serialize(script[_next++]);
            payload.CopyTo(b.Array.AsSpan(b.Offset));
            return Task.FromResult(
                new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }

    [Fact]
    public async Task ASeekReachesTheMonitorWithThePositionTheClientAskedFor()
    {
        var monitor = new Mock<IMediaSessionMonitor>();
        monitor.Setup(m => m.TrySeekAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var socket = await RunAsync(monitor.Object, Seek(93_000));

        monitor.Verify(m => m.TrySeekAsync(93_000, It.IsAny<CancellationToken>()), Times.Once);

        // ONCE, NOT ONCE-OR-MORE. A retry here would be a second seek on a player the user is
        // dragging through, and the second one would land at the position the first already left.
        monitor.VerifyNoOtherCalls();

        // AND NOTHING GOES BACK. The reply is the next media_state, published by the sampler to every
        // client; an acknowledgement on this socket would describe the dispatch rather than the
        // position, and it would arrive before the position it claimed to be about.
        await AssertSeekAddedNoTrafficAsync(socket);
    }

    [Fact]
    public async Task ASeekWithNoPayloadTouchesNothing()
    {
        // A paired client sending the bare type — an older build, or a serializer that dropped the
        // payload — must not become a seek to zero, which would restart whatever the PC is playing.
        var monitor = new Mock<IMediaSessionMonitor>();

        await RunAsync(monitor.Object, new RemexMessage { Type = MessageTypes.MediaSeek });

        monitor.Verify(
            m => m.TrySeekAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ASessionThatRefusesTheSeekIsNotAnErrorAndSaysNothing()
    {
        // Ordinary rather than exceptional: an unseekable session, a player that declines, or a host
        // with no seek target at all all answer false. The connection must survive it and the client
        // must hear nothing, because hearing nothing is precisely how the phone knows to put its own
        // optimistic position back.
        var monitor = new Mock<IMediaSessionMonitor>();
        monitor.Setup(m => m.TrySeekAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var socket = await RunAsync(monitor.Object, Seek(7_500), Seek(8_000));

        monitor.Verify(m => m.TrySeekAsync(7_500, It.IsAny<CancellationToken>()), Times.Once);
        monitor.Verify(m => m.TrySeekAsync(8_000, It.IsAny<CancellationToken>()), Times.Once);
        await AssertSeekAddedNoTrafficAsync(socket);
    }

    /// <summary>
    /// Asserts that carrying seeks put nothing extra on the socket.
    /// </summary>
    /// <remarks>
    /// COMPARED AGAINST A CONNECTION THAT SENT NO SEEK, not against zero: every connection is greeted
    /// with host_info, launcher_sync and layout_sync before the receive loop starts, so "empty" is
    /// the wrong bar and would have to be updated by anyone who adds a greeting. What is being pinned
    /// is that a seek adds nothing — an equal message list is the only form of that claim which
    /// survives the greeting changing.
    /// </remarks>
    private static async Task AssertSeekAddedNoTrafficAsync(ScriptedWebSocket socket)
    {
        var greetingOnly = await RunAsync(Mock.Of<IMediaSessionMonitor>());

        List<string> sent, greeting;
        lock (socket.Sent) sent = [.. socket.Sent.Select(m => m.Type ?? string.Empty)];
        lock (greetingOnly.Sent) greeting = [.. greetingOnly.Sent.Select(m => m.Type ?? string.Empty)];

        Assert.Equal(greeting, sent);
        Assert.DoesNotContain(sent, t => t.StartsWith("media_", StringComparison.Ordinal));
    }

    private static RemexMessage Seek(long positionMs) => new()
    {
        Type = MessageTypes.MediaSeek,
        MediaSeek = new MediaSeekRequest { PositionMs = positionMs },
    };

    /// <summary>Drives one whole loopback connection carrying <paramref name="script"/>.</summary>
    /// <remarks>
    /// The collaborators are <c>null!</c> where this path never reaches them — a primary constructor
    /// captures only what it uses — with the same two exceptions the other handler tests carry: the
    /// file-transfer handler, because teardown calls it on every exit path, and the session registry,
    /// because <c>HandleAsync</c> registers on its first line.
    /// </remarks>
    private static async Task<ScriptedWebSocket> RunAsync(
        IMediaSessionMonitor monitor, params RemexMessage[] script)
    {
        var socket = new ScriptedWebSocket(script);

        var handler = new PingPongHandler(
            NullLogger<PingPongHandler>.Instance,
            null!,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,
            null!,
            NewFileTransferHandler(),
            null!,
            null!,
            null!,
            new ClientSessionRegistry(),
            NewNameStore(),
            NewActivityStore(),
            new FakeHostClipboard(),
            monitor);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await handler.HandleAsync(
                socket,
                isLoopback: true,
                isTrustedForPinAutoFetch: false,
                remoteAddress: "127.0.0.1",
                cts.Token);
        }
        finally
        {
            handler.Dispose();
        }

        return socket;
    }

    private static FileTransferHandler NewFileTransferHandler()
    {
        var svc = Mock.Of<Remex.Core.Services.FileTransfer.IFileTransferService>();
        var trust = Mock.Of<Remex.Core.Services.FileTransfer.IFileTrustService>();
        var volumes = new Remex.Agent.Services.FileTransfer.VolumeEnumerator(
            NullLogger<Remex.Agent.Services.FileTransfer.VolumeEnumerator>.Instance);
        return new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, svc, trust, volumes,
            new Remex.Agent.Services.FileTransfer.SharedRootReadResolver(svc, trust, volumes));
    }

    private static PairedClientNameStore NewNameStore() =>
        new(NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json"));

    private static PairedDeviceActivityStore NewActivityStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
}
