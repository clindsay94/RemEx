using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that a loopback connection cannot ACT AS a paired phone by naming one (RemEx-4215).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-220r stopped a loopback connection being REACHED by a phone's client id:
/// <c>ClientSessionRegistry.Find</c> now requires <c>IdentityProven</c>, which loopback never has. It
/// did not stop loopback ACTING AS one. Loopback is authenticated by construction — it is the PC
/// talking to itself — and has no pairing to prove, so it never reached one of the freeze points that
/// settle <c>connectionClientId</c>: the id stayed whatever the sender last wrote, on every message,
/// for the whole session. Any local process, INCLUDING AN UNELEVATED ONE, could therefore open
/// <c>/ws</c> on 127.0.0.1, name itself a paired phone, and inherit that phone's persisted
/// <c>fullBrowseGranted</c> / <c>autoAcceptIncoming</c> grants through <c>file_volumes_request</c> and
/// <c>file_push_offer</c> — no PIN, no prompt.
/// </para>
/// <para>
/// THE TESTS ASSERT THE ABSENCE OF A QUESTION, NOT JUST A DENIED ANSWER. Checking only that the
/// response comes back refused would still pass if the host asked the trust store about the claimed
/// phone and merely happened to be told no. The property is that the claimed id NEVER REACHES the
/// trust store from an unproven connection, so both tests verify the mock was never asked about it.
/// </para>
/// <para>
/// Collaborators are mocked, real, or <c>null!</c> according to whether this path reaches them — a
/// primary constructor only captures what it uses, and the same technique is used by
/// <see cref="LoopbackTelemetryStreamTests"/>. <c>pairedClientRegistry</c> stays null because its only
/// dereference is the reconnect challenge, which is gated on <c>!isPaired</c> and so is unreachable
/// on loopback.
/// </para>
/// </remarks>
public class LoopbackIdentityClaimTests
{
    /// <summary>The id of a paired phone that already holds every grant worth stealing.</summary>
    private const string PhoneClientId = "phone-with-grants";

