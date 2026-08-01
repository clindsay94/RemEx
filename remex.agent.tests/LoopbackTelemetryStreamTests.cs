using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins which connections get a telemetry stream after the PC's own UI started reading telemetry
/// in-process (RemEx-ite8).
/// </summary>
/// <remarks>
/// <para>
/// The embedded UI auto-connects to <c>wss://localhost:5005/ws</c>, so its dashboard data was
/// serialized, TLS-encrypted, pushed through the loopback adapter, decrypted and rebuilt into a full
/// record graph once a second, forever — to deliver a value already sitting in the same process. The
/// host now skips the stream for loopback connections and the UI subscribes to
/// <see cref="ITelemetryBroadcaster"/> instead.
/// </para>
/// <para>
/// THE DANGEROUS DIRECTION IS THE SECOND TEST. Getting the condition backwards, or letting it drift,
/// costs every phone its telemetry — the dashboard would sit on "Collecting Data" with a healthy
/// connection and no error anywhere. That case had NO coverage before this file: the integration
/// tests all run against a TestServer, where the remote IP is null and therefore every connection
/// counts as loopback, so they could never have caught it.
/// </para>
/// <para>
/// Collaborators are mocked, real, or <c>null!</c> according to whether this path reaches them — a
/// primary constructor only captures what it uses. The file-transfer handler had to be real: teardown
/// calls it on every exit path. The others stay null only because this fake socket sends NO messages —
/// <c>pairedClientRegistry</c> in particular is dereferenced for any inbound message carrying a client
/// id, so making the fake talk would NRE. Same technique as the destructive-action tests (RemEx-w9ui).
/// </para>
/// </remarks>
public class LoopbackTelemetryStreamTests
{
    /// <summary>Accepts one message, then reports Close so <c>HandleAsync</c> unwinds.</summary>
    private sealed class OneShotWebSocket : WebSocket
    {
        public List<RemexMessage> Received { get; } = [];

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
                lock (Received) Received.Add(message);
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            // Hold the connection open long enough for a telemetry tick to be due, then close it.
            await Task.Delay(TimeSpan.FromMilliseconds(400), c);
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

    private sealed class StubTelemetryService : ITelemetryService
    {
        public Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
            => Task.FromResult(new TelemetryPayload
            {
                Sensors =
                [
                    new SensorReading { Name = "Total CPU Usage", Value = 42, Unit = "%", Category = "CPU", Source = "Test" },
                ],
            });
    }

    /// <summary>
    /// Real rather than null, because connection teardown calls CleanupAllTransfersAsync on EVERY
    /// exit path — which is worth knowing in itself: that cleanup is what stops a dropped connection
    /// leaking its in-flight transfers.
    /// </summary>
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

    private static async Task<List<RemexMessage>> RunConnectionAsync(bool isLoopback)
    {
        var sampler = new TelemetryBackgroundService(
            new StubTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance);
        await sampler.StartAsync(CancellationToken.None);

        // The stream sends whatever sample exists, so wait for the first one rather than racing it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sampler.CurrentSnapshot is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotNull(sampler.CurrentSnapshot);

        var handler = new PingPongHandler(
            NullLogger<PingPongHandler>.Instance,
            sampler,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,
            NewFileTransferHandler(),
            null!,
            null!);

        var socket = new OneShotWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await handler.HandleAsync(socket, isLoopback, isTrustedForPinAutoFetch: false, cts.Token);
        }
        finally
        {
            handler.Dispose();
            await sampler.StopAsync(CancellationToken.None);
        }

        lock (socket.Received) return [.. socket.Received];
    }

    [Fact]
    public async Task TheLocalUiIsNotSentTelemetryOverTheSocket()
    {
        // THE BEAD. This connection is the host talking to itself; the UI reads the sample by
        // reference instead, so putting it through serialize/TLS/deserialize was pure waste.
        var sent = await RunConnectionAsync(isLoopback: true);

        // Paired with a positive assertion so this cannot pass vacuously — if the fake ever stopped
        // decoding messages at all, a lone DoesNotContain would still be green.
        Assert.DoesNotContain(sent, m => m.Type == MessageTypes.Telemetry);
        Assert.Contains(sent, m => m.Type == MessageTypes.HostInfo);
    }

    [Fact]
    public async Task ARemoteClientStillReceivesTelemetry()
    {
        // THE REGRESSION GUARD, and the reason this file exists. A phone has no in-process anything;
        // if the skip condition is ever inverted or widened, its dashboard silently stops updating
        // while the connection stays healthy. No integration test can cover this — they run against a
        // TestServer whose remote IP is null, which reads as loopback.
        var sent = await RunConnectionAsync(isLoopback: false);

        Assert.Contains(sent, m => m.Type == MessageTypes.Telemetry && m.Telemetry is not null);
    }
}
