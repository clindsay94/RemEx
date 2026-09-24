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
/// Pins the per-connection telemetry pause (perf audit P0-5): a backgrounded phone sends
/// <c>telemetry_pause</c> and the host stops pushing its 60-100 KB envelope every second to that
/// connection until <c>telemetry_resume</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every failure here is silent. Pause that does nothing wastes battery with no symptom; pause that
/// leaks across connections starves the PC's other clients; and a resume that never resumes is the
/// "connected but telemetry never updates" bug the stream loop's guard comment already describes -
/// which is why the pause was built so it never touches <c>lastSentSnapshot</c>.
/// </para>
/// <para>
/// Drives <see cref="PingPongHandler.StreamTelemetryAsync"/> directly rather than a whole connection:
/// the pause messages are pairing-gated, and pairing a fake socket needs collaborators the
/// connection-level tests deliberately leave null. See that method's remarks for the residual gap.
/// </para>
/// </remarks>
public class TelemetryPauseTests
{
    /// <summary>Counts telemetry frames sent to it and remembers the last one's timestamp.</summary>
    private sealed class CountingWebSocket : WebSocket
    {
        private int _telemetry;
        private long _lastTimestamp;

        public int TelemetryCount => Volatile.Read(ref _telemetry);
        public long LastTimestamp => Interlocked.Read(ref _lastTimestamp);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message?.Type == MessageTypes.Telemetry)
            {
                Interlocked.Exchange(ref _lastTimestamp, message.Timestamp ?? 0);
                Interlocked.Increment(ref _telemetry);
            }
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("the stream loop never receives");

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

    // ---- The gate on its own -------------------------------------------------------------------

    [Fact]
    public void AFreshGateIsNotPausedAndItsWaitIsAlreadyComplete()
    {
        // A new connection must stream from the first tick; a gate that started paused would be
        // the "connected but nothing ever arrives" bug in a new place.
        var gate = new TelemetryPauseGate();

        Assert.False(gate.IsPaused);
        Assert.True(gate.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task ResumeReleasesAWaiterWithoutAnyTimer()
    {
        // THE PROMPTNESS HALF. The wait completes on Resume itself, not on a clock or the next
        // sample, so a returning phone is not left staring at a stale reading for up to a second.
        var gate = new TelemetryPauseGate();
        gate.Pause();

        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
        Assert.True(gate.IsPaused);
        Assert.False(wait.IsCompleted);

        gate.Resume();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task PauseAndResumeAreIdempotent()
    {
        // The phone re-sends its pause on every reconnect and lifecycle event, so duplicates are
        // normal traffic. A second Pause must not orphan the first waiter; a stray Resume must not throw.
        var gate = new TelemetryPauseGate();
        gate.Resume();
        Assert.False(gate.IsPaused);

        gate.Pause();
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
        gate.Pause();
        gate.Resume();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Resume();
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task APausedWaitEndsOnCancellation()
    {
        // Teardown cancels the stream; a paused loop must unwind with the connection, not hang it.
        var gate = new TelemetryPauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource();

        var wait = gate.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    // ---- The stream loop ----------------------------------------------------------------------

    [Fact]
    public async Task PausingOneConnectionStopsOnlyItsStreamAndResumeSendsTheCurrentSampleAtOnce()
    {
        var sampler = new TelemetryBackgroundService(
            new StubTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance);
        await sampler.StartAsync(CancellationToken.None);
        var handler = NewHandler(sampler);

        var phone = new CountingWebSocket();
        var other = new CountingWebSocket();
        var phoneGate = new TelemetryPauseGate();
        var otherGate = new TelemetryPauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await WaitUntilAsync(() => sampler.CurrentSnapshot is not null, "the sampler never published");

            var phoneStream = handler.StreamTelemetryAsync(phone, phoneGate, cts.Token);
            var otherStream = handler.StreamTelemetryAsync(other, otherGate, cts.Token);

            await WaitUntilAsync(() => phone.TelemetryCount >= 1 && other.TelemetryCount >= 1,
                "both streams should send their first sample");

            // PAUSE. Let any send already in flight land, then take the baseline.
            phoneGate.Pause();
            await Task.Delay(300);
            var phoneAtPause = phone.TelemetryCount;
            var otherAtPause = other.TelemetryCount;

            // Span several sampler ticks: at 1 Hz a shorter window cannot tell "paused" from "slow".
            await Task.Delay(2500);

            Assert.Equal(phoneAtPause, phone.TelemetryCount);
            // ISOLATION, and positive so the pause assertion above cannot pass vacuously on a
            // stalled sampler: the other connection kept streaming the whole time. >= +1, not +2:
            // under a loaded parallel test run the sampler's 1000ms-plus-sample-time cycle can push
            // past 1.25s, and a hard +2 in a 2.5s window flakes there (RemEx-w7ei pattern). +1 still
            // proves the other connection was not also silenced by the pause.
            Assert.True(other.TelemetryCount >= otherAtPause + 1,
                $"the unpaused connection must keep streaming; went {otherAtPause} -> {other.TelemetryCount}");

            // RESUME, timed just after a publish so the next one is about a second away. A resume
            // that waited for the sampler would deliver THAT one; a prompt resume delivers the
            // sample current right now - told apart by timestamp, not by a racy latency bound.
            var before = sampler.CurrentSnapshot;
            await WaitUntilAsync(() => !ReferenceEquals(sampler.CurrentSnapshot, before), "the sampler stalled");
            var current = MessageSerializer.Deserialize(sampler.CurrentSnapshot!.Frame.Span)!.Timestamp ?? 0;
            Assert.NotEqual(0, current);
            phoneGate.Resume();

            await WaitUntilAsync(() => phone.TelemetryCount > phoneAtPause, "resume must bring the stream back");
            Assert.Equal(current, phone.LastTimestamp);

            // And it is a stream again, not a single catch-up frame (the lastSentSnapshot trap).
            var afterResume = phone.TelemetryCount;
            await WaitUntilAsync(() => phone.TelemetryCount > afterResume, "the resumed stream must keep sending");

            cts.Cancel();
            await Task.WhenAll(phoneStream, otherStream).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cts.Cancel();
            handler.Dispose();
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APausedStreamEndsWhenTheConnectionIsTornDown()
    {
        // Parked on the resume wait rather than the sampler - teardown must still unwind it.
        var sampler = new TelemetryBackgroundService(
            new StubTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance);
        await sampler.StartAsync(CancellationToken.None);
        var handler = NewHandler(sampler);
        var gate = new TelemetryPauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource();

        try
        {
            var stream = handler.StreamTelemetryAsync(new CountingWebSocket(), gate, cts.Token);
            await Task.Delay(200);
            Assert.False(stream.IsCompleted);

            cts.Cancel();
            await stream.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cts.Cancel();
            handler.Dispose();
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The stream loop reaches only the sampler and the logger, so the rest is mocked or null — the
    /// same construction as <c>LoopbackTelemetryStreamTests</c>.
    /// </summary>
    private static PingPongHandler NewHandler(TelemetryBackgroundService sampler) => new(
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
        null!,
        null!,
        null!,
        null!,
        null!,
        new ClientSessionRegistry(),
        null!,
        null!,
        new FakeHostClipboard(),
        Mock.Of<Remex.Agent.Services.Media.IMediaSessionMonitor>(),
        new Remex.Agent.Services.Theme.PhoneThemeSnapshotStore());
}
