namespace Remex.Agent.Services.ProcessMonitor;

/// <summary>Version and publisher read from an executable's version resource.</summary>
/// <remarks>
/// **THE INSTALL DATE IS DELIBERATELY NOT HERE.** It comes from the file's CREATION time while this
/// record is keyed by its WRITE time, and the two move independently — NTFS tunnelling preserves a
/// creation time across an in-place replace. Carrying it would make the obvious tidy-up (serve the
/// install date from the cache too, since it is computed right there) silently wrong: every process
/// sharing an executable would report whichever creation time was seen first after the last content
/// change. It was in an earlier draft, written and cached and never read.
/// </remarks>
internal readonly record struct ExecutableMetadata(string Version, string Publisher);

/// <summary>
/// Caches executable metadata across process scans, keyed by path and last-write time (RemEx-qz5z3).
/// </summary>
/// <remarks>
/// <para>
/// **THE SCAN USED TO OPEN AND PARSE EVERY RUNNING EXECUTABLE, EVERY POLL.** Version and publisher come
/// from <c>FileVersionInfo.GetVersionInfo</c>, which opens the PE file and parses its version resource,
/// and it ran once per PROCESS rather than once per FILE. A typical Windows box runs 250-400 processes
/// and many are several instances of one executable — browser renderers, a dozen svchost — so the same
/// file was opened and parsed dozens of times per scan, and the whole set again on the next one. None
/// of it is a property of the process: it belongs to the file.
/// </para>
/// <para>
/// **KEYED ON LAST-WRITE TIME AS WELL AS PATH, WHICH IS WHAT MAKES THE CACHE SAFE TO KEEP.** Path alone
/// would serve a stale version string for the life of the agent after an in-place update — and the
/// agent is a long-running service, so "restart it" is not an answer a user would ever think to try.
/// Including the write time costs nothing: the caller already stats the file for the install date, so
/// the timestamp is in hand before this is consulted. An updated binary simply misses and reloads.
/// </para>
/// <para>
/// **A FAILED READ IS NEVER CACHED.** The caller's loader throws for the cases the scan deliberately
/// tolerates — a file replaced mid-scan, a permissions refusal — and caching that would turn one
/// unlucky moment into a permanently blank row. Misses are cheap; a poisoned entry is not.
/// </para>
/// </remarks>
internal sealed class ExecutableMetadataCache
{
    private readonly Dictionary<(string Path, long WriteTicks), ExecutableMetadata> _entries = new();

    /// <summary>Entries currently held. For tests and diagnostics.</summary>
    internal int Count => _entries.Count;

    /// <summary>
    /// Returns cached metadata for <paramref name="path"/>, calling <paramref name="load"/> only when
    /// the file has not been seen at this write time.
    /// </summary>
    /// <remarks>
    /// Not thread-safe by design: the scan already holds a lock for the whole enumeration, and adding
    /// a second synchronisation layer here would suggest a concurrency guarantee this type does not
    /// have. Anything calling it from elsewhere must bring its own.
    ///
    /// <paramref name="load"/> takes the path rather than closing over it so callers can pass a
    /// static method group. A lambda capturing locals would allocate a display class and a delegate
    /// for every process on every scan, including the cache hits — a few hundred gen-0 objects per
    /// poll, added by a change whose whole purpose is to do less work.
    /// </remarks>
    internal ExecutableMetadata GetOrAdd(string path, DateTime lastWriteUtc, Func<string, ExecutableMetadata> load)
    {
        var key = (path, lastWriteUtc.Ticks);
        if (_entries.TryGetValue(key, out var cached))
            return cached;

        // Deliberately outside any try: a throwing loader must reach the caller's handlers, which
        // distinguish a denied read from an exited process, and must leave nothing behind here.
        // Written as a call-then-assign rather than Lazy<T> for the same reason - Lazy's default
        // ExecutionAndPublication caches the EXCEPTION and rethrows it for ever.
        var loaded = load(path);
        _entries[key] = loaded;
        return loaded;
    }

    /// <summary>
    /// Drops entries for paths not seen in the scan that just finished.
    /// </summary>
    /// <remarks>
    /// **WITHOUT THIS THE CACHE IS A LEAK WITH A SLOW FUSE.** The agent runs for weeks; every
    /// executable ever launched, and every version of it, would keep an entry for the life of the
    /// process. Trimming to what the last scan actually saw bounds it to what is running rather than
    /// to what has ever run. Called once per scan, so it is one pass over a few hundred entries.
    /// </remarks>
    internal void RetainOnly(HashSet<string> livePaths)
    {
        if (_entries.Count == 0) return;

        List<(string Path, long WriteTicks)>? stale = null;
        foreach (var key in _entries.Keys)
        {
            if (!livePaths.Contains(key.Path))
                (stale ??= []).Add(key);
        }

        if (stale is null) return;
        foreach (var key in stale)
            _entries.Remove(key);
    }
}
