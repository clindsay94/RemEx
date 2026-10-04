using System.Text;
using Microsoft.Extensions.Logging;
using Remex.Agent.Services.Security;
using Remex.Core.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Readiness;
using Remex.Core.Validation;

namespace Remex.Agent.Services.Diagnostics;

/// <summary>
/// Answers a paired phone's read-only look at the PC's logs and status (RemEx-pp4cm.13).
/// </summary>
/// <remarks>
/// <para>
/// **THE BUFFER ALREADY LIVES IN ONE PLACE.** The PC's Logs &amp; diagnostics page and this service both read
/// <see cref="InMemoryLogSink"/>; nothing moved out of the view-model. The sink now stamps every entry with
/// a sequence number, which is the only reason incremental reads are possible.
/// </para>
/// <para>
/// **WHAT STANDS BETWEEN THE BUFFER AND THE PHONE, IN ORDER.** (1) The session must be a proven, paired,
/// non-loopback one, else <c>refused</c>. (2) At most one request of each kind a second per session, else
/// <c>rate_limited</c>. (3) The request must pass <see cref="DiagnosticsValidation"/>, else
/// <c>invalid_request</c>. (4) Every string is run through <see cref="LogRedaction"/> and an exception is
/// reduced to its type and first line. (5) The response is capped near 256 KB. A refusal is still an
/// answer, so the phone shows a reason instead of waiting out a timeout.
/// </para>
/// <para>
/// Read-only by construction: nothing here writes, clears or reconfigures anything, and the log lines it
/// emits record that a request happened, never what it contained.
/// </para>
/// </remarks>
public sealed class PhoneDiagnosticsService
{
    private readonly Func<IReadOnlyList<LogEntry>> _readEntries;
    private readonly Func<long> _readLastSeq;
    private readonly ISystemReadinessService? _readiness;
    private readonly Func<HostCapabilities> _capabilities;
    private readonly Func<TimeSpan> _uptime;
    private readonly TimeProvider _time;
    private readonly ILogger<PhoneDiagnosticsService>? _logger;

