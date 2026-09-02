using System.Security.Cryptography;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Where resolved cover art lives on the host, keyed by content hash (RemEx-vtorl).
/// </summary>
/// <remarks>
/// A SMALL LRU, NOT A CACHE WITH A TIMER. Artwork is only ever useful while a phone is looking at
/// the track it belongs to, and there are at most a handful of tracks worth remembering at once —
/// eviction by recency is the right shape, and a wall-clock expiry would just be a second, redundant
/// way to reach the same eviction a size bound already gives for free.
/// </remarks>
internal interface IMediaArtworkStore
{
    /// <summary>
    /// Stores the image bytes and returns the id a client will later ask for, or null when
    /// <paramref name="bytes"/> is empty or over <see cref="MediaArtworkStore.MaxArtworkBytes"/>.
    /// </summary>
    string? Put(byte[] bytes);

    /// <summary>The bytes for <paramref name="artworkId"/>, or null when the store never had them
    /// or has since evicted them.</summary>
    byte[]? TryGet(string artworkId);
}

/// <inheritdoc cref="IMediaArtworkStore"/>
internal sealed class MediaArtworkStore(int capacity = 8) : IMediaArtworkStore
{
    /// <summary>
    /// The cap below <c>MessageSerializer.MaxMessageSize</c> (4 MB) that leaves room for base64's
    /// ~1.33x expansion plus the rest of the envelope.
    /// </summary>
    internal const int MaxArtworkBytes = 2 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly LinkedList<(string Id, byte[] Bytes)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Id, byte[] Bytes)>> _entries = new();

    /// <inheritdoc />
    public string? Put(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxArtworkBytes)
        {
            return null;
        }

        var id = ComputeId(bytes);

        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var existing))
            {
                // IDENTICAL BYTES, SAME SLOT. Re-putting the same track's art (every poll that keeps
                // resolving it, say) must not count as a second entry against capacity.
                _order.Remove(existing);
                _order.AddFirst(existing);
                return id;
            }

            var node = new LinkedListNode<(string, byte[])>((id, bytes));
            _order.AddFirst(node);
            _entries[id] = node;

            while (_entries.Count > capacity)
            {
                var lru = _order.Last;
                if (lru is null)
                {
                    break;
                }

                _order.RemoveLast();
                _entries.Remove(lru.Value.Id);
            }
        }

        return id;
    }

    /// <inheritdoc />
    public byte[]? TryGet(string artworkId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(artworkId, out var node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Bytes;
        }
    }

    /// <summary>The first 16 lowercase hex characters of the SHA-256 of <paramref name="bytes"/>.</summary>
    internal static string ComputeId(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash[..8]);
    }
}
