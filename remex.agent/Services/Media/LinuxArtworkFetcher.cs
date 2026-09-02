using System.Net;
using System.Net.Http.Headers;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Turns an MPRIS <c>mpris:artUrl</c> into image bytes, or refuses (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ONLY OUTBOUND REQUEST THE AGENT MAKES ON THE USER'S BEHALF, and it is worth saying so
/// where the code is rather than only in the spec. RemEx otherwise talks to the phone and to the
/// local machine, full stop; a host that quietly acquired a general HTTP habit would be a different
/// product from the one the user installed. It is here because Spotify, Apple Music and every
/// browser publish album art as an <c>https</c> URL and nothing else, so refusing them would mean
/// Linux almost never shows a cover (spec 2.1, approved 2026-09-02). Keep it the only one.
/// </para>
/// <para>
/// THE POLICY IS A WHITELIST, NOT A BLACKLIST. <c>file://</c> and <c>https://</c> are fetched;
/// everything else — <c>http://</c>, <c>ftp://</c>, <c>data:</c>, a bare relative string — is null
/// without a request. An <c>artUrl</c> is an arbitrary string chosen by whatever media player the
/// user happens to have open, so the question this code answers is not "is this URL fine" but "is
/// this one of the two shapes we agreed to". Plaintext <c>http</c> is excluded so that a hostile
/// player cannot use the agent as a cleartext beacon, and redirects are off so that an approved
/// <c>https</c> host cannot bounce the request somewhere unapproved.
/// </para>
/// <para>
/// BOUNDED IN EVERY DIMENSION: 5 s, 2 MB, no redirects, and the size cap is enforced while COPYING
/// rather than from <c>Content-Length</c>, because a header is a claim and the body is the fact. The
/// cap matches <see cref="MediaArtworkStore.MaxArtworkBytes"/> — art the store would refuse is art
/// not worth downloading.
/// </para>
/// </remarks>
internal static class LinuxArtworkFetcher
{
    /// <summary>The largest image worth fetching, matching what the store will accept.</summary>
    internal const int MaxBytes = MediaArtworkStore.MaxArtworkBytes;

    /// <summary>
    /// One client for the process, built once.
    /// </summary>
    /// <remarks>
    /// STATIC BECAUSE A PER-FETCH <c>HttpClient</c> IS THE CLASSIC SOCKET LEAK, and this can run on
    /// every track change for as long as the host is up. The handler is configured rather than
    /// defaulted: <c>AllowAutoRedirect</c> false is part of the policy above, not a tuning knob.
    /// </remarks>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>
    /// The bytes behind <paramref name="artUrl"/>, or null when the policy or the fetch says no.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS EXCEPT CANCELLATION, like everything else on the artwork path. A player pointing
    /// at a dead host is an ordinary Tuesday and must cost nothing more than a missing cover.
    /// </remarks>
    public static async Task<byte[]?> FetchAsync(string? artUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artUrl))
        {
            return null;
        }

        // A RELATIVE STRING IS REJECTED HERE, not resolved against anything. There is no base URI
        // that would be correct: the metadata came from another process, not from a document.
        if (!Uri.TryCreate(artUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.IsFile)
        {
            return ReadLocalFile(uri);
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return await FetchHttpsAsync(uri, ct);
        }

        return null;
    }

    /// <summary>Reads a <c>file://</c> cover off disk, subject to the same size cap.</summary>
    /// <remarks>
    /// THE LENGTH IS CHECKED BEFORE THE READ because a local file can state its size honestly, unlike
    /// an HTTP body. A file that vanished between the player publishing it and this call — a
    /// temporary cache entry, which is what most players write — is null, not an exception.
    /// </remarks>
    private static byte[]? ReadLocalFile(Uri uri)
    {
        try
        {
            var path = uri.LocalPath;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> FetchHttpsAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            // A REDIRECT IS A REFUSAL, not a hop to follow. With AllowAutoRedirect off, 3xx arrives
            // here as a non-success status and falls out below.
            if (!response.IsSuccessStatusCode || !IsImage(response.Content.Headers.ContentType))
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await ReadBoundedAsync(stream, MaxBytes, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as a cancellation with no token behind it. That is
            // a failed fetch, not a shutdown, so it must not propagate as one.
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the response claims to be an image at all.
    /// </summary>
    /// <remarks>
    /// CHECKED SO THAT A CAPTIVE PORTAL'S LOGIN PAGE DOES NOT BECOME AN ALBUM COVER. It is a claim,
    /// not proof — the phone's <c>BitmapFactory</c> is the real arbiter — but a 200 with
    /// <c>text/html</c> is a cheap and common thing to drop before spending 2 MB of socket on it.
    /// </remarks>
    private static bool IsImage(MediaTypeHeaderValue? contentType)
        => contentType?.MediaType is { } mediaType
            && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> from <paramref name="source"/>, and returns null if
    /// there was more.
    /// </summary>
    /// <remarks>
    /// NULL RATHER THAN A TRUNCATED ARRAY, which is the whole point of doing the copy by hand. A
    /// truncated image decodes to a half-drawn cover or to nothing, and the phone has no way to tell
    /// that from a real one; refusing means it draws the glyph instead. Reading one byte past the cap
    /// is how "at the cap" and "over the cap" are told apart without trusting a header.
    /// </remarks>
    internal static async Task<byte[]?> ReadBoundedAsync(Stream source, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var sink = new MemoryStream();

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                break;
            }

            if (sink.Length + read > maxBytes)
            {
                return null;
            }

            sink.Write(buffer, 0, read);
        }

        return sink.Length == 0 ? null : sink.ToArray();
    }
}
