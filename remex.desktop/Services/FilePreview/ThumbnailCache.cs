using Avalonia.Media;
using Avalonia.Media.Imaging;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.Services.FilePreview;

/// <summary>
/// Thumbnails for the File Transfer list and grid (2026-10-08 redesign): fetched only for rows on screen, at most
/// <see cref="MaxConcurrent"/> at a time, once per file version (device, shared folder, path, modified time), and
/// kept for the most recent <see cref="Capacity"/> files. Two rows asking for the same file share one fetch.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>How many thumbnails are kept.</summary>
    public const int Capacity = 400;

    /// <summary>How many are fetched at once, so scrolling a folder of photos does not flood a phone.</summary>
    public const int MaxConcurrent = 4;

    /// <summary>The edge length requested from the host; the list shows 24 px and the grid up to 176 px.</summary>
    public const int RequestedMaxDim = 192;

    public readonly record struct Key(string Device, string RootId, string Path, long ModifiedUnixMs);

    private readonly object _gate = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, IImage? Image)>> _entries = new();
    private readonly LinkedList<(Key Key, IImage? Image)> _order = new();
    private readonly Dictionary<Key, Task<IImage?>> _inFlight = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);

    /// <summary>Turns the host's Base64 JPEG into an image. A seam: unit tests run without a renderer.</summary>
    internal Func<string, IImage?> Decode { get; set; } = base64 =>
    {
        using var stream = new MemoryStream(Convert.FromBase64String(base64));
        return new Bitmap(stream);
    };

    /// <summary>
    /// The thumbnail for <paramref name="key"/>, fetched through <paramref name="client"/> if it is not cached.
    /// Null when the host has none (not an image, or undecodable); a null answer is cached too, so it is not asked again.
    /// </summary>
    public Task<IImage?> GetAsync(FileTransferClient client, Key key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return Task.FromResult(node.Value.Image);
            }

            if (_inFlight.TryGetValue(key, out var pending))
                return pending;

            var fetch = FetchAsync(client, key, ct);
            _inFlight[key] = fetch;
            return fetch;
        }
    }

    private async Task<IImage?> FetchAsync(FileTransferClient client, Key key, CancellationToken ct)
    {
        IImage? image = null;
        var keep = false;
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                var base64 = await client.GetThumbnailRemoteAsync(key.RootId, key.Path, RequestedMaxDim, ct);
                image = string.IsNullOrEmpty(base64) ? null : Decode(base64);
                keep = true;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A row scrolled away (cancelled) or the host failed: nothing cached, so it is tried again later.
            image = null;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(key);
                if (keep)
                    Add(key, image);
            }
        }

        return image;
    }

    private void Add(Key key, IImage? image)
    {
        if (_entries.Remove(key, out var stale))
            _order.Remove(stale);
        var node = _order.AddFirst((key, image));
        _entries[key] = node;
        while (_entries.Count > Capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }
    }

    /// <summary>How many thumbnails are cached (for tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }
}
