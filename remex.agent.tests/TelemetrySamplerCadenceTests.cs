using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Remex.Agent.Services.Telemetry;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The sampler's interval no longer includes however long the sample took (RemEx-6sibx).
/// </summary>
/// <remarks>
/// <para>
/// The loop used to end in <c>await Task.Delay(1000, ct)</c>, which makes the period the sample
/// duration PLUS a second. That duration varies - WMI can block for seconds where an hwmon read is
/// instant - so samples landed at uneven intervals. Since RemEx-uj7s the sampler is the only clock in
/// the subsystem, so the unevenness is delivered verbatim to every client, and the phone appends one
/// history point per message against an INDEX axis: even spacing is precisely what makes its x-axis
/// linear in time.
/// </para>
/// <para>
/// These run on a fake clock (<see cref="SamplerRun"/>): every sample takes exactly half a period of
/// virtual time, so a trailing delay or a lost period shows up as an exact gap rather than a threshold
/// a loaded machine could blur. The cadence is pinned in absolute terms on purpose - it sets telemetry
/// bandwidth and the phone's history resolution, so changing the rate SHOULD fail here.
/// </para>
/// </remarks>
public class TelemetrySamplerCadenceTests
{
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

    [Fact]
    public async Task SamplesLandOnePeriodApartRegardlessOfHowLongTheSampleTakes()
    {
        // THE BEAD. With a 500ms sample and a 1s period a trailing delay lands publishes 1.5s apart
        // and a periodic timer 1.0s apart. A dropped period would start the next sample at once, so
        // the gap would be the 500ms of work.
        var clock = new FakeTimeProvider();
        var source = new GatedTelemetryService();
        using var sampler = new TelemetryBackgroundService(
            source, NullLogger<TelemetryBackgroundService>.Instance) { Clock = clock };

        var times = await SamplerRun.PublishTimesAsync(sampler, source, clock, samples: 4);

        Assert.Equal(SamplerRun.ExpectedPublishTimes(4), times);
    }

    [Fact]
    public async Task TheFirstSampleStartsWithoutWaitingOutATick()
    {
        // ANTI-REGRESSION FOR THE OBVIOUS WAY TO WRITE THIS WRONG. `while (await
        // ticker.WaitForNextTickAsync(ct)) { sample(); }` reads better and costs the host its first
        // second: nothing is published until the timer's first tick, so a client connecting in that
        // window has no reading to be sent and the PC's own dashboard opens empty. The clock is never
        // advanced here, so only a sampler that samples first and waits second gets this far.
        var clock = new FakeTimeProvider();
        var source = new GatedTelemetryService();
        using var sampler = new TelemetryBackgroundService(
            source, NullLogger<TelemetryBackgroundService>.Instance) { Clock = clock };

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await source.SampleStartedAsync();
            source.FinishSample();
            await sampler.WaitForNextSnapshotAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ShuttingDownWhileParkedBetweenSamplesAlsoEndsTheLoopCleanly()
    {
        // THE OTHER HALF OF SHUTDOWN, AND THE ONE PRODUCTION ACTUALLY HITS. Real samples are fast, so
        // the loop is almost always parked in WaitForNextTickAsync rather than inside a sample - a
        // different path through the same catch, where the wait is interrupted rather than entered
        // cancelled. The clock stays put after the publish, so no tick ever arrives.
        var logger = new CapturingLogger();
        var clock = new FakeTimeProvider();
        var source = new GatedTelemetryService();
        using var sampler = new TelemetryBackgroundService(source, logger) { Clock = clock };

        await sampler.StartAsync(CancellationToken.None);
        await source.SampleStartedAsync();
        source.FinishSample();
        await sampler.WaitForNextSnapshotAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
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
        // diagnostic export to work out whether the sampler was alive. Cancelled DURING a sample here:
        // the sample never finishes, the inner catch swallows the cancellation and the wait then
        // throws on an already-cancelled token.
        var logger = new CapturingLogger();
        var source = new GatedTelemetryService();
        using var sampler = new TelemetryBackgroundService(source, logger) { Clock = new FakeTimeProvider() };

        await sampler.StartAsync(CancellationToken.None);
        await source.SampleStartedAsync();
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
