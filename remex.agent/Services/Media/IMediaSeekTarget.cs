namespace Remex.Agent.Services.Media;

/// <summary>
/// Moves the playback position of whatever this host is currently playing (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// SEPARATE FROM <see cref="IMediaSessionReader"/> FOR THE SAME REASON <see cref="IMediaArtworkSource"/>
/// IS: reading is something every platform can be asked to do, and this is not. A platform with no
/// seekable session implements nothing rather than returning false from a method it was forced to
/// have, and the sampler holds one dependency that always answers instead of a nullable one every
/// call site has to check.
/// </para>
/// <para>
/// IMPLEMENTED BY THE PLATFORM READERS THEMSELVES — <c>WindowsMediaSessionReader</c> and
/// <c>LinuxMediaSessionReader</c> — because the session to seek is the session the reading came
/// from, and nothing else in this process knows which player that is. DI therefore resolves this by
/// asking the registered reader whether it happens to be one, and falls back to
/// <see cref="NullMediaSeekTarget.Instance"/> when it is not:
/// <c>sp.GetRequiredService&lt;IMediaSessionReader&gt;() as IMediaSeekTarget ?? NullMediaSeekTarget.Instance</c>.
/// </para>
/// <para>
/// IT MUST NOT THROW, except <see cref="OperationCanceledException"/>. This is reached from the
/// per-connection message loop, so an escaping exception from a third-party media player would drop
/// the phone's whole connection to answer a scrubber drag. False means "did not move it", which is a
/// normal answer: several sessions accept the platform call and ignore it.
/// </para>
/// <para>
/// NOTHING IS PUBLISHED FROM HERE. A seek does not stamp an anchor and does not touch the snapshot
/// gate — the sampler's next poll reads the moved position, the anchor tracker re-anchors because it
/// diverged past tolerance, and the gate publishes to every client. Writing a clock here as well
/// would put two authorities on the same number, and the one that lost would be the one that
/// actually asked the player.
/// </para>
/// </remarks>
internal interface IMediaSeekTarget
{
    /// <summary>
    /// Moves the current session to <paramref name="positionMs"/> milliseconds from the start of the
    /// track, answering whether the platform reported the move as accepted.
    /// </summary>
    /// <remarks>
    /// MILLISECONDS AT THIS BOUNDARY, whatever the platform underneath counts in. SMTC takes 100-ns
    /// ticks and MPRIS takes microseconds; each reader converts on its own side, so the unit on the
    /// wire and the unit in this interface are the same one a reader of <c>MediaPlaybackState</c>
    /// already knows.
    /// </remarks>
    Task<bool> TrySeekAsync(long positionMs, CancellationToken ct);
}

/// <summary>
/// The seek target for a platform that cannot seek.
/// </summary>
/// <remarks>
/// A REAL OBJECT RATHER THAN A NULLABLE DEPENDENCY, so the sampler has one path instead of two and
/// the "no seeking on this platform" case is exercised by the same code that runs on Windows. It is
/// stateless, so a single instance serves everyone.
/// </remarks>
internal sealed class NullMediaSeekTarget : IMediaSeekTarget
{
    /// <summary>The only instance anyone needs.</summary>
    public static readonly NullMediaSeekTarget Instance = new();

    private NullMediaSeekTarget()
    {
    }

    /// <inheritdoc />
    public Task<bool> TrySeekAsync(long positionMs, CancellationToken ct) => Task.FromResult(false);
}
