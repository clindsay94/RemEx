using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Agent.Services.Telemetry;

/// <summary>
/// A background service that polls telemetry data periodically and caches the latest payload.
/// This prevents redundant system scans when multiple clients are connected.
/// </summary>
public sealed class TelemetryBackgroundService(
    ITelemetryService telemetryService,
    ILogger<TelemetryBackgroundService> logger) : BackgroundService, ITelemetryBroadcaster
{
    /// <summary>
    /// One sample and the exact bytes that carry it, published together.
    /// </summary>
    /// <param name="Payload">The sample, for in-process consumers.</param>
    /// <param name="Frame">
    /// The fully serialized <see cref="MessageTypes.Telemetry"/> envelope for that sample. Exposed
    /// as read-only memory on purpose: this exact buffer goes to every connected socket, so handing
    /// out a mutable array would let one in-process consumer corrupt every live stream.
    /// </param>
    /// <remarks>
    /// The two travel as one object so a reader cannot observe a payload with the previous tick's
    /// bytes. Publishing them as separate fields would be a torn read waiting to happen, since the
    /// sampler writes on its own thread while every client stream reads on theirs.
    /// </remarks>
    public sealed record TelemetrySnapshot(TelemetryPayload Payload, ReadOnlyMemory<byte> Frame);

    private readonly TelemetrySnapshotGate<TelemetrySnapshot> _gate = new();

    /// <summary>
    /// The latest sample together with its serialized frame, or <see langword="null"/> before the
    /// first successful poll.
    /// </summary>
    public TelemetrySnapshot? CurrentSnapshot => _gate.Current;

    /// <summary>
    /// Waits until a sample exists that the caller has not already sent (RemEx-uj7s).
    /// </summary>
    /// <remarks>
    /// This is what makes client fan-out push-driven. Every stream used to run its own 1-second
    /// Task.Delay, which drifts against the sampler's 1000 ms PLUS sample duration - so a stream was
    /// systematically the faster loop, routinely found the sample it had already sent, and skipped.
    /// The result was update intervals of mostly one second with a two-second gap whenever the phase
    /// caught up: invisible on screen, but the phone plots history against an index axis rather than
    /// a time axis, so the gaps rendered as uniform and the chart's x-axis stopped being linear in
    /// time. Waiting on the sampler instead means every client receives exactly the samples that
    /// exist, in step.
    /// </remarks>
    public Task<TelemetrySnapshot> WaitForNextSnapshotAsync(TelemetrySnapshot? alreadySent, CancellationToken ct)
        => _gate.WaitForNextAsync(alreadySent, ct);

    /// <inheritdoc />
    public TelemetryPayload? CurrentTelemetry => CurrentSnapshot?.Payload;

    /// <inheritdoc />
    /// <remarks>
    /// A throwing handler is swallowed at the raise site: the UI is not allowed to stop the sampler,
    /// which every connected phone is also fed from.
    /// </remarks>
    public event Action<TelemetryPayload>? TelemetryPublished;


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry background broadcaster started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = await telemetryService.GetTelemetryAsync(stoppingToken);

                // Serialized ONCE here rather than once per connected client per second. With
                // HWiNFO running this envelope is 60-100 KB, so every extra client was another
                // Large Object Heap allocation every tick — and the PC's own dashboard is a client
                // too, so even a single phone means two.
                //
                // Sharing the bytes means sharing the envelope's Timestamp, which changes it from
                // "when this was sent" to "when this was sampled". Nothing reads it: the only
                // consumer of RemexMessage.Timestamp anywhere is the Pong round-trip measurement,
                // which echoes the SENDER's value on a different message type entirely. Sample time
                // is also the more truthful thing for a telemetry frame to carry. (RemEx-0zbj)
                var frame = MessageSerializer.Serialize(new RemexMessage
                {
                    Type = MessageTypes.Telemetry,
                    Telemetry = payload,
                    Timestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                });

                var snapshot = new TelemetrySnapshot(payload, frame);
                _gate.Publish(snapshot);

                try
                {
                    TelemetryPublished?.Invoke(snapshot.Payload);
                }
                catch (Exception ex)
                {
                    // A broken in-process subscriber must not take the sampler down with it — every
                    // connected phone is fed from this same loop.
                    logger.LogWarning(ex, "A telemetry subscriber threw; continuing to sample.");
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling telemetry data.");
            }

            // Poll every 1 second
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("Telemetry background broadcaster stopped.");
    }
}
