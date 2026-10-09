using Microsoft.Extensions.Time.Testing;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// A telemetry source whose sample "takes" however long the test says: it announces that a sample
/// started and finishes only when released, so no real time passes.
/// </summary>
internal sealed class GatedTelemetryService : ITelemetryService
{
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _finish = new(0);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref _calls);
        _started.Release();
        await _finish.WaitAsync(ct);
        return new TelemetryPayload
        {
            Sensors = [new SensorReading { Name = "Total CPU Usage", Value = n, Unit = "%", Source = "Test" }],
        };
    }

    /// <summary>Completes when the sampler begins a sample; fails the test if it never does.</summary>
    public async Task SampleStartedAsync()
    {
        Assert.True(await _started.WaitAsync(TimeSpan.FromSeconds(5)), "the sampler never started the expected sample");
    }

    public void FinishSample() => _finish.Release();
}

/// <summary>
/// Runs a sampler on a fake clock where every sample takes <see cref="Work"/> of virtual time, and
/// reports when each publish landed. Replaces the old wall-clock cadence measurements.
/// </summary>
internal static class SamplerRun
{
    /// <summary>How long each sample takes. Half the period, so a trailing delay would show as 1.5 s gaps.</summary>
    public static readonly TimeSpan Work = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The cadence the phone's history axis depends on. Literal rather than derived from the production
    /// period, so changing that period fails here instead of moving the expectation with it.
    /// </summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    public static TimeSpan[] ExpectedPublishTimes(int samples) =>
        [.. Enumerable.Range(0, samples).Select(i => Work + i * Cadence)];

    /// <summary>
    /// Starts <paramref name="sampler"/>, lets it take <paramref name="samples"/> samples and returns the
    /// virtual time of each publish. The test waits for a sample to start before advancing, so a sampler
    /// that does not start its next sample on the period boundary fails with a timeout, not a flake.
    /// </summary>
    public static async Task<TimeSpan[]> PublishTimesAsync(
        TelemetryBackgroundService sampler, GatedTelemetryService source, FakeTimeProvider clock, int samples)
    {
        var start = clock.GetUtcNow();
        var times = new List<TimeSpan>();
        var published = new SemaphoreSlim(0);
        sampler.TelemetryPublished += _ =>
        {
            lock (times) times.Add(clock.GetUtcNow() - start);
            published.Release();
        };

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < samples; i++)
            {
                await source.SampleStartedAsync();
                clock.Advance(Work);
                source.FinishSample();
                Assert.True(await published.WaitAsync(TimeSpan.FromSeconds(5)), "the sample finished but nothing was published");
                if (i < samples - 1)
                    clock.Advance(Cadence - Work);
            }
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        lock (times) return [.. times];
    }
}
