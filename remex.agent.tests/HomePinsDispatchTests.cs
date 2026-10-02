using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Home;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of the PC Home's pinned-sensor sync (RemEx-wqo7a.5): an authenticated phone is sent
/// <c>home_pins_sync</c> once it has proved who it is and again on every change; loopback never is; a
/// valid <c>home_pins_change</c> reaches the store, and a malformed one is dropped without ending the
/// session.
/// </summary>
/// <remarks>
/// Driven through the real <c>HandleAsync</c> with a real reconnect-proof handshake, the same shape
/// as <c>ThemeSyncDispatchTests</c> but non-loopback: the sync is gated on authentication, and the
/// only way to show that gate is to cross it. Neither message has a reply, so a routing mistake here
/// would show up as pins that silently never move.
/// </remarks>
public class HomePinsDispatchTests
{
    private const string ClientId = "phone-1";

    /// <summary>
    /// A socket that plays a script one receive at a time and records every message the host sends.
    /// Each step sees what has been sent so far, which is how the reconnect proof answers the
    /// challenge nonce the host just issued. A step returning null skips the receive and moves on.
    /// </summary>
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

    private static Func<InteractiveWebSocket, Task<string?>> Do(Action action) =>
        _ =>
        {
            action();
            return Task.FromResult<string?>(null);
        };

