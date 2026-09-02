using Remex.Core.Models;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Fetches the cover image for one playback reading, when the platform can produce one
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// SEPARATE FROM <see cref="IMediaSessionReader"/> BECAUSE THE COST IS SEPARATE. Reading title,
/// artist and status is a cheap local call that happens once a second; resolving artwork can mean
/// draining a WinRT thumbnail stream or fetching an <c>artUrl</c> over the network, and neither
/// belongs on the poll tick. Keeping it its own interface means the sampler can run the read on
/// schedule and the resolve off it, and means a platform that has no artwork at all implements
/// nothing rather than returning null from a method it was forced to have.
/// </para>
/// <para>
/// IMPLEMENTED BY THE PLATFORM READERS THEMSELVES — <c>WindowsMediaSessionReader</c> and
/// <c>LinuxMediaSessionReader</c> each implement both interfaces, because the artwork handle comes
/// out of the same session object the reading did. DI therefore resolves this by asking the
/// registered reader whether it happens to be one, and falls back to
/// <see cref="NullMediaArtworkSource.Instance"/> when it is not:
/// <c>sp.GetRequiredService&lt;IMediaSessionReader&gt;() as IMediaArtworkSource ?? NullMediaArtworkSource.Instance</c>.
/// </para>
/// <para>
/// IT MUST NOT THROW, except <see cref="OperationCanceledException"/>. Artwork is decoration on a
/// feature whose actual job is the play/pause icon; a reader that lets an HTTP failure or a
/// malformed thumbnail escape would take the whole media sampler down with it and the icon would go
/// back to being a picture. Null means "nothing resolvable", which is a normal answer.
/// </para>
/// </remarks>
internal interface IMediaArtworkSource
{
    /// <summary>
    /// The image bytes for <paramref name="state"/>, exactly as the platform supplied them, or null
    /// when there are none.
    /// </summary>
    /// <remarks>
    /// NO TRANSCODING, ON PURPOSE. SMTC thumbnails and remote <c>artUrl</c>s are usually JPEG, the
    /// occasional session gives PNG, and the phone decodes both with <c>BitmapFactory</c> without
    /// being told which. Normalising the format here would mean decoding and re-encoding every cover
    /// on the host to satisfy a field name.
    /// </remarks>
    Task<byte[]?> ResolveArtworkAsync(MediaPlaybackState state, CancellationToken ct);
}

/// <summary>
/// The artwork source for a platform that has none.
/// </summary>
/// <remarks>
/// A REAL OBJECT RATHER THAN A NULLABLE DEPENDENCY, so the sampler has one path instead of two and
/// the "no artwork on this platform" case is exercised by the same code that runs on Windows. It is
/// stateless, so a single instance serves everyone.
/// </remarks>
internal sealed class NullMediaArtworkSource : IMediaArtworkSource
{
    /// <summary>The only instance anyone needs.</summary>
    public static readonly NullMediaArtworkSource Instance = new();

    private NullMediaArtworkSource()
    {
    }

    /// <inheritdoc />
    public Task<byte[]?> ResolveArtworkAsync(MediaPlaybackState state, CancellationToken ct)
        => Task.FromResult<byte[]?>(null);
}
