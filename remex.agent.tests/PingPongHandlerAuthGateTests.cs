using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Agent.Services.Theme;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The pairing gate on the <c>/ws</c> control channel as a remote peer meets it: what an
/// unauthenticated or only-claimed identity is refused, that a paired remote still cannot rewrite the
/// launcher allowlist, and that a bad frame or an old protocol ends the session instead of being
/// served. Driven through the real <c>HandleAsync</c>; the per-feature dispatch suites only exercise
/// the gate incidentally.
/// </summary>
public sealed class PingPongHandlerAuthGateTests
{
    private const string ClientId = "phone-1";

    private sealed class GateSocket(params Func<GateSocket, string?>[] script) : WebSocket
    {
        private int _next;
        private readonly List<RemexMessage> _sent = [];

        public List<RemexMessage> Sent
        {
            get { lock (_sent) return [.. _sent]; }
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
            {
                lock (_sent) _sent.Add(message);
            }

            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            while (_next < script.Length)
            {
                var json = script[_next++](this);
                if (json is null)
                {
                    continue;
                }

                var payload = System.Text.Encoding.UTF8.GetBytes(json);
                payload.AsSpan().CopyTo(b.AsSpan());
                return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
            }

            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
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

    private static string Json(RemexMessage message) =>
        System.Text.Encoding.UTF8.GetString(MessageSerializer.Serialize(message));

    private static Func<GateSocket, string?> Send(RemexMessage message) => _ => Json(message);

    private static Func<GateSocket, string?> SendRaw(string json) => _ => json;

    private static RemexMessage Ping(string? clientId = null) =>
        new() { Type = MessageTypes.Ping, ClientId = clientId, Timestamp = 7 };

    private static Func<GateSocket, string?> Proof(byte[] secret) => socket =>
    {
        var challenge = socket.Sent.Last(m => m.Type == MessageTypes.ReconnectChallenge);
        var nonce = Convert.FromBase64String(challenge.ReconnectChallenge!.NonceBase64);
        return Json(new RemexMessage
        {
            Type = MessageTypes.ReconnectProof,
            ClientId = ClientId,
            ReconnectProof = new ReconnectProof
            {
                ClientId = ClientId,
                ProofHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(secret, nonce)),
            },
        });
    };

    private static RemexMessage ThemeSync(string? correlationId = null) => new()
    {
        Type = MessageTypes.ThemeSync,
        ClientId = ClientId,
        CorrelationId = correlationId,
        ThemeSync = new PhoneThemeSnapshot
        {
            SeedHex = "#6750A4",
            Style = "tonal_spot",
            Mode = "dark",
            Contrast = 0.5,
            DynamicColor = false,
            SentAtUnixMs = 1_700_000_000_000,
        },
    };

    private sealed class Rig
    {
        public PhoneThemeSnapshotStore Theme { get; } = new();
        public FakeHostClipboard Clipboard { get; } = new();
        public Mock<ILauncherStorageService> Launcher { get; } = new();
        public byte[] Secret { get; } = RandomNumberGenerator.GetBytes(32);
    }

    private static async Task RunAsync(Rig rig, GateSocket socket, bool isLoopback = false, bool registerClient = true)
    {
        var dir = Directory.CreateTempSubdirectory("remex-authgate-");
        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(dir.FullName, "paired_clients.json"));
        if (registerClient)
        {
            registry.RegisterClient(ClientId, rig.Secret);
        }

        var telemetry = new TelemetryBackgroundService(
            Mock.Of<ITelemetryService>(), NullLogger<TelemetryBackgroundService>.Instance);
        var fileSvc = Mock.Of<Remex.Core.Services.FileTransfer.IFileTransferService>();
        var trust = Mock.Of<Remex.Core.Services.FileTransfer.IFileTrustService>();
        var volumes = new Remex.Agent.Services.FileTransfer.VolumeEnumerator(
            NullLogger<Remex.Agent.Services.FileTransfer.VolumeEnumerator>.Instance);

