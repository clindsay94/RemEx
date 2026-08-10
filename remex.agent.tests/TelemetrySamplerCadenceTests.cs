using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The sampler's interval no longer includes however long the sample took (RemEx-6sibx).
/// </summary>
/// <remarks>
/// <para>
/// The loop used to end in <c>await Task.Delay(1000, ct)</c>, which makes the period the sample
/// duration PLUS a second. That duration varies — WMI can block for seconds where an hwmon read is
/// instant — so samples landed at uneven intervals. Since RemEx-uj7s the sampler is the only clock in
/// the subsystem, so the unevenness is delivered verbatim to every client, and the phone appends one
/// history point per message against an INDEX axis: even spacing is precisely what makes its x-axis
/// linear in time.
/// </para>
/// <para>
/// **THESE ARE TIMING TESTS, WHICH ARE ONLY WORTH WRITING WHEN THE TWO DESIGNS ARE SEPARATED BY
/// CONSTRUCTION RATHER THAN BY LUCK.** They are: under a trailing delay the gap between publishes is
/// at least the period plus the sample duration ALWAYS, because the code sums them — so with a sample
/// deliberately made slow, no scheduling jitter can produce a short gap. Every bound here is therefore
/// chosen so that load moves the measurement AWAY from failure: the gap ceiling can only be
/// approached from below by a slow machine, and the publish count can only be reduced by one.
/// </para>
/// <para>
/// **THE GAP ASSERTION IS ON THE MEAN, AND THE OPPOSITE MISTAKE IS CAUGHT BY COUNTING RATHER THAN BY
/// A LOWER BOUND ON THAT MEAN.** An earlier draft asserted only that SOME gap was short, which
/// uniformity implies but does not imply — a sampler alternating 1s and 2s spacing, the exact defect
/// this bead removes, satisfied it. A floor on the mean was the next draft and looked symmetrical
/// without being one-sided: the mean telescopes to <c>period + (Dlast - Dfirst)/(n-1)</c> only while
/// every tick is consumed, and a sample overrunning the period coalesces a tick away — which breaks
/// that identity and starts the next sample with no idle, so one stalled sample early in a run drags
/// the mean under a floor with nothing wrong. Counting publishes has no such failure mode.
/// </para>
/// </remarks>
public class TelemetrySamplerCadenceTests
{
    /// <summary>How long each sample deliberately takes.</summary>
    /// <remarks>
    /// Half the period, so a trailing delay would space publishes ~1.5s apart against the timer's
    /// ~1.0s. Much smaller narrows the margin the assertions rely on; much larger would make dropped
    /// ticks the thing under test instead of the interval.
    /// </remarks>
    private static readonly TimeSpan SampleWork = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The production period, used only where the assertion is genuinely RELATIVE to it — that the
    /// first sample does not wait out a tick. The cadence bounds below are deliberately absolute; see
    /// the comment there for why deriving them from this made the test blind to its own subject.
    /// </summary>
    private static readonly TimeSpan Period = TelemetryBackgroundService.SamplePeriod;

    private sealed class SlowTelemetryService : ITelemetryService
    {
        private int _calls;

        public async Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
        {
            await Task.Delay(SampleWork, ct);
            var n = Interlocked.Increment(ref _calls);
            return new TelemetryPayload
            {
                Sensors = [new SensorReading { Name = "Total CPU Usage", Value = n, Unit = "%", Source = "Test" }],
            };
        }
    }