    /// <param name="readEntries">The captured entries, oldest first. Production: the in-memory sink.</param>
    /// <param name="readLastSeq">Newest sequence number ever captured. Production: the in-memory sink.</param>
    /// <param name="readiness">The readiness checks the PC's Diagnostics card shows. Null reads as "could not check".</param>
    /// <param name="capabilities">What this host can do, for the capture, encoder and version rows.</param>
    /// <param name="uptime">How long the agent has been running.</param>
    /// <param name="time">The clock the per-session rate limit reads.</param>
    /// <param name="logger">Optional; records that a request was served or refused, never its content.</param>
    public PhoneDiagnosticsService(
        Func<IReadOnlyList<LogEntry>> readEntries,
        Func<long> readLastSeq,
        ISystemReadinessService? readiness,
        Func<HostCapabilities> capabilities,
        Func<TimeSpan> uptime,
        TimeProvider time,
        ILogger<PhoneDiagnosticsService>? logger = null)
    {
        _readEntries = readEntries ?? throw new ArgumentNullException(nameof(readEntries));
        _readLastSeq = readLastSeq ?? throw new ArgumentNullException(nameof(readLastSeq));
        _readiness = readiness;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _uptime = uptime ?? throw new ArgumentNullException(nameof(uptime));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    /// <summary>One connection's request pacing. Create one per session; not shared between phones.</summary>
    public sealed class SessionGate
    {
        private readonly TimeProvider _time;
        private readonly Dictionary<string, long> _lastMs = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        internal SessionGate(TimeProvider time) => _time = time;

        /// <summary>True when a request of <paramref name="kind"/> may run now, and records that it did.</summary>
        public bool TryEnter(string kind)
        {
            var now = _time.GetTimestamp() * 1000 / _time.TimestampFrequency;
            lock (_lock)
            {
                if (_lastMs.TryGetValue(kind, out var last)
                    && now - last < DiagnosticsValidation.MinRequestIntervalMs)
                {
                    return false;
                }

                _lastMs[kind] = now;
                return true;
            }
        }
    }

    /// <summary>Starts the pacing for one new session.</summary>
    public SessionGate CreateSessionGate() => new(_time);

    /// <summary>
    /// Handles <c>diagnostic_logs_get</c> or <c>diagnostic_summary_get</c> and returns the reply to send.
    /// </summary>
    /// <param name="message">The inbound envelope.</param>
    /// <param name="gate">This session's pacing.</param>
    /// <param name="isLoopback">The connection came from this machine, which is never a phone.</param>
    /// <param name="identityProven">The session proved which paired phone it is.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reply, which echoes the correlation id. Never null and never throws for bad input.</returns>
    public async Task<RemexMessage> HandleAsync(
        RemexMessage message, SessionGate gate, bool isLoopback, bool identityProven, CancellationToken ct)
    {
        var isLogs = message.Type == MessageTypes.DiagnosticLogsGet;

        if (isLoopback || !identityProven)
        {
            _logger?.LogWarning("Refused {Type}: only a proven, paired phone may read the PC log.", message.Type);
            return Reply(message, isLogs, "refused");
        }

        if (!gate.TryEnter(message.Type))
        {
            return Reply(message, isLogs, "rate_limited");
        }

        try
        {
            if (isLogs)
            {
                if (!DiagnosticsValidation.IsValidLogsRequest(message.DiagnosticLogsRequest))
                {
                    return Reply(message, isLogs, "invalid_request");
                }

                return new RemexMessage
                {
                    Type = MessageTypes.DiagnosticLogsResult,
                    CorrelationId = message.CorrelationId,
                    DiagnosticLogsResponse = BuildLogs(message.DiagnosticLogsRequest!),
                };
            }

            var summary = await Task.Run(BuildSummary, ct);
            return new RemexMessage
            {
                Type = MessageTypes.DiagnosticSummaryResult,
                CorrelationId = message.CorrelationId,
                DiagnosticSummaryResponse = summary,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not build the {Type} answer.", message.Type);
            return Reply(message, isLogs, "unavailable");
        }
    }

    private static RemexMessage Reply(RemexMessage request, bool isLogs, string error) => isLogs
        ? new RemexMessage
        {
            Type = MessageTypes.DiagnosticLogsResult,
            CorrelationId = request.CorrelationId,
            DiagnosticLogsResponse = new DiagnosticLogsResponse { Error = error },
        }
        : new RemexMessage
        {
            Type = MessageTypes.DiagnosticSummaryResult,
            CorrelationId = request.CorrelationId,
            DiagnosticSummaryResponse = new DiagnosticSummaryResponse { Error = error },
        };

    /// <summary>Builds one page of redacted log entries. Public so the paging rules can be tested without a socket.</summary>
    public DiagnosticLogsResponse BuildLogs(DiagnosticLogsRequest request)
    {
        // The ranks are LogLevel's own values (a test pins that), so a cast is the whole mapping.
        DiagnosticsValidation.TryGetLevelRank(request.MinLevel, out var minRank);
        var minLevel = (LogLevel)minRank;

        var snapshot = _readEntries();
        var head = Math.Max(_readLastSeq(), snapshot.Count == 0 ? 0 : snapshot[^1].Seq);

        // A sequence number past the newest one means the PC restarted since the phone last looked, and
        // numbering began again. Nothing "after" it can be meant, so serve the newest page instead.
        var after = request.AfterSeq > head ? 0 : request.AfterSeq;

        var matches = new List<LogEntry>();
        foreach (var e in snapshot)
        {
            if (e.Level >= minLevel && e.Seq > after) matches.Add(e);
        }

        List<LogEntry> page;
        bool truncated;
        if (after == 0)
        {
            // Newest page: the last `max` that match. Older ones exist but are not wanted yet.
            var skip = Math.Max(0, matches.Count - request.Max);
            page = matches.GetRange(skip, matches.Count - skip);
            truncated = skip > 0;
        }
        else
        {
            // Incremental: the first `max` after what the phone has, so it never skips a line.
            var take = Math.Min(matches.Count, request.Max);
            page = matches.GetRange(0, take);
            truncated = matches.Count > take;
        }

        var dtos = new List<DiagnosticLogEntry>(page.Count);
        var bytes = 0;
        if (after == 0)
        {
            // Build from the newest backwards so the size cap drops the OLDEST lines.
            for (var i = page.Count - 1; i >= 0; i--)
            {
                var dto = ToDto(page[i]);
                var size = SizeOf(dto);
                if (bytes + size > DiagnosticsValidation.MaxResponseBytes) { truncated = true; break; }
                bytes += size;
                dtos.Add(dto);
            }

            dtos.Reverse();
        }
        else
        {
            foreach (var entry in page)
            {
                var dto = ToDto(entry);
                var size = SizeOf(dto);
                if (bytes + size > DiagnosticsValidation.MaxResponseBytes) { truncated = true; break; }
                bytes += size;
                dtos.Add(dto);
            }
        }

        // Truncated incremental pages continue from the last line actually sent; anything else has seen
        // everything up to the head, including lines the filter hid.
        var lastSeq = truncated && after != 0 && dtos.Count > 0 ? dtos[^1].Seq : head;
        return new DiagnosticLogsResponse { Entries = dtos, LastSeq = lastSeq, Truncated = truncated };
    }

    private static DiagnosticLogEntry ToDto(LogEntry e)
    {
        var message = LogRedaction.RedactText(e.Message);
        if (e.Exception is { } ex)
        {
            var firstLine = LogRedaction.RedactFirstLine(ex.Message);
            message += firstLine.Length == 0
                ? $" [{ex.GetType().Name}]"
                : $" [{ex.GetType().Name}: {firstLine}]";
        }

        if (message.Length > DiagnosticsValidation.MaxMessageLength)
        {
            message = message[..DiagnosticsValidation.MaxMessageLength] + "…";
        }

        return new DiagnosticLogEntry
        {
            Seq = e.Seq,
            TimeUtc = new DateTimeOffset(e.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
            Level = DiagnosticsValidation.LevelName((int)e.Level),
            Category = LogRedaction.RedactText(e.Category),
            Message = message,
        };
    }

    // Close to the JSON size: the text as UTF-8 plus the field names, sequence number and timestamp.
    private static int SizeOf(DiagnosticLogEntry e) =>
        Encoding.UTF8.GetByteCount(e.Message) + Encoding.UTF8.GetByteCount(e.Category) + 110;

    /// <summary>Builds the status rows, the same checks the PC's Diagnostics card shows plus version and uptime.</summary>
    public DiagnosticSummaryResponse BuildSummary()
    {
        var items = new List<DiagnosticSummaryItem>();
        var caps = _capabilities();

        var report = _readiness?.Run(forceFresh: false);
        foreach (var key in new[]
                 {
                     (ReadinessCheckId.PortListening, "listener"),
                     (ReadinessCheckId.Certificate, "certificate"),
                     (ReadinessCheckId.Firewall, "firewall"),
                     (ReadinessCheckId.Elevation, "elevation"),
                     (ReadinessCheckId.Autostart, "autostart"),
                 })
        {
            var check = report?.Applicable.FirstOrDefault(c => c.Id == key.Item1);
            if (check is null)
            {
                // Elevation is not applicable off Windows and is simply absent; every other row that
                // could not be read is shown as a warning rather than dropped, so a gap is visible.
                if (key.Item1 != ReadinessCheckId.Elevation)
                {
                    items.Add(Item(key.Item2, "warn", string.Empty));
                }

                continue;
            }

            items.Add(Item(key.Item2, MapState(check.State), check.Detail));
        }

        // Capture and encoder come from what the host reports it can do; the encoder itself is chosen
        // per stream, so this row says what is available, not what a running stream is using.
        items.Add(caps.SupportsRemoteDesktop
            ? Item("capture", "ok", caps.Platform)
            : Item("capture", "error", caps.RemoteDesktopUnavailableReason ?? string.Empty));
        items.Add(caps.SupportsRemoteDesktop
            ? Item("encoder", "ok", "H.264, JPEG")
            : Item("encoder", "warn", string.Empty));

        // Not redacted: both are values this class formats itself, and a four-part version number such
        // as 3.0.0.1 is shaped exactly like an IPv4 address.
        items.Add(new DiagnosticSummaryItem { Key = "version", State = "ok", Detail = caps.Version });
        items.Add(new DiagnosticSummaryItem { Key = "uptime", State = "ok", Detail = FormatUptime(_uptime()) });
        return new DiagnosticSummaryResponse { Items = items };
    }

    private static DiagnosticSummaryItem Item(string key, string state, string detail) => new()
    {
        Key = key,
        State = state,
        Detail = LogRedaction.RedactFirstLine(detail),
    };

    private static string MapState(ReadinessState state) => state switch
    {
        ReadinessState.Ok => "ok",
        ReadinessState.Problem => "error",
        _ => "warn",
    };

    /// <summary>Whole days, hours and minutes, for example <c>2d 3h 5m</c>. Never negative.</summary>
    internal static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
            : uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m";
    }
}
