using System.Text.Json;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>The host's <c>routines.json</c> (routines spec §6.9). Immutable once published.</summary>
public sealed record RoutineHostDocument
{
    public const int CurrentFileVersion = 1;

    public int FileVersion { get; init; } = CurrentFileVersion;

    /// <summary>PC-side Pause all (§8.7): every owner's automatic routines skip with <c>paused_on_pc</c>.</summary>
    public bool HostPaused { get; init; }

    /// <summary>Per owner phone, keyed by its paired client id. Never merged across owners (§7.4.4).</summary>
    public Dictionary<string, RoutineOwnerRecord>? Owners { get; init; }

    public static RoutineHostDocument Empty { get; } = new() { Owners = new(StringComparer.Ordinal) };

    /// <summary>The owner's record, or null.</summary>
    public RoutineOwnerRecord? Owner(string clientId) =>
        Owners is not null && Owners.TryGetValue(clientId, out var owner) ? owner : null;

    /// <summary>A copy with <paramref name="clientId"/>'s record replaced (or removed when null).</summary>
    public RoutineHostDocument WithOwner(string clientId, RoutineOwnerRecord? owner)
    {
        var owners = new Dictionary<string, RoutineOwnerRecord>(Owners ?? [], StringComparer.Ordinal);
        if (owner is null)
        {
            owners.Remove(clientId);
        }
        else
        {
            owners[clientId] = owner;
        }

        return this with { Owners = owners };
    }
}

/// <summary>One owner's state on this PC: its synced set plus the PC's overrides (§6.1 <c>HostRoutineOverrides</c>).</summary>
public sealed record RoutineOwnerRecord
{
    /// <summary>The phone's sync revision for this PC (§7.4); 0 = never synced.</summary>
    public long Revision { get; init; }

    /// <summary>sha256 (base64) of the applied content (routines + paused), for the equal-revision rule.</summary>
    public string? ContentHash { get; init; }

    public long ReceivedAtUnixMs { get; init; }

    /// <summary>Last sync or run request from this phone; drives the 30-day owner-absent suspension.</summary>
    public long LastSeenUnixMs { get; init; }

    /// <summary>The phone's own Pause all (D4).</summary>
    public bool Paused { get; init; }

    /// <summary>The person at the PC blocked this phone (<c>blocked_by_pc</c>).</summary>
    public bool BlockedByPc { get; init; }

    /// <summary>Routine ids the person at the PC switched off. Never written by the phone.</summary>
    public List<string>? PcDisabled { get; init; }

    /// <summary>The ACCEPTED routines only; a rejected one is never stored (§7.4.2 step 7).</summary>
    public List<Routine>? Routines { get; init; }

    /// <summary>The per-routine results of the last applied sync, re-sent for an idempotent retry.</summary>
    public List<RoutineSyncItemResult>? LastResults { get; init; }

    public bool IsPcDisabled(string? routineId) =>
        routineId is not null && PcDisabled is not null && PcDisabled.Contains(routineId, StringComparer.Ordinal);

    public Routine? Find(string? routineId) =>
        routineId is null ? null : Routines?.FirstOrDefault(r => string.Equals(r.Id, routineId, StringComparison.Ordinal));
}

/// <summary>
/// Owns <c>routines.json</c>: load, validate, persist, owner bookkeeping (routines spec §6.9, §7.4.5, §8.4
/// <c>RoutineStore</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>SAVED BEFORE IT IS TRUE.</b> Every change is computed against the current snapshot, written
/// atomically, and only then published. A failed write leaves the old snapshot in place and reports
/// failure, so the sync handler can answer <c>internal_error</c> and never claim <c>ok</c> for a set that
/// is not on disk (§7.4.2 step 8).
/// </para>
/// <para>
/// <b>AN UNREADABLE OR INVALID FILE IS NEVER EXECUTED AND NEVER OVERWRITTEN (T14, §6.7).</b> It is moved
/// aside as <c>routines.json.unreadable-&lt;utc&gt;</c>, the host starts empty, and <see cref="LoadWarning"/>
/// says so. "Invalid" is all-or-nothing: one routine that fails the validator, or one owner record that
/// does not parse, sets the whole file aside rather than running what was left of it.
/// </para>
/// </remarks>
public sealed class RoutineHostStore
{
    public const string FileName = "routines.json";

    /// <summary>§7.4.4 / Q11.</summary>
    public static readonly TimeSpan OwnerAbsentAfter = TimeSpan.FromDays(30);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRoutineStateFiles _files;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private RoutineHostDocument _current = RoutineHostDocument.Empty;
    private bool _writesBlocked;