    /// <summary>Records formatted log messages so a shutdown claim can be asserted rather than assumed.</summary>
    private sealed class CapturingLogger : ILogger<TelemetryBackgroundService>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return [.. _messages]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, ex));
        }
    }

    /// <summary>Runs the sampler for <paramref name="window"/> and returns when each publish landed.</summary>
    private static async Task<List<TimeSpan>> PublishTimesAsync(TimeSpan window)
    {
        using var sampler = new TelemetryBackgroundService(
            new SlowTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance);

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
            await Task.Delay(window);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        lock (times) return [.. times];
    }

    [Fact]
    public async Task SamplesLandOnePeriodApartRegardlessOfHowLongTheSampleTakes()
    {
        // THE BEAD. With a 500ms sample and a 1s period, a trailing delay averages ~1.5s between
        // publishes and a periodic timer ~1.0s, so the gap ceiling below is unreachable under a
        // trailing delay - every gap is period + work BY CONSTRUCTION, so the mean is too. The count
        // assertion catches the opposite mistake, a period shortened or dropped, where ticks coalesce
        // every iteration and the loop samples back to back.
        var times = await PublishTimesAsync(TimeSpan.FromSeconds(4));

        Assert.True(times.Count >= 2, $"need two publishes to have a gap; got {times.Count}");

        var meanGap = (times[^1] - times[0]) / (times.Count - 1);

        // **THE BOUNDS ARE LITERAL, AND AN EARLIER DRAFT DERIVED THEM FROM SamplePeriod INSTEAD.**
        // That looked tidier and was blind: mutating the period moved the thresholds with it, so
        // doubling it to 2s produced a 2.0s mean against a ceiling that had itself become 2.4s, and
        // the test passed. A test that computes its expectation from the value
        // under test is only ever asserting its own consistency. The observed cadence is a user-facing
        // fact - it sets telemetry bandwidth and the phone's history resolution - so it is pinned in
        // absolute terms, and RemEx-jyuem changing the rate SHOULD fail here and be updated knowingly.
        Assert.True(
            meanGap < TimeSpan.FromMilliseconds(1400),
            $"mean gap {meanGap.TotalMilliseconds:F0}ms suggests the sample duration is still in the interval");

        // **THE OPPOSITE MISTAKE IS CAUGHT BY COUNTING, NOT BY A LOWER BOUND ON THE MEAN.** A floor on
        // the mean looked symmetrical and was not one-sided: a sample overrunning the period coalesces
        // a tick away, which breaks the telescoping the mean relies on AND starts the next sample with
        // no idle, so a single stalled sample early in the run drags the mean under a floor with
        // nothing wrong. A count can only be REDUCED by load, never inflated, so it fails in the safe
        // direction the way the ceiling does. Four seconds at 1 Hz is ~4 publishes; a period shortened
        // or dropped makes the loop sample back to back, bounded only by the fake's 500 ms of work,
        // which is ~8.
        Assert.True(
            times.Count <= 6,
            $"{times.Count} publishes in 4s is faster than the intended 1 Hz");
    }

    [Fact]
    public async Task TheFirstSampleIsPublishedWithoutWaitingOutATick()
    {
        // ANTI-REGRESSION FOR THE OBVIOUS WAY TO WRITE THIS WRONG. `while (await
        // ticker.WaitForNextTickAsync(ct)) { sample(); }` reads better and costs the host its first
        // second: nothing is published until the timer's first tick, so a client connecting in that
        // window has no reading to be sent and the PC's own dashboard opens empty. Sampling first and
        // waiting second is what makes the startup case free.
        //
        // The window is deliberately wider than the period. At exactly one period the mutant publishes
        // nothing at all and Assert.NotEmpty is what fails - which passes for the wrong reason and
        // leaves a sliver where a slow start fails the threshold instead. Two periods lets the mutant
        // publish at ~1.5s and be caught by the assertion that names the property.
        var times = await PublishTimesAsync(Period * 2);

        Assert.NotEmpty(times);
        Assert.True(
            times[0] < Period,
            $"the first sample should not wait out a tick; it landed at {times[0].TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task ShuttingDownWhileParkedBetweenSamplesAlsoEndsTheLoopCleanly()
    {
        // THE OTHER HALF OF SHUTDOWN, AND THE ONE PRODUCTION ACTUALLY HITS. The test below cancels
        // DURING a sample, where the inner catch swallows the cancellation and the wait then throws on
        // an already-cancelled token. Real samples are fast, so at any given moment the loop is almost
        // always parked in WaitForNextTickAsync instead - a different path through the same catch,
        // where the wait is interrupted rather than entered cancelled. A 700ms window against a 500ms
        // sample lands there: one publish at ~500ms, then parked until the 1s tick that never comes.
        var logger = new CapturingLogger();
        using var sampler = new TelemetryBackgroundService(new SlowTelemetryService(), logger);

        await sampler.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        await sampler.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("stopped", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            sampler.ExecuteTask!.IsCompletedSuccessfully,
            $"the loop should end by returning, not by throwing; status was {sampler.ExecuteTask.Status}");
    }

    [Fact]
    public async Task ShuttingDownIsLoggedAndEndsTheLoopCleanly()
    {
        // **THE OLD TRAILING `Task.Delay(1000, stoppingToken)` WAS UNCAUGHT**, so on shutdown it threw
        // straight out of ExecuteAsync and the "stopped" line below it never ran - leaving a log that
        // recorded the sampler starting and never stopping, on every run, for anyone reading a
        // diagnostic export to work out whether the sampler was alive.
        //
        // This is the half of RemEx-6sibx that the cadence tests cannot see: revert the catch and both
        // of them stay green, because neither inspects the logger or the task's final state.
        var logger = new CapturingLogger();
        using var sampler = new TelemetryBackgroundService(new SlowTelemetryService(), logger);

        await sampler.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await sampler.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("stopped", StringComparison.OrdinalIgnoreCase));

        // Paired with the log assertion so neither can pass alone: the loop must EXIT, not fault. A
        // cancelled or faulted ExecuteTask is what an escaping OperationCanceledException looks like.
        Assert.NotNull(sampler.ExecuteTask);
        Assert.True(
            sampler.ExecuteTask!.IsCompletedSuccessfully,
            $"the loop should end by returning, not by throwing; status was {sampler.ExecuteTask.Status}");
    }
}