    private static Func<InteractiveWebSocket, Task<string?>> WaitFor(Func<InteractiveWebSocket, bool> condition) =>
        async socket =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition(socket) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            return null;
        };

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

    private static RemexMessage Change(string name, bool pinned) => new()
    {
        Type = MessageTypes.HomePinsChange,
        ClientId = ClientId,
        HomePinChange = new HomePinChange { SensorName = name, Pinned = pinned },
    };

    private static List<HomePinnedSensors> Syncs(InteractiveWebSocket socket) =>
        socket.Sent.Where(m => m.Type == MessageTypes.HomePinsSync).Select(m => m.HomePins!).ToList();

    private static List<(HomePinChange Change, string ClientId)> RecordRequests(HomePinnedSensorsStore store)
    {
        var requests = new List<(HomePinChange, string)>();
        store.PhoneChangeRequested += (change, clientId) =>
        {
            lock (requests) requests.Add((change, clientId));
        };
        return requests;
    }

    [Fact]
    public async Task AnAuthenticatedPhoneIsSentTheCurrentListAfterItsReconnectAck()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp", "GPU Temp"]);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(Authenticate(secret));

        await RunAsync(socket, store, secret, isLoopback: false);

        var sync = Assert.Single(Syncs(socket));
        Assert.Equal(["CPU Temp"], sync.SensorNames);
        Assert.Equal(["CPU Temp", "GPU Temp"], sync.PinnableSensorNames);
        Assert.Equal(1, sync.Revision);

        var types = socket.Sent.Select(m => m.Type).ToList();
        Assert.True(
            types.IndexOf(MessageTypes.ReconnectResult) < types.IndexOf(MessageTypes.HomePinsSync),
            "the phone must know it is paired before the list arrives");
    }

    [Fact]
    public async Task NothingIsSentBeforeThePhoneHasProvedWhoItIs()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp"]);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            Send(new RemexMessage { Type = MessageTypes.Ping, ClientId = ClientId, Timestamp = 1 }));

        await RunAsync(socket, store, secret, isLoopback: false);

        Assert.Empty(Syncs(socket));
    }

    [Fact]
    public async Task LoopbackIsNeverSentTheListAndCannotChangeIt()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp"]);
        var requests = RecordRequests(store);
        var socket = new InteractiveWebSocket(
            Send(new RemexMessage { Type = MessageTypes.Ping, Timestamp = 1 }),
            Send(Change("CPU Temp", pinned: false)));

        await RunAsync(socket, store, RandomNumberGenerator.GetBytes(32), isLoopback: true);

        Assert.Empty(Syncs(socket));
        Assert.Empty(requests);
    }

    [Fact]
    public async Task APublishWhileConnectedReachesThePhone()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp", "GPU Temp"]);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [
                .. Authenticate(secret),
                Do(() => store.PublishFromPc(["CPU Temp", "GPU Temp"], ["CPU Temp", "GPU Temp"])),
                WaitFor(s => Syncs(s).Count >= 2),
            ]);

        await RunAsync(socket, store, secret, isLoopback: false);

        var syncs = Syncs(socket);
        Assert.Equal(2, syncs.Count);
        Assert.Equal(["CPU Temp", "GPU Temp"], syncs[1].SensorNames);
        Assert.Equal(2, syncs[1].Revision);
    }

    [Fact]
    public async Task AValidChangeIsHandedToTheStoreWithTheProvenClientId()
    {
        var store = new HomePinnedSensorsStore();
        var requests = RecordRequests(store);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket([.. Authenticate(secret), Send(Change("GPU Temp", pinned: true))]);

        await RunAsync(socket, store, secret, isLoopback: false);

        var (change, clientId) = Assert.Single(requests);
        Assert.Equal("GPU Temp", change.SensorName);
        Assert.True(change.Pinned);
        Assert.Equal(ClientId, clientId);
    }

    [Fact]
    public async Task AChangeBeforeAuthenticationNeverReachesTheStore()
    {
        var store = new HomePinnedSensorsStore();
        var requests = RecordRequests(store);
        var socket = new InteractiveWebSocket(Send(Change("GPU Temp", pinned: true)));

        await RunAsync(socket, store, RandomNumberGenerator.GetBytes(32), isLoopback: false);

        Assert.Empty(requests);
    }

    [Fact]
    public async Task AnInvalidChangeIsDroppedAndTheSessionCarriesOn()
    {
        var store = new HomePinnedSensorsStore();
        var requests = RecordRequests(store);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [
                .. Authenticate(secret),
                Send(Change("   ", pinned: true)),
                Send(Change("CPU\u0007Temp", pinned: true)),
                Send(Change(new string('x', 201), pinned: true)),
                Send(new RemexMessage { Type = MessageTypes.HomePinsChange, ClientId = ClientId }),
                Send(Change("GPU Temp", pinned: false)),
            ]);

        await RunAsync(socket, store, secret, isLoopback: false);

        var (change, _) = Assert.Single(requests);
        Assert.Equal("GPU Temp", change.SensorName);
        Assert.False(change.Pinned);
    }

    [Fact]
    public async Task MalformedJsonInTheChangeDoesNotDropTheSession()
    {
        var store = new HomePinnedSensorsStore();
        var requests = RecordRequests(store);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new InteractiveWebSocket(
            [
                .. Authenticate(secret),
                SendRaw("""{"type":"home_pins_change","protocolVersion":2,"clientId":"phone-1","homePinChange":"garbage"}"""),
                SendRaw("""{"type":"home_pins_change","protocolVersion":2,"clientId":"phone-1","homePinChange":{"sensorName":5,"pinned":"yes"}}"""),
                SendRaw("""{"type":"home_pins_change","protocolVersion":2,"clientId":"phone-1","homePinChange":{"pinned":true}}"""),
                SendRaw("""{"type":"home_pins_change","protocolVersion":2,"clientId":"phone-1","homePinChange":{"sensorName":"GPU Temp","pinned":true}}"""),
            ]);

        await RunAsync(socket, store, secret, isLoopback: false);

        var (change, _) = Assert.Single(requests);
        Assert.Equal("GPU Temp", change.SensorName);
    }

    [Fact]
    public void TheHostAdvertisesPinnedSensorSync()
    {
        var provider = new HostCapabilitiesProvider(
            new FakeScreenCaptureService(), Mock.Of<IInputSimulationService>(), () => "0A:1B:2C:3D:4E:5F");

        Assert.True(provider.GetCurrent().SupportsHomePinsSync);
    }

    private static async Task RunAsync(
        InteractiveWebSocket socket, HomePinnedSensorsStore store, byte[] secret, bool isLoopback)
    {
        var registryPath = Path.Combine(Directory.CreateTempSubdirectory("remex-homepins-").FullName, "paired_clients.json");
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
            homePinsStore: store);

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
