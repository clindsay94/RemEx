using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Diagnostics;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of the phone's PC logs and diagnostics screen (RemEx-pp4cm.13), driven through the real
/// <c>PingPongHandler.HandleAsync</c> so the switch that routes the two request types to the service is
/// itself under test: a proven non-loopback phone is answered (redacted), loopback is refused, an
/// unpaired connection gets nothing, and a burst is rate-limited.
/// </summary>
public class PhoneDiagnosticsDispatchTests
{
    private const string ClientId = "phone-1";

    private sealed class InteractiveWebSocket(params Func<InteractiveWebSocket, Task<string?>>[] script) : WebSocket
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

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            while (_next < script.Length)
            {
                var json = await script[_next++](this);
                if (json is null)
                {
                    continue;
                }

                var payload = System.Text.Encoding.UTF8.GetBytes(json);
                payload.CopyTo(b.Array.AsSpan(b.Offset));
                return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true);
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

    private static string Json(RemexMessage message) =>
        System.Text.Encoding.UTF8.GetString(MessageSerializer.Serialize(message));

    private static Func<InteractiveWebSocket, Task<string?>> Send(RemexMessage message) =>
        _ => Task.FromResult<string?>(Json(message));

    private static Func<InteractiveWebSocket, Task<string?>>[] Authenticate(byte[] secret) =>
    [
        Send(new RemexMessage { Type = MessageTypes.Ping, ClientId = ClientId, Timestamp = 1 }),
        socket =>
        {
            var challenge = socket.Sent.Last(m => m.Type == MessageTypes.ReconnectChallenge);
            var nonce = Convert.FromBase64String(challenge.ReconnectChallenge!.NonceBase64);
            return Task.FromResult<string?>(Json(new RemexMessage
            {
                Type = MessageTypes.ReconnectProof,
                ClientId = ClientId,
                ReconnectProof = new ReconnectProof
                {
                    ClientId = ClientId,
                    ProofHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(secret, nonce)),
                },
            }));
        },
    ];

    private static RemexMessage LogsGet(string correlationId) => new()
    {
        Type = MessageTypes.DiagnosticLogsGet,
        ClientId = ClientId,
        CorrelationId = correlationId,
        DiagnosticLogsRequest = new DiagnosticLogsRequest { MinLevel = "trace", Max = 500 },
    };

    private static PhoneDiagnosticsService NewService(params LogEntry[] entries) =>
        new(
            () => entries,
            () => entries.Length == 0 ? 0 : entries[^1].Seq,
            readiness: null,
            () => new HostCapabilities { Version = "3.0.0" },
            () => TimeSpan.FromMinutes(5),
            TimeProvider.System);

    private static LogEntry Secretive() =>
        new(DateTime.UtcNow, LogLevel.Warning, "Remex.Test", "phone 10.1.2.3 sent token=abc123SECRET", null) { Seq = 1 };

    [Fact]
    public async Task AProvenPairedPhoneIsAnsweredWithRedactedEntriesAndItsCorrelationId()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket([.. Authenticate(secret), Send(LogsGet("c-1"))]);

        await RunAsync(socket, NewService(Secretive()), secret, isLoopback: false);

        var reply = Assert.Single(socket.Sent, m => m.Type == MessageTypes.DiagnosticLogsResult);
        Assert.Equal("c-1", reply.CorrelationId);
        Assert.Null(reply.DiagnosticLogsResponse!.Error);
        var line = Assert.Single(reply.DiagnosticLogsResponse.Entries).Message;
        Assert.DoesNotContain("10.1.2.3", line);
        Assert.DoesNotContain("abc123SECRET", line);
    }

    [Fact]
    public async Task ALoopbackConnectionIsRefusedRatherThanServed()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(Send(LogsGet("c-2")));

        await RunAsync(socket, NewService(Secretive()), secret, isLoopback: true);

        var reply = Assert.Single(socket.Sent, m => m.Type == MessageTypes.DiagnosticLogsResult);
        Assert.Equal("refused", reply.DiagnosticLogsResponse!.Error);
        Assert.Empty(reply.DiagnosticLogsResponse.Entries);
    }

    [Fact]
    public async Task AnUnpairedConnectionGetsNoLogsAtAll()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(Send(LogsGet("c-3")));

        await RunAsync(socket, NewService(Secretive()), secret, isLoopback: false);

        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.DiagnosticLogsResult && m.DiagnosticLogsResponse?.Entries.Count > 0);
        Assert.Contains(socket.Sent, m => m.Type == MessageTypes.CommandResponse && m.CommandSuccess == false);
    }

    [Fact]
    public async Task ABurstOnOneSessionIsRateLimited()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [.. Authenticate(secret), Send(LogsGet("a")), Send(LogsGet("b")), Send(LogsGet("c"))]);

        await RunAsync(socket, NewService(Secretive()), secret, isLoopback: false);

        var replies = socket.Sent.Where(m => m.Type == MessageTypes.DiagnosticLogsResult).ToList();
        Assert.Equal(3, replies.Count);
        Assert.Null(replies[0].DiagnosticLogsResponse!.Error);
        Assert.Equal("rate_limited", replies[1].DiagnosticLogsResponse!.Error);
        Assert.Equal("rate_limited", replies[2].DiagnosticLogsResponse!.Error);
    }

    [Fact]
    public async Task TheSummaryRequestIsRoutedToo()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [.. Authenticate(secret), Send(new RemexMessage { Type = MessageTypes.DiagnosticSummaryGet, ClientId = ClientId, CorrelationId = "s" })]);

        await RunAsync(socket, NewService(), secret, isLoopback: false);

        var reply = Assert.Single(socket.Sent, m => m.Type == MessageTypes.DiagnosticSummaryResult);
        Assert.Equal("s", reply.CorrelationId);
        Assert.Contains(reply.DiagnosticSummaryResponse!.Items, i => i.Key == "version" && i.Detail == "3.0.0");
    }

    private static async Task RunAsync(InteractiveWebSocket socket, PhoneDiagnosticsService service, byte[] secret, bool isLoopback)
    {
        var registryPath = Path.Combine(Directory.CreateTempSubdirectory("remex-diag-").FullName, "paired_clients.json");
        var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, registryPath);
        registry.RegisterClient(ClientId, secret);

        var telemetry = new TelemetryBackgroundService(
            Mock.Of<ITelemetryService>(),
            NullLogger<TelemetryBackgroundService>.Instance);

        var handler = new PingPongHandler(
            NullLogger<PingPongHandler>.Instance,
            telemetry,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,
            null!,
            NewFileTransferHandler(),
            null!,
            registry,
            null!,
            new ClientSessionRegistry(),
            new PairedClientNameStore(NullLogger<PairedClientNameStore>.Instance,
                Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json")),
            new PairedDeviceActivityStore(NullLogger<PairedDeviceActivityStore>.Instance,
                Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())),
            new FakeHostClipboard(),
            Mock.Of<IMediaSessionMonitor>(),
            new Remex.Agent.Services.Theme.PhoneThemeSnapshotStore(),
            phoneDiagnostics: service);

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
            try { Directory.Delete(Path.GetDirectoryName(registryPath)!, recursive: true); } catch { /* best-effort */ }
        }
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
}
