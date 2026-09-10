using Remex.Core.Models;

namespace Remex.Agent.Services.Media;

/// <summary>
/// The reader for a host that cannot read a media session (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// A REAL IMPLEMENTATION RATHER THAN A NULL REGISTRATION, so that every consumer can depend on the
/// service existing and nothing has to branch on whether the platform was supported. The single fact
/// this host has to communicate — that no reading is coming — travels once in
/// <c>HostCapabilities.SupportsMediaState</c>, not in a null check at each call site.
/// </para>
/// <para>
/// <see cref="ReadAsync"/> is never called: <c>MediaSessionBackgroundService</c> returns before its
/// loop when <see cref="IsSupported"/> is false. It answers Unknown anyway rather than throwing,
/// because a stub whose unreachable path is a landmine stops being a safe default the moment somebody
/// reorders that check.
/// </para>
/// </remarks>
internal sealed class UnsupportedMediaSessionReader : IMediaSessionReader
{
    public bool IsSupported => false;

    public Task<MediaPlaybackState> ReadAsync(CancellationToken ct)
        => Task.FromResult(new MediaPlaybackState { Status = MediaPlaybackStatus.Unknown });
}
