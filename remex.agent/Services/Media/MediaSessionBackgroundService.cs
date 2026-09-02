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

    /// <summary>The image bytes behind an <c>ArtworkId</c> a reading carried, or null when this host
    /// never resolved that id or has since evicted it.</summary>
    byte[]? TryGetArtwork(string artworkId);

    /// <summary>
    /// Moves the current session to <paramref name="positionMs"/> milliseconds from the start of the
    /// track, answering whether the platform reported the move as accepted (RemEx-vtorl).
    /// </summary>
    /// <remarks>
    /// <para>
    /// FALSE IS A NORMAL ANSWER AND NOTHING IS THROWN. A host with no seekable session — every
    /// platform but Windows and Linux, and Windows sessions whose player declines the call — answers
    /// false through <see cref="NullMediaSeekTarget"/>. This is reached from the per-connection
    /// message loop, so an exception here would drop a phone's whole connection to answer a scrubber
    /// drag.
    /// </para>
    /// <para>
    /// IT PUBLISHES NOTHING, WHICH IS THE PART WORTH SAYING OUT LOUD. No anchor is stamped and the
    /// gate is not touched: the next poll reads the moved position, the tracker re-anchors because
    /// the reading diverged past tolerance, and the gate publishes one <c>media_state</c> to every
    /// client. That publish is the reply, and it is the only one — a seek that the player ignored
    /// therefore produces no message at all, which is exactly what the phone needs in order to notice
    /// and put its own optimistic position back.
    /// </para>
    /// </remarks>
    Task<bool> TrySeekAsync(long positionMs, CancellationToken ct);
}

/// <inheritdoc cref="IMediaSessionMonitor"/>
/// <remarks>
/// ARTWORK RESOLUTION IS KEYED TO TRACK IDENTITY, NOT TO THE POLL TICK. <c>(Title, Artist,
/// SourceApp)</c> changing is what starts a new resolve-and-cancel-the-old-one; an unrelated field
/// changing (status, a position anchor) leaves whatever id is already resolved alone. The id is only
/// ever made visible to a poll AFTER <see cref="IMediaArtworkStore.Put"/> has returned it — the
/// store-before-publish invariant — so a phone that asks for an id from a fresh <c>media_state</c>
/// never races the store that is supposed to answer it.
/// </remarks>
internal sealed class MediaSessionBackgroundService(
    IMediaSessionReader reader,
    IMediaArtworkSource artworkSource,
    IMediaArtworkStore artworkStore,
    IMediaSeekTarget seekTarget,
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

    private readonly object _artworkLock = new();
    private bool _hasArtworkIdentity;
    private (string? Title, string? Artist, string? SourceApp) _artworkIdentity;
    private long _artworkGeneration;
    private string? _currentArtworkId;
    private CancellationTokenSource? _artworkCts;
    private CancellationToken _stoppingToken;

    public bool IsSupported => reader.IsSupported;

    public MediaPlaybackState? Current => _gate.Current;

    public Task<MediaPlaybackState> WaitForNextAsync(MediaPlaybackState? alreadySent, CancellationToken ct)
        => _gate.WaitForNextAsync(alreadySent, ct);

    public byte[]? TryGetArtwork(string artworkId) => artworkStore.TryGet(artworkId);

    /// <inheritdoc />
    /// <remarks>
    /// STRAIGHT THROUGH, WITH NO BOOKKEEPING ON THE WAY. The temptation is to stamp the requested
    /// position onto the gate here so the phone hears back instantly, and it is exactly the mistake:
    /// the host would then be reporting a position nobody has confirmed a player moved to, and a
    /// session that ignored the seek would leave every connected phone showing a number the PC is not
    /// playing until the next reading happened to differ.
    /// </remarks>
    public Task<bool> TrySeekAsync(long positionMs, CancellationToken ct)
        => seekTarget.TrySeekAsync(positionMs, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

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

            reading = ApplyArtwork(reading);

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

    /// <summary>
    /// Merges the current resolved artwork id onto <paramref name="reading"/>, kicking off a fresh
    /// resolution (and cancelling any resolution still running for the previous track) exactly when
    /// track identity changes.
    /// </summary>
    private MediaPlaybackState ApplyArtwork(MediaPlaybackState reading)
    {
        var identity = (reading.Title, reading.Artist, reading.SourceApp);
        string? artworkId;
        MediaPlaybackState? toResolve = null;
        long generation = 0;
        var resolveToken = CancellationToken.None;

        lock (_artworkLock)
        {
            if (!_hasArtworkIdentity || !_artworkIdentity.Equals(identity))
            {
                // A NEW TRACK IDENTITY CLEARS THE ID IMMEDIATELY, before anything has resolved for
                // it. The alternative — keeping the previous track's art up until a new one resolves
                // — is a stale cover under a new title, which is worse than the glyph it would
                // otherwise show for the ~1s the resolve takes.
                _artworkCts?.Cancel();
                _artworkCts?.Dispose();
                _artworkCts = null;

                _hasArtworkIdentity = true;
                _artworkIdentity = identity;
                _artworkGeneration++;
                _currentArtworkId = null;

                var eligible = (reading.Title is not null || reading.Artist is not null)
                    && reading.Status != MediaPlaybackStatus.None
                    && reading.Status != MediaPlaybackStatus.Unknown;

                if (eligible)
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
                    _artworkCts = cts;
                    generation = _artworkGeneration;
                    resolveToken = cts.Token;
                    toResolve = reading;
                }
            }

            artworkId = _currentArtworkId;
        }

        // Started OUTSIDE the lock: IMediaArtworkSource implementations are free to complete
        // synchronously (NullMediaArtworkSource does), and that would re-enter this same method's
        // completion path before the lock above was ever released.
        if (toResolve is not null)
        {
            _ = ResolveArtworkAsync(toResolve, generation, resolveToken);
        }

        return reading with { ArtworkId = artworkId };
    }

    /// <summary>
    /// Resolves artwork off the poll tick and, if this track identity is still the current one,
    /// stores it before making its id visible.
    /// </summary>
    private async Task ResolveArtworkAsync(MediaPlaybackState state, long generation, CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = await artworkSource.ResolveArtworkAsync(state, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The source's contract says it does not throw, so reaching here means it broke that
            // contract. Log and drop it: artwork is decoration on a feature whose real job is the
            // play/pause icon, and letting this take the sampler down would blank that too.
            logger.LogWarning(ex, "Artwork resolution threw; the source is meant to swallow this. Continuing.");
            return;
        }

        if (bytes is null || ct.IsCancellationRequested)
        {
            return;
        }

        // STORE BEFORE PUBLISH: the id below only becomes visible to the poll loop once Put has
        // returned it, so a phone that later asks for this id from a fresh media_state can never
        // race the store that is supposed to answer it.
        var id = artworkStore.Put(bytes);
        if (id is null)
        {
            return;
        }

        lock (_artworkLock)
        {
            // Only apply it if this is still the identity it was resolved for — a track change while
            // this was in flight already cleared the id and bumped the generation.
            if (_artworkGeneration == generation)
            {
                _currentArtworkId = id;
            }
        }
    }

    /// <summary>
    /// Cancels and disposes whatever artwork resolution is still in flight so a stopping sampler
    /// does not leave a resolve (a WinRT thumbnail drain, an https fetch) running past shutdown.
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_artworkLock)
        {
            _artworkCts?.Cancel();
            _artworkCts?.Dispose();
            _artworkCts = null;
        }

        return base.StopAsync(cancellationToken);
    }
}
