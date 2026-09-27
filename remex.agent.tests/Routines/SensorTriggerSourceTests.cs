using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Routines;
using Remex.Core.Models;
using Remex.Core.Routines;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>SensorTriggerSourceTests</c> (§8.5.1, T9, R-SYS-20): sustain, hysteresis clear,
/// the 300 s cooldown, the missing-sensor record, and demand held only while a sensor routine is armed.
/// </summary>
public sealed class SensorTriggerSourceTests
{
    private const string Gpu = FakeTelemetry.GpuTemp;

    private static (SensorTriggerSource Source, FakeTelemetry Feed, ManualTimeProvider Time, List<RoutineTriggerFire> Fires, List<RoutineTriggerFire> Missing) Create()
    {
        var feed = new FakeTelemetry();
        var time = new ManualTimeProvider();
        var source = new SensorTriggerSource(feed, time, NullLogger.Instance);
        var fires = new List<RoutineTriggerFire>();
        var missing = new List<RoutineTriggerFire>();
        source.Fired += fires.Add;
        source.Unavailable += missing.Add;
        return (source, feed, time, fires, missing);
    }

    private static SensorArming Arming(double threshold = 85, int sustain = 30, AlertDirection direction = AlertDirection.Above, string id = "r1") =>
        new(Owner, id, Gpu, "GPU temperature", direction, threshold, sustain);

    /// <summary>One sample per second for <paramref name="seconds"/> seconds at <paramref name="value"/>.</summary>
    private static void Feed(FakeTelemetry feed, ManualTimeProvider time, double value, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            feed.Publish((Gpu, value));
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void DemandIsHeldOnlyWhileASensorRoutineIsArmed()
    {
        var (source, feed, _, _, _) = Create();
        Assert.Equal(0, feed.Leases);

        source.SetArmed([Arming()]);
        Assert.Equal(1, feed.Leases);
        Assert.True(source.HoldsDemand);

        // Re-arming does not stack leases.
        source.SetArmed([Arming(), Arming(id: "r2")]);
        Assert.Equal(1, feed.Leases);

        source.SetArmed([]);
        Assert.Equal(0, feed.Leases);
        Assert.False(source.HoldsDemand);
    }

    [Fact]
    public void DisposeReleasesTheLease()
    {
        var (source, feed, _, _, _) = Create();
        source.SetArmed([Arming()]);
        source.Dispose();
        Assert.Equal(0, feed.Leases);
    }

    [Fact]
    public void FiresOnlyOnceTheBreachIsSustained()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(sustain: 30)]);

        Feed(feed, time, 90, 30);
        Assert.Empty(fires);

        feed.Publish((Gpu, 90));
        var fire = Assert.Single(fires);
        Assert.Equal(RoutineRunSources.PcSensor, fire.Source);
        Assert.Equal("GPU temperature", fire.Detail.SensorName);
        Assert.Equal(90, fire.Detail.Value);
    }

    [Fact]
    public void ANonLiveSampleRestartsTheSustainWindow()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(sustain: 30)]);

        Feed(feed, time, 90, 20);
        Feed(feed, time, 70, 1);
        Feed(feed, time, 90, 20);

        Assert.Empty(fires);
    }

    [Fact]
    public void TheClearBandKeepsABreachLiveJustUnderTheThreshold()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(threshold: 85, sustain: 10)]);

        // 84 is inside the 2% band (1.7) of a live breach: the window keeps running.
        Feed(feed, time, 90, 5);
        Feed(feed, time, 84, 6);
        Assert.Single(fires);
    }

    [Fact]
    public void BelowTheBandTheBreachClears()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(threshold: 85, sustain: 10)]);

        Feed(feed, time, 90, 5);
        Feed(feed, time, 83, 1);
        Feed(feed, time, 90, 5);
        Assert.Empty(fires);
    }

    [Fact]
    public void AStillBreachedSensorDoesNotFireTwice()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(sustain: 5)]);

        Feed(feed, time, 90, 1000);

        Assert.Single(fires);
    }

    [Fact]
    public void ReArmingNeedsANonLiveSampleAndTheCooldown()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(sustain: 5)]);

        Feed(feed, time, 90, 6);
        Assert.Single(fires);

        // Back to normal, then hot again well inside the 300 s cooldown: nothing.
        Feed(feed, time, 70, 1);
        Feed(feed, time, 90, 100);
        Assert.Single(fires);

        // Past the cooldown, still breached: it fires again (the non-live sample already happened).
        Feed(feed, time, 90, 200);
        Assert.Equal(2, fires.Count);
    }

    [Fact]
    public void BelowDirectionFiresOnALowValue()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(threshold: 10, sustain: 5, direction: AlertDirection.Below)]);

        Feed(feed, time, 5, 6);

        Assert.Single(fires);
    }

    [Fact]
    public void AMissingSensorIsRecordedOnceAfterTenMinutes()
    {
        var (source, feed, time, fires, missing) = Create();
        source.SetArmed([Arming(sustain: 5)]);

        for (var i = 0; i < 12; i++)
        {
            feed.Publish(("/other/sensor", 1));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        feed.Publish(("/other/sensor", 1));
        var record = Assert.Single(missing);
        Assert.Equal("GPU temperature", record.Detail.SensorName);
        Assert.Empty(fires);

        // It comes back, then goes again: a fresh ten minutes before the next record.
        feed.Publish((Gpu, 50));
        feed.Publish(("/other/sensor", 1));
        time.Advance(TimeSpan.FromMinutes(5));
        feed.Publish(("/other/sensor", 1));
        Assert.Single(missing);
    }

    [Fact]
    public void AMissingSampleClearsTheBreach()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(sustain: 10)]);

        Feed(feed, time, 90, 8);
        feed.Publish(("/other/sensor", 1));
        Feed(feed, time, 90, 8);

        Assert.Empty(fires);
    }

    [Fact]
    public void ChangingTheThresholdStartsTheRoutineFresh()
    {
        var (source, feed, time, fires, _) = Create();
        source.SetArmed([Arming(threshold: 85, sustain: 10)]);
        Feed(feed, time, 90, 8);

        source.SetArmed([Arming(threshold: 88, sustain: 10)]);
        Feed(feed, time, 90, 5);

        Assert.Empty(fires);
    }
}

