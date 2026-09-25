namespace Remex.Agent.Services.Media;

/// <summary>
/// Runs the spec 2.1 artwork fallback chain: the first attempt that produces bytes wins
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// SHARED BY BOTH PLATFORMS BECAUSE THE ORDER IS THE DECISION, not the fetching. Session art first,
/// the app's own icon second, nothing third — Windows and Linux differ only in how each rung is
/// fetched. Writing the ladder twice would be two chances for the order to drift, and the order is
/// the part a user notices: falling to the app icon while album art existed makes every track in an
/// album look identical.
/// </para>
/// <para>
/// AN ATTEMPT THAT THROWS IS A RUNG THAT MISSED, not a failure. Each rung reaches something outside
/// this process — a WinRT thumbnail stream, a file on disk, an HTTPS host, a package manifest — and
/// every one of them can be denied, absent or malformed. <see cref="IMediaArtworkSource"/>'s contract
/// is that artwork never takes the sampler down, so the swallow lives here, once, rather than being
/// re-derived inside each attempt.
/// </para>
/// <para>
/// AN EMPTY ARRAY COUNTS AS NOTHING. A zero-byte thumbnail and a zero-byte HTTP body are both real
/// answers from real players, and passing one on would put an id in the store for an image that
/// cannot decode — the phone would then request it, get bytes, and draw nothing, with no way to fall
/// back to the glyph.
/// </para>
/// </remarks>
internal static class MediaArtworkFallback
{
    /// <summary>
    /// The bytes from the first attempt that returns a non-empty array, or null when none does.
    /// </summary>
    /// <remarks>
    /// LAZY BY CONSTRUCTION — the attempts are functions, not tasks, so a later rung is never started
    /// when an earlier one succeeds. The common case on both platforms is that rung one hits, and
    /// rung two on Windows enumerates installed packages.
    /// </remarks>
    public static async Task<byte[]?> FirstNonEmptyAsync(
        IEnumerable<Func<CancellationToken, Task<byte[]?>>> attempts, CancellationToken ct)
    {
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var bytes = await attempt(ct);
                if (bytes is { Length: > 0 })
                {
                    return bytes;
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a missed rung. The one exception the contract lets out.
                throw;
            }
            catch (Exception)
            {
                // Deliberately unlogged and deliberately broad: this runs once per track change, the
                // callers already log at their own level of detail, and there is nothing the host can
                // do about a player that will not hand over its cover.
            }
        }

        return null;
    }

    /// <summary>
    /// The icon bytes <c>DesktopIconExtractionService</c> produces for <paramref name="path"/>, or
    /// null when all it had was its placeholder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SHARED BY BOTH PLATFORMS' SECOND RUNG — a Windows executable path and a Linux
    /// <c>.desktop</c> path go into the same extractor, which is what spec 2.1 means by "both then go
    /// through <c>remex.agent</c>'s <c>DesktopIconExtractionService</c>, not <c>remex.core</c>'s
    /// stub".
    /// </para>
    /// <para>
    /// THE SENTINEL IS DETECTED BY IDENTITY, NOT BY DECODING. That extractor never returns null; it
    /// returns a 32×32 transparent PNG when it fails, which is indistinguishable from a real icon
    /// once it is base64 and would travel to the phone as an invisible cover the user cannot tell
    /// from a broken one. Comparing against the constant is exact, costs nothing, and is why
    /// <c>FallbackBase64Icon</c> is <c>internal</c> rather than <c>private</c>.
    /// </para>
    /// <para>
    /// CACHED PER PATH AND FILE TIMESTAMP (perf audit P3-47). This rung runs on every track change of
    /// a player whose session has no art, and each run re-extracted the same icon through the shell
    /// image list and re-encoded it as PNG — then base64, then back to bytes. The executable does not
    /// change between tracks, so the result is kept, keyed on the path AND its last-write time so an
    /// app update that ships a new icon is picked up. A miss (placeholder) is cached too, but only for
    /// <see cref="ExtractedIconCache.DefaultMissTtl"/>: the extractor swallows its own failures and
    /// returns the same placeholder for "this file has no icon" and "the shell was not ready yet"
    /// (explorer still starting at logon), so a miss cached until the next app update could pin a
    /// transient failure for weeks. An exception that escapes extraction is not cached at all.
    /// </para>
    /// <para>
    /// The returned array is shared between callers; the artwork path only reads it.
    /// </para>
    /// </remarks>
    internal static byte[]? ExtractedIconBytes(string path)
    {
        DateTime stamp;
        try
        {
            // A missing file reports the 1601 sentinel rather than throwing; that is still a stable key.
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            stamp = default;
        }

        try
        {
            return IconCache.GetOrAdd(path, stamp, ExtractUncached);
        }
        catch (Exception)
        {
            // Thrown out of the extractor, so GetOrAdd cached nothing: the next track change retries.
            return null;
        }
    }

    private static readonly ExtractedIconCache IconCache = new(capacity: 32);

    // Deliberately lets exceptions escape: ExtractedIconCache does not cache a throw, which is what
    // keeps a transient failure from sticking.
    private static byte[]? ExtractUncached(string path)
    {
        var base64 = new DesktopIconExtractionService().ExtractIconAsBase64(path);

        if (string.IsNullOrEmpty(base64)
            || string.Equals(base64, DesktopIconExtractionService.FallbackBase64Icon, StringComparison.Ordinal))
        {
            return null;
        }

        return Convert.FromBase64String(base64);
    }
}

/// <summary>
/// Small bounded cache of extracted app-icon bytes keyed on (path, last-write time) (P3-47).
/// </summary>
/// <remarks>
/// Bounded by clearing when full rather than by LRU order: the working set is "the handful of media
/// apps this user runs", far under the cap, so the clear is a leak guard, not a hot path.
/// A hit is kept until the key changes; a miss (null) only for <c>missTtl</c>, because a miss can be
/// a transient failure the extractor reports the same way as "no icon". An exception thrown by the
/// extractor propagates and caches nothing.
/// </remarks>
internal sealed class ExtractedIconCache(int capacity, TimeSpan? missTtl = null, Func<DateTime>? utcNow = null)
{
    internal static readonly TimeSpan DefaultMissTtl = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _missTtl = missTtl ?? DefaultMissTtl;
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly Dictionary<(string Path, DateTime Stamp), (byte[]? Bytes, DateTime CachedAtUtc)> _entries = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public byte[]? GetOrAdd(string path, DateTime stamp, Func<string, byte[]?> extract)
    {
        var key = (path, stamp);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached)
                && (cached.Bytes is not null || _utcNow() - cached.CachedAtUtc < _missTtl))
            {
                return cached.Bytes;
            }
        }

        // Outside the lock: extraction is slow (shell + PNG encode). Two racing callers for the same
        // new key both extract once and the second write wins with an equivalent value. A throw
        // leaves the cache untouched.
        var bytes = extract(path);

        lock (_gate)
        {
            if (_entries.Count >= capacity && !_entries.ContainsKey(key)) _entries.Clear();
            _entries[key] = (bytes, _utcNow());
        }

        return bytes;
    }
}
