using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// P1-11: the cursor loop slows from 90 Hz to 30 Hz when idle, snaps back on the first activity,
/// and keeps its ClipCursor / animated-shape slow tick at ~10 Hz wall-clock at either rate.
/// Pure state machine, so no real timing is involved.
/// </summary>
public class CursorPollCadenceTests
{
    [Fact]
    public void StaysAtFullRateUntilTheIdleThreshold()
    {
        var cadence = new CursorPollCadence();

        for (var i = 1; i < CursorPollCadence.IdleTicksBeforeSlowdown; i++)
        {
            Assert.Equal(CursorPollCadence.ActiveIntervalMs, cadence.NextIntervalMs(activity: false));
        }

        Assert.Equal(CursorPollCadence.IdleIntervalMs, cadence.NextIntervalMs(activity: false));
        Assert.True(cadence.IsIdle);
    }

    [Fact]
    public void SnapsBackToFullRateOnTheFirstActivity()
    {
        var cadence = new CursorPollCadence();
        for (var i = 0; i < CursorPollCadence.IdleTicksBeforeSlowdown * 3; i++)
        {
            cadence.NextIntervalMs(activity: false);
        }
        Assert.True(cadence.IsIdle);

        Assert.Equal(CursorPollCadence.ActiveIntervalMs, cadence.NextIntervalMs(activity: true));
        Assert.False(cadence.IsIdle);

        // And a fresh still period has to elapse again before slowing down.
        Assert.Equal(CursorPollCadence.ActiveIntervalMs, cadence.NextIntervalMs(activity: false));
    }

    [Fact]
    public void ActivityInsideTheWindowRestartsIt()
    {
        var cadence = new CursorPollCadence();
        for (var i = 0; i < CursorPollCadence.IdleTicksBeforeSlowdown - 1; i++)
        {
            cadence.NextIntervalMs(activity: false);
        }

        cadence.NextIntervalMs(activity: true);

        for (var i = 1; i < CursorPollCadence.IdleTicksBeforeSlowdown; i++)
        {
            Assert.Equal(CursorPollCadence.ActiveIntervalMs, cadence.NextIntervalMs(activity: false));
        }
    }

    [Fact]
    public void IdleRateIsSlowerAndActiveRateIsStill90Hz()
    {
        Assert.Equal(1000.0 / 90.0, CursorPollCadence.ActiveIntervalMs, precision: 9);
        Assert.True(CursorPollCadence.IdleIntervalMs > CursorPollCadence.ActiveIntervalMs);
        // The idle rate still has to be fast enough that the first movement is not visibly late.
        Assert.True(CursorPollCadence.IdleIntervalMs <= 50.0);
    }

    [Fact]
    public void FirstCallIsAlwaysASlowTick()
    {
        Assert.True(new CursorPollCadence().TryConsumeSlowTick(0));
    }

    [Theory]
    [InlineData(CursorPollCadence.ActiveIntervalMs, 9)]
    [InlineData(CursorPollCadence.IdleIntervalMs, 3)]
    public void SlowTickHoldsAtTenHertzAtEitherTickRate(double intervalMs, int ticksPerSlowTick)
    {
        // Feed the exact absolute timeline the pacer produces (n * interval, which at 90 Hz lands the
        // 9th tick at 99.999... ms) and check the slow tick falls on every Nth tick, no earlier.
        var cadence = new CursorPollCadence();
        var fired = new List<int>();

        for (var n = 0; n <= ticksPerSlowTick * 10; n++)
        {
            if (cadence.TryConsumeSlowTick(n * intervalMs)) fired.Add(n);
        }

        Assert.Equal(Enumerable.Range(0, 11).Select(k => k * ticksPerSlowTick), fired);
    }

    [Fact]
    public void ForceSlowTickFiresOnTheNextCall()
    {
        var cadence = new CursorPollCadence();
        Assert.True(cadence.TryConsumeSlowTick(1000));
        Assert.False(cadence.TryConsumeSlowTick(1010));

        cadence.ForceSlowTick();

        Assert.True(cadence.TryConsumeSlowTick(1011));
    }
}
