using System.Text.Json;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>The host's <c>routine_runs.json</c> (routines spec §6.9).</summary>
public sealed record RoutineRunsDocument
{
    public int FileVersion { get; init; } = RoutineHostDocument.CurrentFileVersion;

    /// <summary>The next <c>seq</c> to hand out; strictly increasing for the life of the file.</summary>
    public long NextSeq { get; init; } = 1;

    public List<RoutineRun>? Runs { get; init; }
}

/// <summary>
/// PC run history (routines spec §8.8, §8.4 <c>RoutineRunStore</c>): <c>seq</c> allocation, retention, the
/// startup sweep and report paging.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>seq</c> IS THE LAST-MODIFIED SEQUENCE, NOT THE CREATION ORDER.</b> Every upsert stamps a new one,
/// so a record that changes after the phone saw it (a run that ended, a queued notify that expired) is
/// re-sent by the cursor-based report (§7.3.6) and the phone upserts it by <c>runId</c>.
/// </para>
/// <para>
/// <b>A RUN IS SAVED IN STATE <c>running</c> BEFORE ITS FIRST STEP (§8.1).</b> The startup sweep turns any
/// record still in that state into <c>interrupted</c> / <c>interrupted_pc</c>, which is the only way a run
/// cut short by a crash or a power loss is visible at all.
/// </para>
/// <para>
/// Retention (PC column of §8.8): a record is kept while it is among the last 20 of its routine OR younger
/// than 30 days, never older than 90 days, and the file holds at most 500 (oldest dropped first). A running
/// record is never dropped.
/// </para>
/// </remarks>
public sealed class RoutineRunStore
{
    public const string FileName = "routine_runs.json";
    public const int KeepPerRoutine = 20;
    public const int Cap = 500;
    public const int MaxPerReport = 50;
    public static readonly TimeSpan KeepYoungerThan = TimeSpan.FromDays(30);
    public static readonly TimeSpan NeverOlderThan = TimeSpan.FromDays(90);

    private readonly IRoutineStateFiles _files;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private RoutineRunsDocument _current = new() { Runs = [] };

    public RoutineRunStore(IRoutineStateFiles files, TimeProvider time, ILogger<RoutineRunStore> logger)
    {
        _files = files;
        _time = time;
        _logger = logger;
    }

    /// <summary>Set when the history file had to be set aside at load; null otherwise.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>
    /// Loads the file and sweeps every record left <c>running</c> to <c>interrupted_pc</c>. Never throws.
    /// </summary>
    public async Task LoadAndSweepAsync()
    {
        _files.SweepStagingOrphans(FileName);

        RoutineRunsDocument? loaded = null;
        try
        {
            var text = _files.Read(FileName);
            if (text is not null)
            {
                loaded = JsonSerializer.Deserialize<RoutineRunsDocument>(text, RoutineHostStore.JsonOptions);
                if (loaded is null || loaded.FileVersion != RoutineHostDocument.CurrentFileVersion)
                {
                    throw new JsonException("unsupported history file");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // History is display data, but it is still never overwritten blind: set it aside first.
            var aside = _files.Quarantine(FileName, _time.GetUtcNow());
            _logger.LogError(ex, "{File} could not be read; set aside as {Aside}. History starts empty.", FileName, aside);
            LoadWarning = ex.GetType().Name;
            loaded = null;
        }

        var document = loaded ?? new RoutineRunsDocument { Runs = [] };
        var nowMs = NowMs();
        var nextSeq = Math.Max(1, document.NextSeq);
        var runs = new List<RoutineRun>((document.Runs ?? []).Where(r => r is not null && r.RunId is not null));
        var swept = 0;
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].Outcome == RoutineRunOutcomes.Running)
            {
                runs[i] = Interrupted(runs[i], nowMs) with { Seq = nextSeq++ };
                swept++;
            }
        }

        lock (_gate)
        {
            _current = document with { NextSeq = nextSeq, Runs = runs };
        }

