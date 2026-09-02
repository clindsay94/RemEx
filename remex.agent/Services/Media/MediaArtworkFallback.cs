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
    /// </remarks>
    internal static byte[]? ExtractedIconBytes(string path)
    {
        try
        {
            var base64 = new DesktopIconExtractionService().ExtractIconAsBase64(path);

            if (string.IsNullOrEmpty(base64)
                || string.Equals(base64, DesktopIconExtractionService.FallbackBase64Icon, StringComparison.Ordinal))
            {
                return null;
            }

            return Convert.FromBase64String(base64);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