        var handler = new PingPongHandler(
            NullLogger<PingPongHandler>.Instance,
            telemetry,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            rig.Launcher.Object,
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,
            null!,
            new FileTransferHandler(
                NullLogger<FileTransferHandler>.Instance, fileSvc, trust, volumes,
                new Remex.Agent.Services.FileTransfer.SharedRootReadResolver(fileSvc, trust, volumes)),
            null!,
            registry,
            null!,
            new ClientSessionRegistry(),
            new PairedClientNameStore(
                NullLogger<PairedClientNameStore>.Instance,
                Path.Combine(dir.FullName, "names.json")),
            new PairedDeviceActivityStore(
                NullLogger<PairedDeviceActivityStore>.Instance,
                Path.Combine(dir.FullName, "activity.json")),
            rig.Clipboard,
            Mock.Of<IMediaSessionMonitor>(),
            rig.Theme);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await handler.HandleAsync(
                socket,
                isLoopback,
                isTrustedForPinAutoFetch: false,
                remoteAddress: isLoopback ? "127.0.0.1" : "192.168.1.50",
                cts.Token);
        }
        finally
        {
            handler.Dispose();
            try { dir.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }

    private static RemexMessage OnlyRefusal(GateSocket socket) =>
        Assert.Single(socket.Sent, m => m.Type == MessageTypes.CommandResponse);

    [Fact]
    public async Task UnpairedRemote_GatedMessage_IsRefusedWithCorrelationAndNothingIsApplied()
    {
        var rig = new Rig();
        var socket = new GateSocket(Send(ThemeSync(correlationId: "corr-9")));

        await RunAsync(rig, socket, registerClient: false);

        var refusal = OnlyRefusal(socket);
        Assert.False(refusal.CommandSuccess);
        Assert.Contains("Pairing required", refusal.CommandMessage);
        Assert.Equal("corr-9", refusal.CorrelationId);
        Assert.Null(rig.Theme.Latest);
    }

    [Fact]
    public async Task UnpairedRemote_Refusal_DoesNotEndTheSession()
    {
        var rig = new Rig();
        var socket = new GateSocket(Send(ThemeSync()), Send(Ping()));

        await RunAsync(rig, socket, registerClient: false);

        Assert.Single(socket.Sent, m => m.Type == MessageTypes.CommandResponse);
        var pong = Assert.Single(socket.Sent, m => m.Type == MessageTypes.Pong);
        Assert.Equal(7, pong.Timestamp);
    }

    [Fact]
    public async Task UnpairedRemote_ClipboardPush_IsRefusedOnBothAnswerPathsAndNeverWritten()
    {
        var rig = new Rig();
        var socket = new GateSocket(Send(new RemexMessage
        {
            Type = MessageTypes.ClipboardPush,
            ClientId = ClientId,
            CorrelationId = "push-1",
            ClipboardPush = new ClipboardPush { Text = "hello" },
        }));

        await RunAsync(rig, socket, registerClient: false);

        Assert.Equal("push-1", OnlyRefusal(socket).CorrelationId);
        var result = Assert.Single(socket.Sent, m => m.Type == MessageTypes.ClipboardPushResult);
        Assert.Equal("refused", result.ClipboardPushResult!.Reason);
        Assert.Equal("push-1", result.CorrelationId);
        Assert.Equal(0, rig.Clipboard.WriteCount);
    }

    [Fact]
    public async Task ABareClaimedPairedClientId_DoesNotAuthenticate()
    {
        var rig = new Rig();
        var socket = new GateSocket(Send(Ping(ClientId)), Send(ThemeSync()));

        await RunAsync(rig, socket);

        Assert.Single(socket.Sent, m => m.Type == MessageTypes.ReconnectChallenge);
        Assert.Contains("Pairing required", OnlyRefusal(socket).CommandMessage);
        Assert.Null(rig.Theme.Latest);
    }

    [Fact]
    public async Task AProofFromTheWrongSecret_LeavesTheConnectionUnpaired()
    {
        var rig = new Rig();
        var socket = new GateSocket(
            Send(Ping(ClientId)),
            Proof(RandomNumberGenerator.GetBytes(32)),
            Send(ThemeSync()));

        await RunAsync(rig, socket);

        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.ReconnectResult);
        Assert.Contains("Pairing required", OnlyRefusal(socket).CommandMessage);
        Assert.Null(rig.Theme.Latest);
    }

    [Fact]
    public async Task AProofWithNoOutstandingChallenge_LeavesTheConnectionUnpaired()
    {
        var rig = new Rig();
        // Never pinged with a known id, so no challenge exists to answer; a precomputed proof is worthless.
        var forged = Json(new RemexMessage
        {
            Type = MessageTypes.ReconnectProof,
            ClientId = ClientId,
            ReconnectProof = new ReconnectProof
            {
                ClientId = ClientId,
                ProofHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(rig.Secret, new byte[32])),
            },
        });
        var socket = new GateSocket(SendRaw(forged), Send(ThemeSync()));

