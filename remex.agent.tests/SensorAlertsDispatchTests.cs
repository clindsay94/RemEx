using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Alerts;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.Alerts;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of the phone's sensor alert requests (RemEx-pp4cm.12): a proven, non-loopback phone's
/// <c>sensor_alerts_get</c> / <c>sensor_alert_set</c> / <c>sensor_alert_remove</c> reach the desktop with
/// the proven client id; loopback and unauthenticated senders do not; and a malformed one is dropped
/// without ending the session, and answered with a fresh rule list so the phone's edit is put back.
/// </summary>
/// <remarks>
/// Driven through the real <c>HandleAsync</c> with a real reconnect-proof handshake, the same shape as
/// <c>HomePinsDispatchTests</c>: the request is gated on authentication, and the only way to show that
/// gate is to cross it. The desktop is a recording double; the real one is covered in
/// <c>remex.desktop.tests</c>.
/// </remarks>
public class SensorAlertsDispatchTests
{
    private const string ClientId = "phone-1";

    private sealed class RecordingAlerts : IPhoneSensorAlerts
    {
        public List<PhoneSensorAlertRequest> Requests { get; } = [];

        public event Action<PhoneSensorAlertRequest>? PhoneRequested;

        public Task PublishFiredAsync(SensorAlertFiredEvent fired) => Task.CompletedTask;

        public Task PublishRulesAsync(IReadOnlyList<SensorAlertRule> rules) => Task.CompletedTask;

        public void RequestFromPhone(PhoneSensorAlertRequest request)
        {
            lock (Requests) Requests.Add(request);
            PhoneRequested?.Invoke(request);
        }

        public List<PhoneSensorAlertRequest> Snapshot()
        {
            lock (Requests) return [.. Requests];
        }
    }

    /// <summary>A socket that plays a script one receive at a time and records every message the host sends.</summary>
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

    private static Func<InteractiveWebSocket, Task<string?>> SendRaw(string json) =>
        _ => Task.FromResult<string?>(json);

    /// <summary>The two steps of a real PAIR-1 reconnect: a clientId-bearing ping, then the proof.</summary>
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

    private static RemexMessage Set(string name = "CPU Package", double threshold = 90) => new()
    {
        Type = MessageTypes.SensorAlertSet,
        ClientId = ClientId,
        SensorAlertChange = new SensorAlertChange
        {
            SensorName = name, Threshold = threshold, Direction = AlertDirection.Above, Severity = AlertSeverity.Critical,
        },
    };

    private static RemexMessage Remove(string name = "CPU Package") => new()
    {
        Type = MessageTypes.SensorAlertRemove,
        ClientId = ClientId,
        SensorAlertRemoval = new SensorAlertRemoval { SensorName = name },
    };

    private static RemexMessage Get() => new() { Type = MessageTypes.SensorAlertsGet, ClientId = ClientId };

    [Fact]
    public async Task AProvenPhonesSetRemoveAndGetReachTheDesktopWithItsClientId()
    {
        var alerts = new RecordingAlerts();
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket([.. Authenticate(secret), Send(Set()), Send(Remove("GPU Temp")), Send(Get())]);

        await RunAsync(socket, alerts, secret, isLoopback: false);

        var requests = alerts.Snapshot();
        Assert.Equal(
            [PhoneSensorAlertRequestKind.Set, PhoneSensorAlertRequestKind.Remove, PhoneSensorAlertRequestKind.Get],
            requests.Select(r => r.Kind));
        Assert.All(requests, r => Assert.Equal(ClientId, r.ClientId));
        Assert.Equal("CPU Package", requests[0].Change!.SensorName);
        Assert.Equal(90, requests[0].Change!.Threshold);
        Assert.Equal(AlertSeverity.Critical, requests[0].Change!.Severity);
        Assert.Equal("GPU Temp", requests[1].SensorName);
    }

    [Fact]
    public async Task LoopbackCannotAskForOrChangeRules()
    {
        var alerts = new RecordingAlerts();
        var socket = new InteractiveWebSocket(
            Send(new RemexMessage { Type = MessageTypes.Ping, Timestamp = 1 }), Send(Set()), Send(Remove()), Send(Get()));

        await RunAsync(socket, alerts, RandomNumberGenerator.GetBytes(32), isLoopback: true);

        Assert.Empty(alerts.Snapshot());
    }

    [Fact]
    public async Task ARequestBeforeAuthenticationNeverReachesTheDesktop()
    {
        var alerts = new RecordingAlerts();
        var socket = new InteractiveWebSocket(Send(Set()), Send(Remove()), Send(Get()));

        await RunAsync(socket, alerts, RandomNumberGenerator.GetBytes(32), isLoopback: false);

        Assert.Empty(alerts.Snapshot());
    }