/// <summary>The sensor source wired into the host (§8.5.1): validation against the catalog, arming and demand.</summary>
public sealed class SensorRoutineHostTests
{
    [Fact]
    public async Task ASensorRoutineIsAcceptedWhenThePcHasTheSensor()
    {
        var bench = new RoutineHostTestBench(withTelemetry: true);
        await bench.InitializeAsync();
        bench.Telemetry!.Publish((FakeTelemetry.GpuTemp, 60));

        var result = await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(), NotifyPhone()));

        Assert.True(Assert.Single(result.Results!).Accepted);
        Assert.True(bench.Availability.SensorAvailable);
    }

    [Fact]
    public async Task AnUnknownSensorIsRejectedAsUnavailable()
    {
        var bench = new RoutineHostTestBench(withTelemetry: true);
        await bench.InitializeAsync();
        bench.Telemetry!.Publish((FakeTelemetry.RamLoad, 40));

        var result = await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(), NotifyPhone()));

        var item = Assert.Single(result.Results!);
        Assert.False(item.Accepted);
        Assert.Equal(RoutineReasonCodes.SensorUnavailable, item.ReasonCode);
        Assert.True(RoutineSyncHandler.IsTransientRejection(item));
    }

    [Fact]
    public async Task AnIdleSamplerIsWokenToReadTheCatalog()
    {
        var bench = new RoutineHostTestBench(withTelemetry: true);
        await bench.InitializeAsync();
        var telemetry = bench.Telemetry!;
        telemetry.OnAcquire = () => _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            telemetry.Publish((FakeTelemetry.GpuTemp, 55));
        });

        var result = await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(), NotifyPhone()));

        Assert.True(Assert.Single(result.Results!).Accepted);
    }

    [Fact]
    public async Task WithoutASamplerSensorRoutinesAreStillRefused()
    {
        var bench = new RoutineHostTestBench();
        await bench.InitializeAsync();

        var result = await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(), NotifyPhone()));

        Assert.Equal(RoutineReasonCodes.SensorUnavailable, Assert.Single(result.Results!).ReasonCode);
        Assert.False(bench.Availability.SensorAvailable);
    }

    [Fact]
    public async Task AnArmedSensorRoutineHoldsDemandAndPauseReleasesIt()
    {
        var bench = new RoutineHostTestBench(withTelemetry: true);
        await bench.InitializeAsync();
        bench.Telemetry!.Publish((FakeTelemetry.GpuTemp, 60));
        Assert.Equal(0, bench.Telemetry.Leases);

        await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(), NotifyPhone()));
        Assert.Equal(1, bench.Telemetry.Leases);

        await bench.Service.SetHostPausedAsync(true);
        Assert.Equal(0, bench.Telemetry.Leases);
    }

    [Fact]
    public async Task AMissingSensorShowsWaitingOnThePcPageUntilItReportsAgain()
    {
        var bench = new RoutineHostTestBench(withTelemetry: true);
        await bench.InitializeAsync();
        bench.Telemetry!.Publish((FakeTelemetry.GpuTemp, 60));
        await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(), NotifyPc()));

        bool Waiting() => bench.Service.GetSnapshot().Owners.Single().Routines.Single().WaitingForSensor;
        for (var i = 0; i <= 10; i++)
        {
            bench.Telemetry.Publish((FakeTelemetry.RamLoad, 40));
            bench.Time.Advance(TimeSpan.FromMinutes(1));
        }

        bench.Telemetry.Publish((FakeTelemetry.RamLoad, 40));
        Assert.True(Waiting());
        await WaitUntilAsync(
            () => bench.Runs.Query(Owner, Id(1)).Any(r => r.ReasonCode == RoutineReasonCodes.SensorUnavailable),
            "no sensor_unavailable record");

        bench.Telemetry.Publish((FakeTelemetry.GpuTemp, 60));
        Assert.False(Waiting());
    }

    [Fact]
    public async Task ASustainedBreachStartsARun()
    {
        var bench = new RoutineHostTestBench(withTelemetry: true);
        await bench.InitializeAsync();
        bench.Telemetry!.Publish((FakeTelemetry.GpuTemp, 60));
        await bench.SyncAsync(1, routines: Routine(1, SensorTrigger(sustain: 5), NotifyPc("hot")));

        for (var i = 0; i < 7; i++)
        {
            bench.Telemetry.Publish((FakeTelemetry.GpuTemp, 95));
            bench.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await WaitUntilAsync(() => bench.Runs.Query(Owner, Id(1)).Any(r => r.Outcome == RoutineRunOutcomes.Succeeded), "the sensor run never finished");
        var run = bench.Runs.Query(Owner, Id(1)).First();
        Assert.Equal(RoutineRunSources.PcSensor, run.Source);
        Assert.Equal("GPU temperature", run.SourceDetail!.SensorName);
    }
}
