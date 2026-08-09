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
        => await RunLoopbackConnectionAsync(trust, awaitedResponseType, sessions: null, script);

    /// <summary>
    /// As above, but with a real <c>TransferSessionManager</c> wired in so the control-plane
    /// messages can be driven through the dispatch rather than called directly (RemEx-juas).
    /// </summary>
    private static async Task<List<RemexMessage>> RunLoopbackConnectionAsync(
        IFileTrustService trust,
        string awaitedResponseType,
        Remex.Agent.Services.FileTransfer.TransferSessionManager? sessions,
        params RemexMessage[] script)
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
            sessions!,  // TransferSessionManager: null unless a test drives the control plane
            null!,  // PairedClientRegistry: only the reconnect challenge reads it, and !isPaired gates that
            null!,  // FilePushOriginator: this test never pushes FROM the host
            new ClientSessionRegistry(),
            NewNameStore(),
            NewActivityStore());

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
    public async Task ALoopbackFilePushOfferIsNotAnsweredAtAll_TheDoorIsGone()
    {
        // THIS TEST CHANGED SHAPE AT RemEx-e11w, and the reason matters more than the assertion.
        //
        // It used to prove that a loopback connection claiming PhoneClientId could not push files by
        // riding that phone's auto-accept-incoming grant — a guarded door. RemEx-e11w removed the door:
        // a phone-initiated push IS an upload, so the file_push_offer consent handshake is gone from
        // this side entirely and nothing dispatches the message any more.
        //
        // A deleted attack surface still deserves a test, because the way it comes back is somebody
        // re-adding a handler without re-reading the decision. Asserting SILENCE pins that: if this
        // ever fails, an inbound file_push_offer is being answered again, and whoever did it owes
        // RemEx-e11w a fresh look — including RemEx-j63q (a blank clientId reaching RequestConsentAsync)
        // and RemEx-u64l (a host-side exception reported to the phone as a user deny), both of which
        // lived in the deleted handler and would come back with it.
        //
        // The real upload path is unaffected and is where this scenario's protection now lives:
        // file_transfer_offer on /ws/files, gated by pairing and by ResolveForWrite (IsWritable,
        // path-escape, size cap). RemEx-4215 and RemEx-4u0d hold the loopback-identity line there.
        var trust = NewGenerousTrustService();

        var sent = await RunLoopbackConnectionAsync(
            trust.Object,
            MessageTypes.Pong,
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
            },
            // Ping second, so the connection has a reason to answer something. If a push response
            // were still being produced it would be in `sent` alongside the pong, and an assertion
            // on an empty list could not tell "refused" from "the harness never ran".
            new RemexMessage { Type = MessageTypes.Ping, ProtocolVersion = ProtocolVersionPolicy.Current });

        Assert.Contains(sent, m => m.Type == MessageTypes.Pong);
        Assert.DoesNotContain(sent, m => m.Type == MessageTypes.FilePushResponse);

        // HONEST LIMIT, measured rather than assumed (review). This catches a SYNCHRONOUS re-add,
        // which is the realistic regression — the arm that was deleted dispatched inline, and
        // RunDetachedAsync is a bare `await handler()`. It would NOT reliably catch a re-add that
        // genuinely suspends (a real prompt, a real FileTrustService instead of this mock): the
        // socket stops waiting once it has seen the Pong, so a slow response could land after the
        // snapshot and the guard would go green with the door open. Passing
        // MessageTypes.FilePushResponse as awaitedResponseType above closes that hole completely —
        // the socket then waits its full timeout for a reply that never comes — at a cost of ~10s
        // on every run of a 51s suite. Not paid, on purpose; change it here if the balance shifts.

        // Nothing was asked of the trust store either — there is no consent kind left to ask about.
        trust.Verify(
            t => t.RequestConsentAsync(It.IsAny<string>(), It.IsAny<FileConsentRequest>(), It.IsAny<CancellationToken>()),
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

    /// <summary>A throwaway activity store; these tests never read a date back.</summary>
    private static PairedDeviceActivityStore NewActivityStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

    [Fact]
    public async Task ALoopbackConnectionCannotCancelAPairedPhonesTransfer()
    {
        // THE CALL SITE, not the guard. TransferSessionManagerTests proves HandleControl refuses a
        // foreign key; this proves PingPongHandler actually HANDS it the connection's identity. Change
        // the dispatch to pass a constant, or the wrong variable, and the guard is still perfectly
        // correct and completely useless - which is the failure RemEx-0719's review caught one bead
        // earlier, where four predicate tests stayed green with the call site deleted (RemEx-juas).
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            var transferService = Mock.Of<IFileTransferService>();
            var volumes = new Remex.Agent.Services.FileTransfer.VolumeEnumerator(
                NullLogger<Remex.Agent.Services.FileTransfer.VolumeEnumerator>.Instance);
            var trust = NewGenerousTrustService();
            using var sessions = new Remex.Agent.Services.FileTransfer.TransferSessionManager(
                NullLogger<Remex.Agent.Services.FileTransfer.TransferSessionManager>.Instance,
                files,
                new Remex.Agent.Services.FileTransfer.SharedRootReadResolver(transferService, trust.Object, volumes),
                staging.FullName);

            // The victim: a real, live inbound transfer owned by the paired phone.
            var tid = Guid.NewGuid().ToString("N");
            await sessions.BeginReceiveAsync(
                PhoneClientId,
                new FileTransferOffer
                {
                    TransferId = tid,
                    Mode = "upload",
                    SourcePath = "/phone/DCIM/photo.bin",
                    DestRoot = "root-a",
                    DestRelativePath = null,
                    FileName = "photo.bin",
                    Size = 4096,
                    ResumeRequested = false,
                },
                default);
            await sessions.WriteChunkAsync(tid, 0, new byte[128], default);

            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            Assert.True(File.Exists(partial), "the victim's transfer should have staged normally");

            // The attacker: a loopback connection, which RemEx-4215 freezes at NO identity, naming the
            // phone's id on the message and cancelling by transfer id. One message, no pairing.
            // HostInfo, not FileTransferResult: a file_transfer_control produces NO reply, so awaiting a
            // response type that never arrives burned the harness's full 10s timeout on every run.
            // HostInfo is sent on connect, so the wait ends as soon as the script has been consumed.
            await RunLoopbackConnectionAsync(
                trust.Object,
                MessageTypes.HostInfo,
                sessions,
                new RemexMessage
                {
                    Type = MessageTypes.FileTransferControl,
                    ProtocolVersion = ProtocolVersionPolicy.Current,
                    ClientId = PhoneClientId,
                    FileTransferControl = new FileTransferControl
                    {
                        TransferId = tid,
                        Action = FileTransferControlActions.Cancel,
                    },
                });

            Assert.True(
                File.Exists(partial),
                "a loopback connection must not be able to cancel a paired phone's transfer by naming its id");

            // POSITIVE CONTROL, and it is not optional. File.Exists surviving is equally satisfied by
            // "the guard refused it" and by "the message never reached HandleControl at all" - so
            // without this, deleting `case MessageTypes.FileTransferControl` from the dispatch would
            // leave this test green while cancel silently stopped working for real phones. Found in
            // review; it is the same class of gap as the one RemEx-0719 was failed for.
            var ownTid = Guid.NewGuid().ToString("N");
            await sessions.BeginReceiveAsync(
                string.Empty,
                new FileTransferOffer
                {
                    TransferId = ownTid,
                    Mode = "upload",
                    SourcePath = "/local/tool/scratch.bin",
                    DestRoot = "root-a",
                    DestRelativePath = null,
                    FileName = "scratch.bin",
                    Size = 4096,
                    ResumeRequested = false,
                },
                default);
            await sessions.WriteChunkAsync(ownTid, 0, new byte[128], default);

            var ownPartial = Path.Combine(staging.FullName, ownTid + ".remexpart");
            Assert.True(File.Exists(ownPartial));

            await RunLoopbackConnectionAsync(
                trust.Object,
                MessageTypes.HostInfo,
                sessions,
                new RemexMessage
                {
                    Type = MessageTypes.FileTransferControl,
                    ProtocolVersion = ProtocolVersionPolicy.Current,
                    FileTransferControl = new FileTransferControl
                    {
                        TransferId = ownTid,
                        Action = FileTransferControlActions.Cancel,
                    },
                });

            Assert.False(
                File.Exists(ownPartial),
                "the loopback connection's cancel of its OWN transfer must still land - otherwise the "
                + "assertion above proves only that nothing was delivered");
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }
}