    public RoutineHostStore(IRoutineStateFiles files, TimeProvider time, ILogger<RoutineHostStore> logger)
    {
        _files = files;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after a change has been saved and published.</summary>
    public event Action? Changed;

    /// <summary>The published snapshot. Never mutate it.</summary>
    public RoutineHostDocument Current => Volatile.Read(ref _current);

    /// <summary>Set when the file had to be set aside at load (T14); null otherwise.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>
    /// Loads the file once at start. Never throws: an unreadable file is set aside and the host starts empty.
    /// </summary>
    public void Load()
    {
        _files.SweepStagingOrphans(FileName);

        // T14: a file an ordinary user could have planted is never parsed, let alone run.
        if (_files.TrustProblem(FileName) is { } untrusted)
        {
            SetAside($"it is not protected ({untrusted})");
            return;
        }

        string? text;
        try
        {
            text = _files.Read(FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetAside($"the file could not be read ({ex.GetType().Name})");
            return;
        }

        if (text is null)
        {
            Volatile.Write(ref _current, RoutineHostDocument.Empty);
            return;
        }

        var problem = TryParse(text, out var document);
        if (problem is not null)
        {
            SetAside(problem);
            return;
        }

        Volatile.Write(ref _current, document!);
        _logger.LogInformation(
            "Loaded routines for {Owners} phone(s) from {File}.", document!.Owners?.Count ?? 0, FileName);
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the current snapshot, saves the result atomically, then publishes
    /// it. The mutation returns a null document for "nothing to save". Returns false when the save failed,
    /// in which case nothing was published.
    /// </summary>
    public async Task<(bool Saved, T Result)> UpdateAsync<T>(Func<RoutineHostDocument, (RoutineHostDocument? Next, T Result)> mutate)
    {
        T result;
        await _writeGate.WaitAsync();
        try
        {
            // An original that could not be set aside is never overwritten (atomic-save rules: a fallback
            // document is never persisted over the real one). Until it is moved or reads back valid, every
            // save is refused and the caller answers internal_error.
            if (_writesBlocked && !TryUnblock())
            {
                _logger.LogError("{File} could not be set aside; refusing to save over it.", FileName);
                return (false, default!);
            }

            (var next, result) = mutate(Current);
            if (next is null)
            {
                return (true, result);
            }

            try
            {
                await _files.WriteAsync(FileName, JsonSerializer.Serialize(next, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving {File} failed; the previous routines stay in force.", FileName);
                return (false, result);
            }

            Volatile.Write(ref _current, next);
        }
        finally
        {
            _writeGate.Release();
        }

        RaiseChanged();
        return (true, result);
    }

    /// <summary>Whether an owner is suspended for absence right now (§7.4.4).</summary>
    public bool IsSuspended(RoutineOwnerRecord owner) =>
        owner.LastSeenUnixMs > 0
        && _time.GetUtcNow().ToUnixTimeMilliseconds() - owner.LastSeenUnixMs >= (long)OwnerAbsentAfter.TotalMilliseconds;

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A routines change listener failed.");
        }
    }

    private void SetAside(string problem)
    {
        var aside = _files.Quarantine(FileName, _time.GetUtcNow());
        _logger.LogError(
            "{File} is unreadable, invalid or untrusted ({Problem}); set aside as {Aside}. Starting with no PC routines.",
            FileName, problem, aside ?? "(could not be moved)");
        LoadWarning = problem;
        Volatile.Write(ref _current, RoutineHostDocument.Empty);

        // Not moved and still there: the empty document in memory must never be saved over it.
        _writesBlocked = aside is null && _files.Exists(FileName);
    }

    /// <summary>
    /// Clears the write block when the original is gone, reads back valid and trusted (adopted as the current
    /// state), or can now be set aside. Caller holds the write gate.
    /// </summary>
    private bool TryUnblock()
    {
        try
        {
            if (!_files.Exists(FileName))
            {
                _writesBlocked = false;
                return true;
            }

            if (_files.TrustProblem(FileName) is null
                && _files.Read(FileName) is { } text
                && TryParse(text, out var document) is null)
            {
                Volatile.Write(ref _current, document!);
                _writesBlocked = false;
                _logger.LogInformation("{File} reads back valid; saves resume.", FileName);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still unreadable: try to move it aside below.
        }

        if (_files.Quarantine(FileName, _time.GetUtcNow()) is { } aside)
        {
            _writesBlocked = false;
            _logger.LogWarning("{File} was set aside as {Aside} on a later attempt; saves resume.", FileName, aside);
            return true;
        }

        return false;
    }

    /// <summary>Parses and validates a whole file. Returns null on success, else the reason it was refused.</summary>
    internal static string? TryParse(string text, out RoutineHostDocument? document)
    {
        document = null;
        RoutineHostDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RoutineHostDocument>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            return $"not valid JSON ({ex.Path})";
        }
        catch (NotSupportedException)
        {
            return "not valid JSON";
        }

        if (parsed is null)
        {
            return "empty document";
        }

        if (parsed.FileVersion != RoutineHostDocument.CurrentFileVersion)
        {
            return $"fileVersion {parsed.FileVersion}";
        }

        var owners = new Dictionary<string, RoutineOwnerRecord>(StringComparer.Ordinal);
        foreach (var (clientId, owner) in parsed.Owners ?? [])
        {
            if (string.IsNullOrWhiteSpace(clientId) || owner is null)
            {
                return "an owner record is empty";
            }

            foreach (var routine in owner.Routines ?? [])
            {
                var verdict = RoutineValidator.ValidateRoutine(routine);
                if (!verdict.IsValid || !RoutineTriggerTypes.IsHostRun(routine.Trigger?.Type))
                {
                    return $"a stored routine is invalid ({verdict.ReasonCode})";
                }
            }

            owners[clientId] = owner;
        }

        document = parsed with { Owners = owners };
        return null;
    }
}