    /// <summary>Plays a fixed script of client messages, then holds the socket open for the reply.</summary>
    /// <remarks>
    /// HOLDING IT OPEN IS LOAD-BEARING. Both consent-gated handlers are dispatched OFF the reader loop
    /// (<c>RunDetachedAsync</c>) so a pending prompt cannot stall the connection — so closing as soon as
    /// the script runs out would race the response and the test would pass with nothing sent at all.
    /// </remarks>
    private sealed class ScriptedWebSocket(string awaitedResponseType, params RemexMessage[] script) : WebSocket
    {
        private readonly Queue<byte[]> _script = new(script.Select(MessageSerializer.Serialize));
        private readonly TaskCompletionSource _replied = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RemexMessage> Received { get; } = [];

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
            {
                lock (Received) Received.Add(message);
                if (message.Type == awaitedResponseType) _replied.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            byte[]? next = null;
            lock (_script)
            {
                if (_script.Count > 0) next = _script.Dequeue();
            }

            if (next is not null)
            {
                next.CopyTo(b.Array!, b.Offset);
                return new WebSocketReceiveResult(next.Length, WebSocketMessageType.Text, true);
            }

            // Script exhausted: wait for the detached handler's reply, then close. A timeout returns
            // Close rather than throwing, so the failure shows up as the missing response it is.
            try
            {
                await _replied.Task.WaitAsync(TimeSpan.FromSeconds(10), c);
            }
            catch (TimeoutException)
            {
            }

            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
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

    /// <summary>A trust store that would hand <see cref="PhoneClientId"/> everything, if asked.</summary>
    private static Mock<IFileTrustService> NewGenerousTrustService()
    {
        var trust = new Mock<IFileTrustService>();
        trust.Setup(t => t.IsFullBrowseGrantedAsync(PhoneClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        trust.Setup(t => t.IsAutoAcceptIncomingAsync(PhoneClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        trust.Setup(t => t.RequestConsentAsync(PhoneClientId, It.IsAny<FileConsentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileConsentDecision(Granted: true, Remember: true));
        return trust;
    }

    /// <summary>Runs one whole connection through the real handler and returns everything it sent.</summary>
    private static async Task<List<RemexMessage>> RunLoopbackConnectionAsync(
        IFileTrustService trust, string awaitedResponseType, params RemexMessage[] script)
    {
        var volumes = new Remex.Agent.Services.FileTransfer.VolumeEnumerator(
            NullLogger<Remex.Agent.Services.FileTransfer.VolumeEnumerator>.Instance);
        var transferService = Mock.Of<IFileTransferService>();
        var fileTransferHandler = new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, transferService, trust, volumes,
            new Remex.Agent.Services.FileTransfer.SharedRootReadResolver(transferService, trust, volumes));

        var handler = new PingPongHandler(
            NullLogger<PingPongHandler>.Instance,
            new TelemetryBackgroundService(
                Mock.Of<ITelemetryService>(), NullLogger<TelemetryBackgroundService>.Instance),
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,  // ScreenshotService: this test never takes one
            null!,  // PairingHandler: loopback never pairs
            fileTransferHandler,
            null!,  // TransferSessionManager: no transfer is ever negotiated
            null!,  // PairedClientRegistry: only the reconnect challenge reads it, and !isPaired gates that
            null!,  // FilePushOriginator: this test never pushes FROM the host
            new ClientSessionRegistry(),
            NewNameStore());

        var socket = new ScriptedWebSocket(awaitedResponseType, script);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await handler.HandleAsync(
                socket, isLoopback: true, isTrustedForPinAutoFetch: false,
                remoteAddress: "127.0.0.1", cts.Token);
        }
        finally
        {
            handler.Dispose();
        }

        lock (socket.Received) return [.. socket.Received];
    }

    [Fact]
    public async Task ALoopbackConnectionCannotBrowseVolumesAsAPairedPhone()
    {
        // THE BEAD. A local process claims a paired phone's id on the one message that carries it and
        // asks to browse the whole PC. The phone holds a remembered full-browse grant; the local
        // process must not inherit it.
        var trust = NewGenerousTrustService();

        var sent = await RunLoopbackConnectionAsync(
            trust.Object,
            MessageTypes.FileVolumesResponse,
            new RemexMessage
            {
                Type = MessageTypes.FileVolumesRequest,
                ProtocolVersion = ProtocolVersionPolicy.Current,
                ClientId = PhoneClientId,
                FileVolumesRequest = new FileVolumesRequest { RequestId = "vol-1" },
            });

        var response = Assert.Single(sent, m => m.Type == MessageTypes.FileVolumesResponse);
        Assert.False(response.FileVolumesResponse!.FullBrowseGranted);
        Assert.Empty(response.FileVolumesResponse.Volumes);

        // The grant was never even looked up: an unproven connection has no client identity to look up.
        trust.Verify(
            t => t.IsFullBrowseGrantedAsync(PhoneClientId, It.IsAny<CancellationToken>()), Times.Never);
        trust.Verify(
            t => t.RequestConsentAsync(PhoneClientId, It.IsAny<FileConsentRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ALoopbackConnectionCannotPushFilesAsAPairedPhone()
    {
        // The second door onto the same persisted trust: auto-accept-incoming skips the prompt
        // entirely, so a claimed id here means files land on the PC with nobody asked.
        var trust = NewGenerousTrustService();

        var sent = await RunLoopbackConnectionAsync(
            trust.Object,
            MessageTypes.FilePushResponse,
            new RemexMessage
            {
                Type = MessageTypes.FilePushOffer,
                ProtocolVersion = ProtocolVersionPolicy.Current,
                ClientId = PhoneClientId,
                FilePushOffer = new FilePushOffer
                {
                    PushId = "push-1",
                    Files = [new FilePushFile { Name = "payload.txt", Size = 12 }],
                },
            });

        var response = Assert.Single(sent, m => m.Type == MessageTypes.FilePushResponse);
        Assert.False(response.FilePushResponse!.Accepted);
        Assert.Null(response.FilePushResponse.TransferIds);

        trust.Verify(
            t => t.RequestConsentAsync(PhoneClientId, It.IsAny<FileConsentRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TheLocalUiStillGetsAWorkingConnection()
    {
        // THE OTHER DIRECTION, and the reason this file is not just two DoesNotContains. The PC's own
        // UI rides this same loopback socket; a fix that broke it would be silent, because the UI never
        // sends a clientId at all and so has nothing to notice missing.
        var sent = await RunLoopbackConnectionAsync(
            Mock.Of<IFileTrustService>(),
            MessageTypes.Pong,
            new RemexMessage { Type = MessageTypes.Ping, ProtocolVersion = ProtocolVersionPolicy.Current });

        Assert.Contains(sent, m => m.Type == MessageTypes.HostInfo);
        Assert.Contains(sent, m => m.Type == MessageTypes.Pong);
    }

    /// <summary>
    /// A name store pointed at a throwaway file.
    /// </summary>
    /// <remarks>
    /// NEVER the production path. That resolves to the machine-wide ProgramData store beside the
    /// pairing secrets, so a test constructing the default would read — and on any write, replace —
    /// the real device names on the developer's own machine.
    /// </remarks>
    private static PairedClientNameStore NewNameStore() =>
        new(NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json"));
}
