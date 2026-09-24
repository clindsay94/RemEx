using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins consumer-gated sampling on the host (perf audit P0-10): the telemetry and media samplers do
/// their work only while something holds a demand lease, and keep their PeriodicTimer spacing while
/// they do.
/// </summary>
/// <remarks>
/// <para>
/// Every failure here is silent in one of two directions. A lease that leaks, or a gate that is never
/// consulted, brings back the 1 Hz poll on every idle tray-resident PC with no symptom at all. A lease
/// that is dropped early, or a sampler that does not resume, is a dashboard or phone that stops
/// updating with nothing in the log - the "connected but telemetry never updates" shape.
/// </para>
/// <para>
/// The desktop's three signals (window, tray flyout, armed alerts) are pinned separately in
/// <c>TelemetryDemandCoordinatorTests</c>; this file covers the counter, the samplers and the phone
/// stream's lease.
/// </para>
/// </remarks>
public class SamplingDemandTests
{
    // ---- The counter ---------------------------------------------------------------------------

    [Fact]
    public void EachLeaseCountsOnceAndReleasesOnlyItself()
    {
        var demand = new SamplingDemand();
        Assert.False(demand.IsDemanded);

        var phone = demand.Acquire();
        var window = demand.Acquire();
        Assert.Equal(2, demand.Consumers);

        // A COUNT, NOT A FLAG: one consumer leaving must not silence the other, and releasing twice
        // must not drive the count under the real number of readers.
        phone.Dispose();
        phone.Dispose();
        Assert.Equal(1, demand.Consumers);
        Assert.True(demand.IsDemanded);

        window.Dispose();
        Assert.Equal(0, demand.Consumers);
        Assert.False(demand.IsDemanded);
    }

    [Fact]
    public void TicksGoIdleOnceAndResumeOnceAsConsumersComeAndGo()
    {
        var demand = new SamplingDemand();

        // Starts idle: a host with nobody attached does nothing and does not announce going idle.
        Assert.Equal(SampleTick.Skip, demand.NextTick());

        var lease = demand.Acquire();
        Assert.Equal(SampleTick.Resume, demand.NextTick());
        Assert.Equal(SampleTick.Sample, demand.NextTick());

        lease.Dispose();
        Assert.Equal(SampleTick.Idle, demand.NextTick());
        Assert.Equal(SampleTick.Skip, demand.NextTick());

        using var again = demand.Acquire();
        Assert.Equal(SampleTick.Resume, demand.NextTick());
        Assert.Equal(SampleTick.Sample, demand.NextTick());
    }

    [Fact]
    public void AnUngatedDemandAlwaysSamplesAndNeverChangesState()
    {
        // The pre-P0-10 behaviour a sampler built without a gate keeps, so the tests that drive one
        // directly still see a sample every tick.
        var demand = SamplingDemand.Ungated();

        Assert.True(demand.IsDemanded);
        Assert.Equal(SampleTick.Sample, demand.NextTick());
        Assert.Equal(SampleTick.Sample, demand.NextTick());
    }

    // ---- DemandHold ----------------------------------------------------------------------------

    [Fact]
    public void AHoldTakesOneLeaseAndDropsItAsTheConditionFlips()
    {
        var demand = new SamplingDemand();
        var hold = new DemandHold(demand.Acquire);

        hold.Set(true);
        hold.Set(true);
        Assert.Equal(1, demand.Consumers);
        Assert.True(hold.IsHeld);

        hold.Set(false);
        hold.Set(false);
        Assert.Equal(0, demand.Consumers);
        Assert.False(hold.IsHeld);

        hold.Set(true);
        hold.Dispose();
        Assert.Equal(0, demand.Consumers);

        // Disposed means disposed: a late Set from an event handler must not leak a new lease.
        hold.Set(true);
        Assert.Equal(0, demand.Consumers);
    }

    [Fact]
    public void AHoldWhoseAcquireReturnsNullStillCountsAsHeld()
    {
        var calls = 0;
        var hold = new DemandHold(() => { calls++; return null; });

        hold.Set(true);
        hold.Set(true);
        Assert.True(hold.IsHeld);
        Assert.Equal(1, calls);

        hold.Set(false);
        Assert.False(hold.IsHeld);
    }

