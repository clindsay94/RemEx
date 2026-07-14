using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Desktop.Services;

/// <summary>Outcome of an update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The installed build is the latest published release.</summary>
    UpToDate,

    /// <summary>A newer release is available on GitHub.</summary>
    UpdateAvailable,

    /// <summary>The check could not complete (offline, rate-limited, malformed response, …).</summary>
    Failed,
}

/// <summary>Immutable result of a single update check.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string DownloadUrl);

/// <summary>
/// Checks the public GitHub Releases API for a newer RemEx build and caches the most recent result.
/// <para>
/// This is a <b>PC-only</b> concern — it lives in <c>remex.desktop</c>, never runs on the Android
/// client, and makes a single anonymous <c>GET</c> to
/// <c>api.github.com/repos/clindsay94/remex/releases/latest</c>. No identifying data or telemetry is
/// sent; the request carries only the User-Agent GitHub requires. Failures are swallowed into
/// <see cref="UpdateCheckStatus.Failed"/> so a missing network never disrupts startup.
/// </para>
/// </summary>
public sealed class UpdateCheckService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/clindsay94/remex/releases/latest";
    private const string ReleasesPageUrl =
        "https://github.com/clindsay94/remex/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Public releases page — the fallback download target when a release has no html_url.</summary>
    public string ReleasesUrl => ReleasesPageUrl;

    /// <summary>The most recent completed check, or <c>null</c> before the first check finishes.</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    /// <summary>
    /// Raised whenever <see cref="LastResult"/> changes. Fires on the thread that completed the check
    /// (typically a thread-pool thread from a startup check), so UI subscribers must marshal to the UI
    /// thread themselves.
    /// </summary>
    public event EventHandler? ResultChanged;

    /// <summary>The running build's version (from the entry/desktop assembly), e.g. "2.2.0.0".</summary>
    public string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's REST API rejects requests that omit a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemEx-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Performs a check, updates <see cref="LastResult"/>, raises <see cref="ResultChanged"/>, and
    /// returns the result. Never throws — any error resolves to <see cref="UpdateCheckStatus.Failed"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result;
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() : null;
            var downloadUrl = string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUrl : htmlUrl!;

            if (TryParseVersion(tag, out var latest) && TryParseVersion(CurrentVersion, out var current))
            {
                var status = Normalize(latest) > Normalize(current)
                    ? UpdateCheckStatus.UpdateAvailable
                    : UpdateCheckStatus.UpToDate;
                result = new UpdateCheckResult(status, CurrentVersion, TrimTag(tag), downloadUrl);
            }
            else
            {
                // We reached GitHub but couldn't make sense of the version — don't claim up-to-date.
                result = new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, downloadUrl);
            }
        }
        catch (Exception)
        {
            // Offline, DNS failure, timeout, HTTP error, rate limit, or bad JSON — all non-fatal.
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, ReleasesPageUrl);
        }

        LastResult = result;
        ResultChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Strips a leading "v"/"V" from a release tag (e.g. "v2.3.0" → "2.3.0").</summary>
    private static string? TrimTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var trimmed = tag.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
            trimmed = trimmed.Substring(1);
        return trimmed;
    }

    /// <summary>Parses a release tag or assembly version, ignoring any pre-release/build suffix.</summary>
    private static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0);
        var trimmed = TrimTag(raw);
        if (string.IsNullOrWhiteSpace(trimmed)) return false;

        // Drop a pre-release / build-metadata suffix ("2.3.0-beta.1", "2.3.0+ci") before parsing.
        int cut = trimmed.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) trimmed = trimmed.Substring(0, cut);
        return Version.TryParse(trimmed, out version!);
    }

    /// <summary>
    /// Collapses a version to Major.Minor.Build so the comparison ignores the revision field, which
    /// differs meaninglessly between a 4-part assembly version (2.2.0.0) and a 3-part release (2.2.0).
    /// </summary>
    private static Version Normalize(Version v) =>
        new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}
