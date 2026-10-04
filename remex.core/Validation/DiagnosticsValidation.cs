using Remex.Core.Models;

namespace Remex.Core.Validation;

/// <summary>
/// The one rule both ends of the phone's view of the PC's logs apply (RemEx-pp4cm.13).
/// </summary>
/// <remarks>
/// <para>
/// The host validates what the phone sends before it reads the buffer: a request that fails
/// <see cref="IsValidLogsRequest"/> is answered <c>invalid_request</c>, never partially served. The
/// phone builds its request through the same limits, so in normal use nothing is ever refused, and the
/// check on the host is the bound that does not depend on the peer behaving.
/// </para>
/// <para>
/// NativeAOT-safe: no reflection, no regex, no serialization. Level names are lower-case on the wire,
/// the same words on both platforms, and map onto integer ranks here so neither end has to parse an
/// enum by name.
/// </para>
/// </remarks>
public static class DiagnosticsValidation
{
    /// <summary>Most log entries one request may ask for.</summary>
    public const int MaxEntriesPerRequest = 500;

    /// <summary>Entries returned when a request does not say how many.</summary>
    public const int DefaultEntriesPerRequest = 200;

    /// <summary>
    /// Largest response the host will build, in bytes of text (about 256 KB). Entries past it are left
    /// for the next page and <see cref="DiagnosticLogsResponse.Truncated"/> is set.
    /// </summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>Longest message the host sends for one entry, in UTF-16 code units.</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>Fewest milliseconds between two requests of one kind on one session.</summary>
    public const int MinRequestIntervalMs = 1000;

    /// <summary>The level names the wire carries, lowest first.</summary>
    public static readonly IReadOnlyList<string> LevelNames =
        ["trace", "debug", "information", "warning", "error", "critical"];

    /// <summary>
    /// Maps a wire level name to its rank: 0 trace, 1 debug, 2 information, 3 warning, 4 error, 5 critical.
    /// False for anything unknown (case-sensitive).
    /// </summary>
    /// <remarks>
    /// A plain integer, not <c>Microsoft.Extensions.Logging.LogLevel</c>, on purpose. This type is compiled
    /// into <c>libRemexCore.so</c> and the NativeAOT link step for Android cannot load the logging
    /// abstractions assembly: a method here that named <c>LogLevel</c> linked with a warning and would
    /// throw when called. The ranks equal <c>LogLevel</c>'s own values, so the host casts; a test pins it.
    /// </remarks>
    public static bool TryGetLevelRank(string? name, out int rank)
    {
        switch (name)
        {
            case "trace": rank = 0; return true;
            case "debug": rank = 1; return true;
            case "information": rank = 2; return true;
            case "warning": rank = 3; return true;
            case "error": rank = 4; return true;
            case "critical": rank = 5; return true;
            default: rank = -1; return false;
        }
    }

    /// <summary>The wire name for a level rank. Anything outside 0 to 5 reads as <c>information</c>.</summary>
    public static string LevelName(int rank) => rank switch
    {
        0 => "trace",
        1 => "debug",
        2 => "information",
        3 => "warning",
        4 => "error",
        5 => "critical",
        _ => "information",
    };

    /// <summary>
    /// Whether a logs request may be served: a known level, <c>afterSeq</c> not negative, and
    /// <c>max</c> from 1 to <see cref="MaxEntriesPerRequest"/>. A null request is invalid.
    /// </summary>
    public static bool IsValidLogsRequest(DiagnosticLogsRequest? request) =>
        request is not null
        && request.AfterSeq >= 0
        && request.Max is >= 1 and <= MaxEntriesPerRequest
        && TryGetLevelRank(request.MinLevel, out _);

    /// <summary>Whether a summary row's state is one the phone knows how to draw.</summary>
    public static bool IsValidState(string? state) => state is "ok" or "warn" or "error";
}