    // ---- The telemetry sampler -----------------------------------------------------------------

    private sealed class CountingTelemetryService : ITelemetryService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult(new TelemetryPayload
            {
                Sensors = [new SensorReading { Name = "Total CPU Usage", Value = n, Unit = "%", Source = "Test" }],
            });
        }
    }

    [Fact]
    public async Task TheTelemetrySamplerPollsOnlyWhileALeaseIsHeldAndForgetsItsReadingWhenIdle()
    {
        var sensors = new CountingTelemetryService();
        var demand = new SamplingDemand();
        using var sampler = new TelemetryBackgroundService(
            sensors, NullLogger<TelemetryBackgroundService>.Instance, demand);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // NOBODY READING: well over a period passes and the sensors are never touched.
            await Task.Delay(1500);
            Assert.Equal(0, sensors.Calls);
            Assert.Null(sampler.CurrentSnapshot);

            // A consumer arrives (a phone connecting, the window showing, an alert being armed).
            var lease = sampler.AcquireDemand();
            await WaitUntilAsync(() => sampler.CurrentSnapshot is not null, "sampling must resume for a new consumer");
            await WaitUntilAsync(() => sensors.Calls >= 2, "and keep going while the consumer stays");

            // The last consumer leaves: the next tick goes idle and clears the published reading, so a
            // later consumer is not handed it as current.
            lease.Dispose();
            await WaitUntilAsync(() => sampler.CurrentSnapshot is null, "going idle must clear the stale reading");
            var callsWhenIdle = sensors.Calls;
            await Task.Delay(1500);
            Assert.Equal(callsWhenIdle, sensors.Calls);

            // And it comes back again.
            using var again = sampler.AcquireDemand();
            await WaitUntilAsync(() => sensors.Calls > callsWhenIdle && sampler.CurrentSnapshot is not null,
                "sampling must resume a second time");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AWaiterParkedWhileIdleIsWokenByTheFirstFreshSample()
    {
        // The phone-connects-to-an-idle-host path: its stream waits on the gate with nothing sent yet,
        // and must be woken by the sample its own lease causes rather than sleep forever.
        var demand = new SamplingDemand();
        using var sampler = new TelemetryBackgroundService(
            new CountingTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance, demand);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            var wait = sampler.WaitForNextSnapshotAsync(null, CancellationToken.None);
            await Task.Delay(300);
            Assert.False(wait.IsCompleted);

            using var lease = sampler.AcquireDemand();
            var snapshot = await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(snapshot);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Half-period sample work, the same shape as <c>TelemetrySamplerCadenceTests</c>.</summary>
    private sealed class SlowTelemetryService : ITelemetryService
    {
        public async Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
        {
            await Task.Delay(500, ct);
            return new TelemetryPayload
            {
                Sensors = [new SensorReading { Name = "Total CPU Usage", Value = 1, Unit = "%", Source = "Test" }],
            };
        }
    }

    [Fact]
    public async Task AGatedSamplerWithALeaseKeepsThePeriodicTimerSpacing()
    {
        // RemEx-6sibx UNDER THE GATE. The gate decides whether a tick does work; it must not become a
        // second clock. Same bounds and reasoning as TelemetrySamplerCadenceTests: a trailing delay
        // (or any per-tick wait added by the gate) puts the 500ms of work back into the interval and
        // the mean rises to ~1.5s; a lost period makes the loop sample back to back, ~8 in 4s.
        var demand = new SamplingDemand();
        using var lease = demand.Acquire();
        using var sampler = new TelemetryBackgroundService(
            new SlowTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance, demand);

        var clock = new Stopwatch();
        var times = new List<TimeSpan>();
        sampler.TelemetryPublished += _ =>
        {
            lock (times) times.Add(clock.Elapsed);
        };

        clock.Start();
        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        List<TimeSpan> snapshot;
        lock (times) snapshot = [.. times];

        Assert.True(snapshot.Count >= 2, $"need two publishes to have a gap; got {snapshot.Count}");
        var meanGap = (snapshot[^1] - snapshot[0]) / (snapshot.Count - 1);
        Assert.True(meanGap < TimeSpan.FromMilliseconds(1400),
            $"mean gap {meanGap.TotalMilliseconds:F0}ms suggests the sample duration is back in the interval");
        Assert.True(snapshot.Count <= 6, $"{snapshot.Count} publishes in 4s is faster than the intended 1 Hz");
        Assert.True(snapshot[0] < TelemetryBackgroundService.SamplePeriod,
            $"a lease held at start must not cost the first sample a tick; it landed at {snapshot[0].TotalMilliseconds:F0}ms");
    }

    // ---- The media sampler ---------------------------------------------------------------------

    private sealed class CountingReader : IMediaSessionReader
    {
        private int _reads;

        public bool IsSupported => true;

        public int Reads => Volatile.Read(ref _reads);

        public Task<MediaPlaybackState> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new MediaPlaybackState { Status = MediaPlaybackStatus.Playing, Title = "Same song" });
        }
    }

    [Fact]
    public async Task TheMediaSamplerReadsOnlyWhileALeaseIsHeldAndRepublishesAfterIdle()
    {
        var reader = new CountingReader();
        var sampler = new MediaSessionBackgroundService(
            reader,
            NullMediaArtworkSource.Instance,
            new MediaArtworkStore(),
            NullMediaSeekTarget.Instance,
            NullLogger<MediaSessionBackgroundService>.Instance,
            new SamplingDemand());

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(1500);
            Assert.Equal(0, reader.Reads);
            Assert.Null(sampler.Current);

            var lease = sampler.AcquireDemand();
            await WaitUntilAsync(() => sampler.Current is not null, "a connected client must get a reading");

            lease.Dispose();
            await WaitUntilAsync(() => sampler.Current is null, "going idle must forget the reading");
            var readsWhenIdle = reader.Reads;
            await Task.Delay(1500);
            Assert.Equal(readsWhenIdle, reader.Reads);

            // THE SAME READING AS BEFORE MUST STILL BE PUBLISHED. The value-equality dedupe would
            // otherwise swallow it and leave the cleared gate empty - a phone connecting to a PC that
            // is still playing the same song would never be told anything.
            using var again = sampler.AcquireDemand();
            await WaitUntilAsync(() => sampler.Current is not null, "resuming must republish an unchanged reading");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    // ---- The phone's telemetry stream ----------------------------------------------------------

    /// <summary>An open socket that accepts and discards every send.</summary>
    private sealed class SinkWebSocket : WebSocket
    {
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
            => Task.CompletedTask;

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

    [Fact]
    public async Task APhoneStreamHoldsDemandExceptWhilePausedAndReleasesItOnDisconnect()
    {
        var demand = new SamplingDemand();
        using var sampler = new TelemetryBackgroundService(
            new CountingTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance, demand);
        await sampler.StartAsync(CancellationToken.None);
        var handler = NewHandler(sampler);
        var pause = new TelemetryPauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var stream = handler.StreamTelemetryAsync(new SinkWebSocket(), pause, cts.Token);

            // CONNECTED: the phone alone is enough to start the sampler.
            await WaitUntilAsync(() => demand.Consumers == 1, "a connected phone must hold demand");
            await WaitUntilAsync(() => sampler.CurrentSnapshot is not null, "and the sampler must run for it");

            // PAUSED (backgrounded phone): the claim goes, within a tick - the loop may be parked on
            // the sampler when the pause lands and releases on its next pass.
            pause.Pause();
            await WaitUntilAsync(() => demand.Consumers == 0, "a paused phone must not keep the sampler running");

            // RESUMED: taken again immediately, without waiting for a sample.
            pause.Resume();
            await WaitUntilAsync(() => demand.Consumers == 1, "a resumed phone must hold demand again");

            // DISCONNECTED: released on the way out.
            cts.Cancel();
            await stream.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, demand.Consumers);
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
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            await Task.Delay(10);
        }
    }

    /// <summary>Same construction as <c>TelemetryPauseTests</c>: the stream loop reaches only the
    /// sampler and the logger.</summary>
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
        Mock.Of<IMediaSessionMonitor>(),
        new Remex.Agent.Services.Theme.PhoneThemeSnapshotStore());
}
