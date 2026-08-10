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
    /// <remarks>
    /// The two travel as one object so a reader cannot observe a payload with the previous tick's
    /// bytes. Publishing them as separate fields would be a torn read waiting to happen, since the
    /// sampler writes on its own thread while every client stream reads on theirs. That still holds
    /// now the bytes are built on demand: the builder closes over THIS tick's payload and timestamp,
    /// so a late reader gets this sample's envelope, never a later one's.
    /// </remarks>
    public sealed class TelemetrySnapshot
    {
        private readonly Func<ReadOnlyMemory<byte>> _buildFrame;
        private readonly object _frameLock = new();
        private ReadOnlyMemory<byte>? _frame;

        internal TelemetrySnapshot(TelemetryPayload payload, Func<ReadOnlyMemory<byte>> buildFrame)
        {
            Payload = payload;
            _buildFrame = buildFrame;
        }

        /// <summary>The sample itself. Always present; costs nothing.</summary>
        public TelemetryPayload Payload { get; }

        /// <summary>
        /// The serialized envelope, built on first access and cached (RemEx-jyuem).
        /// </summary>
        /// <remarks>
        /// <para>
        /// **BUILT LAZILY BECAUSE AN IDLE PC WAS PAYING FOR IT EVERY SECOND AND NOBODY WAS READING
        /// IT.** Measured on a 453-sensor machine this envelope is 74 KB, and it used to be built on
        /// every tick whether or not a client existed - roughly 4.4 MB a minute allocated for nothing
        /// on a PC with no phone connected. The desktop's own dashboard never touches it; it reads
        /// <see cref="Payload"/>. The bytes exist purely for remote streams, so they are now built
        /// only if a remote stream asks.
        /// </para>
        /// <para>
        /// **NOT `Lazy&lt;T&gt;`, DELIBERATELY.** Its default ExecutionAndPublication mode CACHES AN
        /// EXCEPTION and rethrows it for the life of the object, so one transient serialization
        /// failure would leave this snapshot permanently unsendable while every later snapshot worked
        /// - a per-tick glitch turned into a permanent hole. Check-then-build under a lock stores
        /// nothing on the failing path, so the next reader simply tries again. Same hazard recorded on
        /// ExecutableMetadataCache (RemEx-qz5z3).
        /// </para>
        /// <para>
        /// Locked rather than raced because the build is expensive enough that two streams arriving
        /// together should not both do it, and because the field must not be published half-written.
        /// Read-only memory on purpose: this exact buffer goes to every connected socket, so handing
        /// out a mutable array would let one consumer corrupt every live stream.
        /// </para>
        /// </remarks>
        public ReadOnlyMemory<byte> Frame
        {
            get
            {
                lock (_frameLock)
                {
                    _frame ??= _buildFrame();
                    return _frame.Value;
                }
            }
        }
    }

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


    /// <summary>How often a sample is taken.</summary>
    internal static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry background broadcaster started.");

        // **A PeriodicTimer, NOT Task.Delay AT THE FOOT OF THE LOOP (RemEx-6sibx).** A trailing delay
        // makes the period the sample duration PLUS a second, and that duration varies - WMI can block
        // for seconds where an hwmon read is instant. Samples therefore landed at uneven intervals.
        // Since RemEx-uj7s this is the only clock in the subsystem, so that unevenness is delivered
        // verbatim to every client, and the phone appends one history point per message against an
        // INDEX axis: uniform spacing is exactly what makes its x-axis linear in time.
        //
        // **WHAT THAT BUYS IS AN AVERAGE, AND SAYING IT REMOVES THE SAMPLE DURATION WOULD BE TOO
        // STRONG.** A publish lands when the sample FINISHES while the timer paces when it STARTS, so
        // the gap is `period + (Dn - Dn-1)`. The LEVEL of the duration is gone - the mean is now the
        // period no matter how slow the sensors are, where before it was the period plus that level -
        // but the CHANGE in duration between consecutive samples still passes through one for one.
        // That residue is far smaller than the level, and it does not accumulate.
        //
        // PeriodicTimer does NOT queue missed ticks; they coalesce into one. A sample slower than the
        // period drops ticks rather than bursting to catch up, which is what this wants - a backlog of
        // stale telemetry has no value and the burst would be a second kind of uneven spacing.
        //
        // **THE OTHER EDGE OF THAT COALESCING IS DELIBERATE AND IS A REAL CHANGE FOR SLOW MACHINES
        // (RemEx-c2bxf).** A saved tick means the wait returns immediately, so a sample taking longer
        // than the period is followed by NO idle at all and the next one starts back to back. The old
        // trailing delay guaranteed a second of quiet between polls whatever the poll cost. A machine
        // whose sensors take ~1.1s therefore moves from polling every 2.1s to every 1.1s: closer to
        // the 1 Hz everyone else already gets, which is the intent, but up to twice the polls and no
        // pause. The amplification is bounded at 2x, worst when the duration is near the period, and
        // decays toward 1x as it grows. Whether a slow machine should instead be given a floor is a
        // policy question with a number nobody has measured, so it is filed rather than guessed.
        using var ticker = new PeriodicTimer(SamplePeriod);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = await telemetryService.GetTelemetryAsync(stoppingToken);

                // Serialized ONCE per tick rather than once per connected client, and now only if a
                // client actually asks (RemEx-jyuem). Measured at 74 KB on a 453-sensor machine, so
                // building it per client was an allocation per stream per second - and building it
                // eagerly meant an idle PC with no phone connected paid ~4.4 MB a minute for bytes
                // nobody read. The desktop's own dashboard reads Payload, never Frame.
                //
                // THE TIMESTAMP IS CAPTURED HERE, NOT INSIDE THE BUILDER, so the envelope still says
                // when the sample was TAKEN rather than when some later stream happened to ask for
                // it. Sharing the bytes means sharing that timestamp, which changes its meaning from
                // "when this was sent" to "when this was sampled". Nothing reads it: the only
                // consumer of RemexMessage.Timestamp anywhere is the Pong round-trip measurement,
                // which echoes the SENDER's value on a different message type entirely. Sample time
                // is also the more truthful thing for a telemetry frame to carry. (RemEx-0zbj)
                var sampledAt = System.Diagnostics.Stopwatch.GetTimestamp();

                var snapshot = new TelemetrySnapshot(
                    payload,
                    () => MessageSerializer.Serialize(new RemexMessage
                    {
                        Type = MessageTypes.Telemetry,
                        Telemetry = payload,
                        Timestamp = sampledAt,
                    }));
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

            try
            {
                // False means the timer was disposed; cancellation throws. Both are shutdown.
                if (!await ticker.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                // **CAUGHT SO THE LINE BELOW CAN ACTUALLY RUN.** The old `await Task.Delay(1000,
                // stoppingToken)` sat uncaught at the foot of the loop, so on shutdown it threw
                // straight out of ExecuteAsync and the "stopped" message was unreachable - leaving a
                // log that recorded the sampler starting and never stopping. (Strictly, it survived a
                // microseconds-wide window where cancellation landed between the delay completing and
                // the loop re-checking the token; in practice, never.)
                break;
            }
        }

        logger.LogInformation("Telemetry background broadcaster stopped.");
    }
}