    [Fact]
    public async Task AMalformedSetIsDroppedAnsweredWithAGetAndTheSessionCarriesOn()
    {
        var alerts = new RecordingAlerts();
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [
                .. Authenticate(secret),
                Send(Set(name: "   ")),
                Send(Set(name: "CPU\u0007Temp")),
                Send(Set(threshold: double.MaxValue)),
                Send(new RemexMessage { Type = MessageTypes.SensorAlertSet, ClientId = ClientId }),
                Send(Remove(name: " ")),
                Send(Set(name: "GPU Temp", threshold: 85)),
            ]);

        await RunAsync(socket, alerts, secret, isLoopback: false);

        var requests = alerts.Snapshot();
        // Five refused requests each become a Get (the phone gets the unchanged rules back), then the good one.
        Assert.Equal(5, requests.Count(r => r.Kind == PhoneSensorAlertRequestKind.Get));
        var set = Assert.Single(requests, r => r.Kind == PhoneSensorAlertRequestKind.Set);
        Assert.Equal("GPU Temp", set.Change!.SensorName);
        Assert.DoesNotContain(requests, r => r.Kind == PhoneSensorAlertRequestKind.Remove);
    }

    [Fact]
    public async Task ABurstOfRequestsPastTheSessionGateIsDroppedAndRepublishedAsGets()
    {
        var alerts = new RecordingAlerts();
        var secret = RandomNumberGenerator.GetBytes(32);
        var burst = PhoneSensorAlertGate.MaxRequestsPerWindow + 15;
        var script = Authenticate(secret).Concat(Enumerable.Range(0, burst).Select(i => Send(Set(threshold: 50 + i))));
        var socket = new InteractiveWebSocket([.. script]);

        await RunAsync(socket, alerts, secret, isLoopback: false);

        var requests = alerts.Snapshot();
        Assert.Equal(PhoneSensorAlertGate.MaxRequestsPerWindow, requests.Count(r => r.Kind == PhoneSensorAlertRequestKind.Set));
        // Every refused Set still reaches the desktop as a Get, so the phone's optimistic edit is put back
        // (the desktop coalesces those into one publish).
        Assert.Equal(15, requests.Count(r => r.Kind == PhoneSensorAlertRequestKind.Get));
        Assert.All(requests.Where(r => r.Kind == PhoneSensorAlertRequestKind.Set).Select((r, i) => (r, i)), x =>
            Assert.Equal(50 + x.i, x.r.Change!.Threshold));
    }

    [Fact]
    public async Task MalformedJsonInARequestDoesNotDropTheSession()
    {
        var alerts = new RecordingAlerts();
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [
                .. Authenticate(secret),
                SendRaw("""{"type":"sensor_alert_set","protocolVersion":2,"clientId":"phone-1","sensorAlertChange":"garbage"}"""),
                SendRaw("""{"type":"sensor_alert_set","protocolVersion":2,"clientId":"phone-1","sensorAlertChange":{"sensorName":"CPU","threshold":"hot","direction":"Above","severity":"Warning"}}"""),
                SendRaw("""{"type":"sensor_alert_set","protocolVersion":2,"clientId":"phone-1","sensorAlertChange":{"sensorName":"CPU","threshold":90,"direction":"Sideways","severity":"Warning"}}"""),
                SendRaw("""{"type":"sensor_alert_set","protocolVersion":2,"clientId":"phone-1","sensorAlertChange":{"sensorName":"GPU Temp","threshold":70.5,"direction":"Below","severity":"Warning"}}"""),
            ]);

        await RunAsync(socket, alerts, secret, isLoopback: false);

        var set = Assert.Single(alerts.Snapshot(), r => r.Kind == PhoneSensorAlertRequestKind.Set);
        Assert.Equal("GPU Temp", set.Change!.SensorName);
        Assert.Equal(AlertDirection.Below, set.Change.Direction);
        Assert.Equal(70.5, set.Change.Threshold);
    }

    [Fact]
    public void TheHostAdvertisesSensorAlerts()
    {
        var provider = new HostCapabilitiesProvider(
            new FakeScreenCaptureService(), Mock.Of<IInputSimulationService>(), () => "0A:1B:2C:3D:4E:5F");

        Assert.True(provider.GetCurrent().SupportsSensorAlerts);
    }

    private static async Task RunAsync(
        InteractiveWebSocket socket, RecordingAlerts alerts, byte[] secret, bool isLoopback)
    {
        var registryPath = Path.Combine(Directory.CreateTempSubdirectory("remex-sensoralerts-").FullName, "paired_clients.json");
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
            NewNameStore(),
            NewActivityStore(),
            new FakeHostClipboard(),
            Mock.Of<IMediaSessionMonitor>(),
            new Remex.Agent.Services.Theme.PhoneThemeSnapshotStore(),
            phoneSensorAlerts: alerts);

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

    private static PairedClientNameStore NewNameStore() =>
        new(NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json"));

    private static PairedDeviceActivityStore NewActivityStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
}
