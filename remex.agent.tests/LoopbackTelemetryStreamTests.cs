using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Security;
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
    /// <summary>Accepts messages for <paramref name="holdMs"/>, then reports Close so <c>HandleAsync</c> unwinds.</summary>
    private sealed class OneShotWebSocket(int holdMs = 400) : WebSocket
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
            await Task.Delay(TimeSpan.FromMilliseconds(holdMs), c);
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

    /// <summary>Fails every telemetry send with a transient error, counting the attempts.</summary>
    /// <remarks>
    /// Only telemetry throws. Failing every send would take down the handshake instead, and the
    /// question here is specifically what the telemetry loop does when a send keeps failing while the
    /// connection itself stays healthy — which is the case the loop's per-iteration catch exists for.
    /// <see cref="InvalidOperationException"/> rather than <see cref="WebSocketException"/> on purpose:
    /// the latter is the one exception the loop treats as fatal, so it would end the stream and prove
    /// nothing about retry pacing.
    /// </remarks>
    private sealed class TelemetryFailingWebSocket(int holdMs) : WebSocket
    {
        private int _telemetryAttempts;

        public int TelemetryAttempts => Volatile.Read(ref _telemetryAttempts);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message?.Type != MessageTypes.Telemetry)
                return Task.CompletedTask;

            Interlocked.Increment(ref _telemetryAttempts);
            throw new InvalidOperationException("transient send failure");
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(holdMs), c);
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

    private static async Task<List<RemexMessage>> RunConnectionAsync(
        bool isLoopback, ClientSessionRegistry? sessionRegistry = null, int holdMs = 400)
    {
        var socket = new OneShotWebSocket(holdMs);
        await RunHandlerAsync(socket, isLoopback, sessionRegistry);
        lock (socket.Received) return [.. socket.Received];
    }

    /// <summary>Drives one whole connection over <paramref name="socket"/> and returns when it closes.</summary>
    private static async Task RunHandlerAsync(
        WebSocket socket, bool isLoopback, ClientSessionRegistry? sessionRegistry = null)
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
            null!,  // ScreenshotService: this test never takes one
            null!,
            NewFileTransferHandler(),
            null!,
            null!,
            null!,  // FilePushOriginator: this test never pushes
            sessionRegistry ?? new ClientSessionRegistry(),
            NewNameStore(),
            NewActivityStore(),
            new FakeHostClipboard());

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await handler.HandleAsync(
                socket, isLoopback, isTrustedForPinAutoFetch: false,
                remoteAddress: isLoopback ? "127.0.0.1" : "192.168.1.50", cts.Token);
        }
        finally
        {
            handler.Dispose();
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFinishedConnectionLeavesNothingBehindInTheSessionRegistry()
    {
        // END-TO-END LEAK CHECK, through the real handler rather than the registry in isolation.
        // HandleAsync registers on its first line and relies on `using` to unregister, so a future
        // early return, throw or rewrite that drops the `using` would leave a phantom session that
        // PhonePresence reports as an attached phone with nothing attached. Driving a whole
        // connection and finding the registry empty afterwards is what catches that.
        //
        // LOOPBACK ON PURPOSE. Snapshot only reports AUTHENTICATED sessions, and a connection that
        // never pairs never becomes one — so running this over a non-loopback socket would find an
        // empty registry whether the session leaked or not, and prove nothing. Loopback is
        // authenticated the moment it connects, so a leak is visible.
        var registry = new ClientSessionRegistry();

        await RunConnectionAsync(isLoopback: true, registry);

        Assert.Empty(registry.Snapshot());
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

    [Fact]
    public async Task ARemoteClientKeepsReceivingTelemetryAndNotJustTheFirstSample()
    {
        // **THE TEST ABOVE ASSERTS "AT LEAST ONE", AND THAT IS EXACTLY THE HOLE A ONE-CHARACTER SLIP
        // FITS THROUGH (RemEx-uj7s).** When the stream stopped running its own timer and began waiting
        // on the sampler, the first draft wrote `lastSentSnapshot = await WaitForNextSnapshotAsync(...)`
        // - which marks the awaited sample as SENT before sending it, so the loop's reference-equality
        // check then skips it. The stream emitted its opening sample and went silent forever, with a
        // healthy socket, no exception and no log line. Every test in this file stayed green.
        //
        // Counting past one is the only thing that sees it. The hold spans several sampler ticks
        // rather than the 400 ms the other cases use, because at 1 Hz a shorter connection cannot
        // distinguish "streaming" from "sent one and stopped".
        var sent = await RunConnectionAsync(isLoopback: false, holdMs: 2500);

        var telemetry = sent.Count(m => m.Type == MessageTypes.Telemetry);
        Assert.True(telemetry >= 2, $"expected the stream to keep sending; got {telemetry} telemetry message(s)");
    }

    [Fact]
    public async Task AFailingSendIsRetriedAtIntervalsRatherThanInATightLoop()
    {
        // **WAITING ON THE SAMPLER REMOVED THE PACING FROM THE ERROR PATH, WHICH THE OLD Task.Delay
        // GAVE FOR FREE (RemEx-uj7s).** lastSentSnapshot is advanced only AFTER a send returns, so a
        // failed send leaves it pointing at the previous sample - and the new wait, asked for anything
        // newer than that, finds the current sample already qualifies and returns instantly. The loop
        // then retries the failing send immediately, and again, and again: a hot spin burning a core
        // and writing one warning per pass for as long as the fault lasts, on a connection that still
        // looks healthy. The fix backs off on a failed tick instead of waiting.
        //
        // Counting attempts over a window is what distinguishes "retrying" from "spinning" - both send
        // more than once, and only the rate tells them apart. The bound is deliberately loose: the
        // failure mode it guards produces thousands, not four.
        // **BOTH BOUNDS ARE LOAD-BEARING, AND AN EARLIER DRAFT ASSERTED ONLY `>= 1`.** That is
        // satisfied by a stream that attempts one send and then DIES - the opposite failure, and
        // precisely the per-iteration resilience the bead warns a redesign must preserve (only
        // WebSocket-state failures may end the stream). Nothing else in the repo covers it, because
        // OneShotWebSocket never throws. Changing the generic catch to `return` would have passed.
        var socket = new TelemetryFailingWebSocket(holdMs: 2500);

        var elapsed = Stopwatch.StartNew();
        await RunHandlerAsync(socket, isLoopback: false);
        elapsed.Stop();

        Assert.True(
            socket.TelemetryAttempts >= 2,
            $"the stream must keep retrying, not die on the first failure; got {socket.TelemetryAttempts}");

        // Against measured elapsed time rather than the nominal hold. Retries are one per second, so
        // the ceiling has to move with the window actually observed - a scheduling overshoot on a
        // loaded machine lengthens the hold and would fail a fixed bound for a reason that has nothing
        // to do with spinning. The failure this guards produces thousands, so the slack costs nothing.
        var ceiling = elapsed.Elapsed.TotalSeconds + 2;
        Assert.True(
            socket.TelemetryAttempts <= ceiling,
            $"expected ~1 retry/sec over {elapsed.Elapsed.TotalSeconds:F1}s; got {socket.TelemetryAttempts}, which is a spin");
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

}
