using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Models;

namespace Remex.Agent.Services.Media;

/// <summary>
/// What the PC is playing, sampled once for the whole process and handed to every client (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// ONE SAMPLER, NOT ONE PER CONNECTION, for the reason <c>TelemetryBackgroundService</c> already
/// records: a per-connection poll means N calls into the media API per second for N phones, and N
/// clocks drifting against each other so that two phones watching the same PC disagree about when it
/// started playing.
/// </para>
/// <para>
/// A NEW SNAPSHOT IS PUBLISHED ONLY WHEN THE READING CHANGES BY VALUE, and that is the load-bearing
/// line rather than an optimisation. <see cref="TelemetrySnapshotGate{T}"/> defines "already sent" as
/// REFERENCE equality, so publishing an equal-but-new instance every second would wake every parked
/// client stream and push an identical envelope down every socket, once a second, forever — a phone
/// sitting on the Remote Control screen would receive a steady trickle of news that nothing had
/// happened. <see cref="MediaPlaybackState"/> is a record precisely so that this comparison is the
/// obvious one-liner it looks like.
/// </para>
/// </remarks>
/// <remarks>
/// PUBLIC WHILE <see cref="IMediaSessionReader"/> STAYS INTERNAL, and the split is the point rather
/// than an accident of the compiler: this is the abstraction consumers name — <c>PingPongHandler</c>
/// is public and takes it — while the reader is a platform detail nothing outside this folder should
/// be able to reach for.
/// </remarks>
public interface IMediaSessionMonitor
{
    /// <summary>Whether this host can read a media session at all.</summary>
    bool IsSupported { get; }

    /// <summary>The newest reading, or null before the first poll completes.</summary>
    MediaPlaybackState? Current { get; }

    /// <summary>Waits for a reading the caller has not already sent.</summary>
    Task<MediaPlaybackState> WaitForNextAsync(MediaPlaybackState? alreadySent, CancellationToken ct);
}

/// <inheritdoc cref="IMediaSessionMonitor"/>
internal sealed class MediaSessionBackgroundService(
    IMediaSessionReader reader,
    ILogger<MediaSessionBackgroundService> logger) : BackgroundService, IMediaSessionMonitor
{
    /// <summary>
    /// How often the media session is read.
    /// </summary>
    /// <remarks>
    /// A SECOND IS CHOSEN FOR THE FEEL OF THE BUTTON, not for the cost of the call. This is the
    /// worst-case lag between pressing play at the PC and the phone's icon agreeing, and anything
    /// slower reads as the icon being broken again — which is the exact impression this feature
    /// exists to remove. It matches the telemetry sampler, so the two do not beat against each other.
    /// </remarks>
    internal static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(1);

    private readonly TelemetrySnapshotGate<MediaPlaybackState> _gate = new();

    public bool IsSupported => reader.IsSupported;

    public MediaPlaybackState? Current => _gate.Current;

    public Task<MediaPlaybackState> WaitForNextAsync(MediaPlaybackState? alreadySent, CancellationToken ct)
        => _gate.WaitForNextAsync(alreadySent, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!reader.IsSupported)
        {
            // NOTHING IS PUBLISHED, WHICH IS NOT THE SAME AS PUBLISHING "unknown". A host that cannot
            // read says so once, in HostCapabilities, and then stays quiet; clients gate on the
            // capability. Publishing an Unknown every poll would put a message on every socket to
            // report a fact that never changes and was already answered on connect.
            logger.LogInformation("Media session reporting is not available on this host.");
            return;
        }

        logger.LogInformation("Media session sampler started.");

        // A PeriodicTimer rather than a trailing delay, the same choice and the same reasoning as the
        // telemetry sampler: a trailing delay makes the period the read duration PLUS a second, and
        // the read duration varies with whichever media player is running.
        using var timer = new PeriodicTimer(PollPeriod);

        MediaPlaybackState? lastPublished = null;

        do
        {
            MediaPlaybackState reading;
            try
            {
                reading = await reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The reader's contract says it does not throw, so reaching here means it broke that
                // contract. Log it and keep sampling rather than ending the loop: a sampler that dies
                // silently presents as the icon freezing, which is indistinguishable from the original
                // bug. Not published as Unknown, because one bad poll should not blank a good reading.
                logger.LogWarning(ex, "Media session read threw; the reader is meant to swallow this. Continuing.");
                continue;
            }

            // VALUE comparison, feeding a gate that tests REFERENCE equality. See the class remarks.
            if (reading != lastPublished)
            {
                _gate.Publish(reading);
                lastPublished = reading;
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Waits for the next tick, reporting cancellation as "stop" rather than as an exception.
    /// </summary>
    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
