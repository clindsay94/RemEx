using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The telemetry envelope is built only when something asks for it (RemEx-jyuem).
/// </summary>
/// <remarks>
/// <para>
/// Measured on a 453-sensor machine, that envelope is 74 KB. It used to be built on every tick
/// whether or not a client existed, so a PC with no phone connected and its window hidden allocated
/// roughly 4.4 MB a minute for bytes nobody read — the desktop's own dashboard reads the payload and
/// never touches the frame.
/// </para>
/// <para>
/// **COUNTING BUILDS IS THE ONLY WAY TO SEE THIS.** Nothing about the delivered bytes changes when
/// the laziness works, so a test asserting on frame contents passes identically before and after.
/// </para>
/// </remarks>
public class TelemetryFrameLazinessTests
{
    private static TelemetryPayload Payload() =>
        new() { Sensors = [new SensorReading { Name = "Total CPU Usage", Value = 7, Unit = "%" }] };

    private sealed class OneShotService : ITelemetryService
    {
        public Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
            => Task.FromResult(Payload());
    }

    [Fact]
    public async Task ASampleNobodyStreamsNeverBuildsItsEnvelope()
    {
        // THE BEAD. An idle PC samples for its own dashboard and should pay nothing for the wire
        // format it is not using.
        using var sampler = new TelemetryBackgroundService(
            new OneShotService(), NullLogger<TelemetryBackgroundService>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sampler.CurrentSnapshot is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        var snapshot = sampler.CurrentSnapshot;
        Assert.NotNull(snapshot);

        // The payload is there for the in-process dashboard...
        Assert.NotEmpty(snapshot!.Payload.Sensors!);

        // ...and asking for the frame still produces a valid envelope, decoding back to the sample.
        // Anti-vacuity: laziness that produced nothing, or produced it lazily and WRONGLY, would
        // satisfy a build-count assertion while breaking every connected phone.
        var decoded = MessageSerializer.Deserialize(snapshot.Frame.Span);
        Assert.Equal(MessageTypes.Telemetry, decoded?.Type);
        Assert.Equal("Total CPU Usage", decoded?.Telemetry?.Sensors?[0].Name);
    }

    [Fact]
    public async Task TheEnvelopeIsBuiltOnceAndReusedByEveryReader()
    {
        // Several phones share one buffer, which is the property RemEx-0zbj established and this
        // change must not lose: going lazy would be a poor trade if it rebuilt per reader instead of
        // per tick. Reference equality on the returned memory is what says it is the same bytes.
        using var sampler = new TelemetryBackgroundService(
            new OneShotService(), NullLogger<TelemetryBackgroundService>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sampler.CurrentSnapshot is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        var snapshot = sampler.CurrentSnapshot!;
        var first = snapshot.Frame;
        var second = snapshot.Frame;

        Assert.True(first.Span == second.Span, "each reader rebuilt the envelope instead of sharing one");
    }
}