        await RunAsync(rig, socket);

        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.ReconnectResult);
        Assert.Contains("Pairing required", OnlyRefusal(socket).CommandMessage);
        Assert.Null(rig.Theme.Latest);
    }

    [Fact]
    public async Task AProofIsSingleUse_AFailedAttemptConsumesTheChallenge()
    {
        var rig = new Rig();
        // The wrong proof burns the nonce, so the correct proof for it afterwards must not be accepted.
        string? nonceB64 = null;
        var socket = new GateSocket(
            Send(Ping(ClientId)),
            s =>
            {
                nonceB64 = s.Sent.Last(m => m.Type == MessageTypes.ReconnectChallenge).ReconnectChallenge!.NonceBase64;
                return Json(new RemexMessage
                {
                    Type = MessageTypes.ReconnectProof,
                    ClientId = ClientId,
                    ReconnectProof = new ReconnectProof { ClientId = ClientId, ProofHmacBase64 = "AAAA" },
                });
            },
            _ => Json(new RemexMessage
            {
                Type = MessageTypes.ReconnectProof,
                ClientId = ClientId,
                ReconnectProof = new ReconnectProof
                {
                    ClientId = ClientId,
                    ProofHmacBase64 = Convert.ToBase64String(
                        HMACSHA256.HashData(rig.Secret, Convert.FromBase64String(nonceB64!))),
                },
            }),
            Send(ThemeSync()));

        await RunAsync(rig, socket);

        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.ReconnectResult);
        Assert.Null(rig.Theme.Latest);
    }

    [Fact]
    public async Task AValidProof_UnlocksGatedMessagesThatWereRefusedBefore()
    {
        var rig = new Rig();
        var socket = new GateSocket(
            Send(Ping(ClientId)),
            Send(ThemeSync()),
            Proof(rig.Secret),
            Send(ThemeSync()));

        await RunAsync(rig, socket);

        Assert.Single(socket.Sent, m => m.Type == MessageTypes.CommandResponse);
        Assert.Single(socket.Sent, m => m.Type == MessageTypes.ReconnectResult);
        Assert.NotNull(rig.Theme.Latest);
    }

    [Theory]
    [InlineData(MessageTypes.LauncherAdd)]
    [InlineData(MessageTypes.LauncherRemove)]
    [InlineData(MessageTypes.LauncherSync)]
    public async Task PairedRemote_CannotMutateTheLauncherAllowlist(string type)
    {
        var rig = new Rig();
        var socket = new GateSocket(
            Send(Ping(ClientId)),
            Proof(rig.Secret),
            Send(new RemexMessage { Type = type, ClientId = ClientId, CorrelationId = "l-1" }));

        await RunAsync(rig, socket);

        Assert.Single(socket.Sent, m => m.Type == MessageTypes.ReconnectResult);
        var refusal = OnlyRefusal(socket);
        Assert.False(refusal.CommandSuccess);
        Assert.Contains("only be changed on the PC itself", refusal.CommandMessage);
        Assert.Equal("l-1", refusal.CorrelationId);
        rig.Launcher.Verify(l => l.SaveEntriesAsync(It.IsAny<IEnumerable<AppEntry>>()), Times.Never);
    }

    [Fact]
    public async Task UnpairedRemote_LauncherMutation_IsRefusedAsUnpairedBeforeAnythingElse()
    {
        var rig = new Rig();
        var socket = new GateSocket(Send(new RemexMessage { Type = MessageTypes.LauncherAdd, ClientId = ClientId }));

        await RunAsync(rig, socket, registerClient: false);

        Assert.Contains("Pairing required", OnlyRefusal(socket).CommandMessage);
        rig.Launcher.Verify(l => l.SaveEntriesAsync(It.IsAny<IEnumerable<AppEntry>>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LegacyProtocolVersion_IsRefusedAndTheSessionEnds(int version)
    {
        var rig = new Rig();
        var socket = new GateSocket(
            SendRaw($$"""{"type":"ping","protocolVersion":{{version}},"correlationId":"v-1"}"""),
            Send(Ping()));

        await RunAsync(rig, socket);

        var refusal = OnlyRefusal(socket);
        Assert.False(refusal.CommandSuccess);
        Assert.Contains("not supported", refusal.CommandMessage);
        Assert.Equal("v-1", refusal.CorrelationId);
        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.Pong);
    }

    [Fact]
    public async Task ALegacyVersionOnLoopback_IsAlsoRefused()
    {
        var rig = new Rig();
        var socket = new GateSocket(SendRaw("""{"type":"ping","protocolVersion":1}"""), Send(Ping()));

        await RunAsync(rig, socket, isLoopback: true);

        Assert.Single(socket.Sent, m => m.Type == MessageTypes.CommandResponse);
        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.Pong);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"type\":")]
    [InlineData("[1,2,3]")]
    public async Task AnUnparseableFrame_EndsTheSessionBeforeLaterMessagesAreServed(string frame)
    {
        var rig = new Rig();
        var socket = new GateSocket(SendRaw(frame), Send(Ping()), Send(ThemeSync()));

        await RunAsync(rig, socket, isLoopback: true);

        Assert.DoesNotContain(socket.Sent, m => m.Type is MessageTypes.Pong or MessageTypes.CommandResponse);
        Assert.Null(rig.Theme.Latest);
    }
}
