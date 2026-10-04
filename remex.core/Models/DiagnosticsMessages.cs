namespace Remex.Core.Models;

/// <summary>
/// Phone → host: one page of the PC's captured log, for <c>diagnostic_logs_get</c> (RemEx-pp4cm.13).
/// </summary>
/// <remarks>
/// <para>
/// Read-only. The phone can look at the PC's retained log buffer and can do nothing else with it: there
/// is no clear, no capture-level change and no export. Paired, proven, non-loopback sessions only, one
/// request a second per session, and every string that leaves the PC is redacted first.
/// </para>
/// <para>
/// <see cref="AfterSeq"/> 0 asks for the newest page; any other value asks for the entries that came
/// after that sequence number, oldest first, so a polling phone never misses or repeats a line.
/// Validated by <see cref="Validation.DiagnosticsValidation"/> on both ends.
/// </para>
/// </remarks>
public sealed record DiagnosticLogsRequest
{
    /// <summary>The last sequence number the phone already has. 0 means "give me the newest page".</summary>
    public long AfterSeq { get; init; }

    /// <summary>Lowest level to return: trace, debug, information, warning, error or critical.</summary>
    public string MinLevel { get; init; } = "information";

    /// <summary>Most entries to return, 1 to <see cref="Validation.DiagnosticsValidation.MaxEntriesPerRequest"/>.</summary>
    public int Max { get; init; } = 200;
}

/// <summary>One redacted log line, as the phone sees it.</summary>
public sealed record DiagnosticLogEntry
{
    /// <summary>Position in the PC's buffer. Rises by one for every entry captured, never repeats within a run.</summary>
    public long Seq { get; init; }

    /// <summary>When the entry was captured, UTC.</summary>
    public DateTimeOffset TimeUtc { get; init; }

    /// <summary>Lower-case level name: trace, debug, information, warning, error or critical.</summary>
    public string Level { get; init; } = "information";

    /// <summary>The logger category, redacted.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>The message, redacted, with any exception reduced to its type and first line.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Host → phone: the answer to a <see cref="DiagnosticLogsRequest"/>, for <c>diagnostic_logs_result</c>.
/// </summary>
public sealed record DiagnosticLogsResponse
{
    /// <summary>The entries, oldest first. Empty when nothing matched or <see cref="Error"/> is set.</summary>
    public List<DiagnosticLogEntry> Entries { get; init; } = [];

    /// <summary>
    /// The sequence number to send as <c>afterSeq</c> next. When <see cref="Truncated"/> is false this is
    /// the newest sequence number the PC had, even if the filter hid it, so the next poll does not
    /// re-scan what was already looked at.
    /// </summary>
    public long LastSeq { get; init; }

    /// <summary>True when more matching entries exist than were returned (page cap or size cap).</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Null on success. Otherwise a token the phone translates: <c>refused</c>, <c>rate_limited</c>,
    /// <c>invalid_request</c> or <c>unavailable</c>. Never a sentence, because the PC does not know the
    /// phone's language.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>One row of the PC's diagnostics summary.</summary>
public sealed record DiagnosticSummaryItem
{
    /// <summary>
    /// What the row is about: listener, certificate, firewall, elevation, autostart, capture, encoder,
    /// version or uptime. The phone maps it to a translated label and an icon; an unknown key is shown
    /// as plain text rather than dropped.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>One of <c>ok</c>, <c>warn</c> or <c>error</c>.</summary>
    public string State { get; init; } = "ok";

    /// <summary>A short redacted detail (a version, a duration, a backend name). May be empty.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Host → phone: the answer to <c>diagnostic_summary_get</c>, for <c>diagnostic_summary_result</c>.
/// </summary>
public sealed record DiagnosticSummaryResponse
{
    /// <summary>The status rows, in the order the PC lists them. Empty when <see cref="Error"/> is set.</summary>
    public List<DiagnosticSummaryItem> Items { get; init; } = [];

    /// <summary>Null on success; otherwise the same tokens as <see cref="DiagnosticLogsResponse.Error"/>.</summary>
    public string? Error { get; init; }
}
