using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>The last successful update check, persisted so a relaunch can reuse it (perf audit P3-56).</summary>
internal sealed record UpdateCheckCache(DateTimeOffset CheckedAtUtc, string LatestVersion, string DownloadUrl);

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

        // Only a real answer is remembered: a failed check must not suppress the next launch's retry.
        if (result.Status != UpdateCheckStatus.Failed && result.LatestVersion is { } latestVersion)
            TryWriteCache(_cachePath, new UpdateCheckCache(_time.GetUtcNow(), latestVersion, result.DownloadUrl));

        LastResult = result;
        ResultChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>How long a successful startup answer is reused before the next launch asks again.</summary>
    internal static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time;
    private readonly string _cachePath;

    public UpdateCheckService()
        : this(TimeProvider.System, Path.Combine(RemexDataPaths.PerUserDirectory, "update_check.json"))
    {
    }

    internal UpdateCheckService(TimeProvider time, string cachePath)
    {
        _time = time;
        _cachePath = cachePath;
    }

    /// <summary>
    /// The startup path (perf audit P3-56). Reuses a successful answer younger than
    /// <see cref="StartupCheckInterval"/> instead of calling GitHub on every launch - every minimized
    /// logon start included - and falls through to <see cref="CheckAsync"/> otherwise. The About page's
    /// "check now" still calls <see cref="CheckAsync"/> directly, so a user who asks always gets a live
    /// answer.
    /// </summary>
    public Task<UpdateCheckResult> CheckOnStartupAsync(CancellationToken cancellationToken = default)
    {
        if (TryReadCache(_cachePath) is { } cache
            && FromCache(cache, _time.GetUtcNow(), CurrentVersion) is { } cached)
        {
            LastResult = cached;
            ResultChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(cached);
        }
        return CheckAsync(cancellationToken);
    }

    /// <summary>
    /// A cached answer re-judged against the RUNNING build, or null when it is stale, from the future
    /// (a clock that jumped back), or unparseable. Re-judging matters: a cache that said "2.6.0 is out"
    /// must read as up to date once 2.6.0 is what is installed.
    /// </summary>
    internal static UpdateCheckResult? FromCache(UpdateCheckCache cache, DateTimeOffset now, string currentVersion)
    {
        var age = now - cache.CheckedAtUtc;
        if (age < TimeSpan.Zero || age > StartupCheckInterval) return null;
        if (!TryParseVersion(cache.LatestVersion, out var latest) || !TryParseVersion(currentVersion, out var current))
            return null;

        var status = Normalize(latest) > Normalize(current)
            ? UpdateCheckStatus.UpdateAvailable
            : UpdateCheckStatus.UpToDate;
        var url = string.IsNullOrWhiteSpace(cache.DownloadUrl) ? ReleasesPageUrl : cache.DownloadUrl;
        return new UpdateCheckResult(status, currentVersion, TrimTag(cache.LatestVersion), url);
    }

    /// <summary>Reads the cache file; missing, unreadable or malformed all read as "no cache".</summary>
    internal static UpdateCheckCache? TryReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Debug, "UpdateCheck", "Update-check cache unreadable; checking online", ex);
            return null;
        }
    }

    /// <summary>Best-effort write; a failure only costs the next launch one extra request.</summary>
    internal static void TryWriteCache(string path, UpdateCheckCache cache)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            // Best-effort - see the summary. Logged so a permanently unwritable cache is diagnosable.
            InMemoryLogSink.Append(LogLevel.Debug, "UpdateCheck", "Update-check cache could not be written", ex);
        }
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
