using Remex.Core.Messages;

namespace Remex.Agent.Services.Media;

/// <summary>
/// The last serialized <c>media_artwork</c> reply, reused while the same artwork is requested again
/// (perf audit P4-24). Every request used to base64-encode and JSON-serialize up to 2 MB of cover art
/// afresh, once per request per client, although a track change is exactly when every connected
/// phone asks for the same id.
/// </summary>
/// <remarks>
/// ONE SLOT, NOT A PER-ID CACHE: the reply is ~1.33x the image, so remembering one per store entry
/// would double what the artwork store already bounds. Keyed on the id AND the store's byte[]
/// instance, so an id evicted and re-stored (a new array) is re-serialized rather than trusted on the
/// hash alone. Callers only look it up once the store has returned bytes - an evicted id still gets
/// its null reply built fresh. The returned array is shared and goes out through
/// <see cref="MessageSerializer.SendRawAsync"/>, which never mutates it.
/// </remarks>
internal sealed class MediaArtworkReplyCache
{
    /// <summary>Process-wide: the reply depends only on the artwork, not on the connection.</summary>
    internal static MediaArtworkReplyCache Shared { get; } = new();

    private readonly object _lock = new();
    private string? _artworkId;
    private byte[]? _source;
    private byte[]? _reply;

    /// <summary>Replies serialized since construction; a test seam.</summary>
    internal int SerializeCount { get; private set; }

    public byte[] GetOrCreate(string artworkId, byte[] artworkBytes)
    {
        lock (_lock)
        {
            if (_reply is not null
                && ReferenceEquals(_source, artworkBytes)
                && string.Equals(_artworkId, artworkId, StringComparison.Ordinal))
            {
                return _reply;
            }
        }

        // Built outside the lock: two racing requests for a new id may both serialize once, which is
        // no worse than before and keeps a 2 MB encode from blocking other lookups.
        var reply = MessageSerializer.Serialize(Build(artworkId, artworkBytes));

        lock (_lock)
        {
            SerializeCount++;
            _artworkId = artworkId;
            _source = artworkBytes;
            _reply = reply;
        }

        return reply;
    }

    internal static RemexMessage Build(string artworkId, byte[]? artworkBytes) => new()
    {
        Type = MessageTypes.MediaArtwork,
        MediaArtwork = new Remex.Core.Models.MediaArtwork
        {
            ArtworkId = artworkId,
            PngBase64 = artworkBytes is null ? null : Convert.ToBase64String(artworkBytes),
        },
    };
}