        if (swept > 0)
        {
            _logger.LogWarning("{Count} routine run(s) were cut short by the last shutdown; marked interrupted.", swept);
            await SaveAsync();
        }
    }

    /// <summary>
    /// Inserts or replaces a record (by <c>runId</c>), stamping a new <c>seq</c>, applies retention and
    /// saves. Returns the stored record. A failed save is logged, never thrown: history must not stop a run.
    /// </summary>
    public async Task<RoutineRun> UpsertAsync(RoutineRun run)
    {
        RoutineRun stored;
        lock (_gate)
        {
            var runs = new List<RoutineRun>(_current.Runs ?? []);
            stored = run with { Seq = _current.NextSeq };
            var index = runs.FindIndex(r => string.Equals(r.RunId, run.RunId, StringComparison.Ordinal));
            if (index >= 0)
            {
                runs[index] = stored;
            }
            else
            {
                runs.Add(stored);
            }

            _current = _current with { NextSeq = _current.NextSeq + 1, Runs = ApplyRetention(runs, NowMs()) };
        }

        await SaveAsync();
        return stored;
    }

    /// <summary>
    /// One report page for <paramref name="ownerClientId"/>: records with <c>seq &gt; cursor</c>, oldest first,
    /// at most <see cref="MaxPerReport"/>, plus whether more follow (§7.3.6).
    /// </summary>
    public (List<RoutineRun> Page, bool More) Page(string ownerClientId, long cursor)
    {
        lock (_gate)
        {
            var matching = (_current.Runs ?? [])
                .Where(r => string.Equals(r.OwnerClientId, ownerClientId, StringComparison.Ordinal) && (r.Seq ?? 0) > cursor)
                .OrderBy(r => r.Seq)
                .ToList();
            return (matching.Take(MaxPerReport).ToList(), matching.Count > MaxPerReport);
        }
    }

    /// <summary>History, newest first, optionally narrowed.</summary>
    public IReadOnlyList<RoutineRun> Query(string? ownerClientId, string? routineId)
    {
        lock (_gate)
        {
            return (_current.Runs ?? [])
                .Where(r => (ownerClientId is null || string.Equals(r.OwnerClientId, ownerClientId, StringComparison.Ordinal))
                    && (routineId is null || string.Equals(r.RoutineId, routineId, StringComparison.Ordinal)))
                .OrderByDescending(r => r.TriggeredAtUnixMs)
                .ThenByDescending(r => r.Seq)
                .ToList();
        }
    }

    /// <summary>A record by run id, or null.</summary>
    public RoutineRun? Find(string runId)
    {
        lock (_gate)
        {
            return (_current.Runs ?? []).FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
        }
    }

    /// <summary>Deletes every record of one owner (revoke and forget, T7/T8). Throws when the save fails.</summary>
    public async Task ForgetOwnerAsync(string ownerClientId)
    {
        bool changed;
        lock (_gate)
        {
            var runs = _current.Runs ?? [];
            var kept = runs.Where(r => !string.Equals(r.OwnerClientId, ownerClientId, StringComparison.Ordinal)).ToList();
            changed = kept.Count != runs.Count;
            _current = _current with { Runs = kept };
        }

        if (changed)
        {
            await SaveCoreAsync();
        }
    }

    /// <summary>The terminal form of a record that never finished (§8.1).</summary>
    internal static RoutineRun Interrupted(RoutineRun run, long nowMs) => run with
    {
        Outcome = RoutineRunOutcomes.Interrupted,
        ReasonCode = RoutineReasonCodes.InterruptedPc,
        EndedAtUnixMs = nowMs,
        Steps = run.Steps?.Select(s => s.Status is RoutineStepStatuses.Running or RoutineStepStatuses.Pending
            ? s with { Status = RoutineStepStatuses.Skipped, EndedAtUnixMs = s.Status == RoutineStepStatuses.Running ? nowMs : s.EndedAtUnixMs }
            : s).ToList(),
    };

    internal static List<RoutineRun> ApplyRetention(List<RoutineRun> runs, long nowMs)
    {
        var youngMs = (long)KeepYoungerThan.TotalMilliseconds;
        var maxAgeMs = (long)NeverOlderThan.TotalMilliseconds;

        var recentPerRoutine = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in runs.GroupBy(r => (r.OwnerClientId ?? string.Empty) + "\n" + (r.RoutineId ?? string.Empty)))
        {
            foreach (var run in group.OrderByDescending(r => r.TriggeredAtUnixMs).Take(KeepPerRoutine))
            {
                recentPerRoutine.Add(run.RunId!);
            }
        }

        var kept = runs.Where(r =>
            r.Outcome == RoutineRunOutcomes.Running
            || (nowMs - r.TriggeredAtUnixMs < maxAgeMs
                && (recentPerRoutine.Contains(r.RunId!) || nowMs - r.TriggeredAtUnixMs < youngMs)))
            .ToList();

        if (kept.Count > Cap)
        {
            // Oldest first out, but never a running record.
            var droppable = kept.Where(r => r.Outcome != RoutineRunOutcomes.Running)
                .OrderBy(r => r.TriggeredAtUnixMs)
                .Take(kept.Count - Cap)
                .Select(r => r.RunId!)
                .ToHashSet(StringComparer.Ordinal);
            kept = kept.Where(r => !droppable.Contains(r.RunId!)).ToList();
        }

        return kept;
    }

    private async Task SaveAsync()
    {
        try
        {
            await SaveCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving {File} failed; the record stays in memory.", FileName);
        }
    }

    private async Task SaveCoreAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(_current, RoutineHostStore.JsonOptions);
            }

            await _files.WriteAsync(FileName, json);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
