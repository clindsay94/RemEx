using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Remex.Core.Routines;
using Remex.Desktop.Services.Routines;

namespace Remex.Desktop.Tests.Routines;

/// <summary>An in-memory <see cref="IRoutinesHost"/> that records every call the PC Routines page makes.</summary>
internal sealed class FakeRoutinesHost : IRoutinesHost
{
    public event EventHandler? Changed;

    public RoutinesHostSnapshot Snapshot { get; set; } = new(false, false, "idle", "session", [], []);

    public List<RoutineRun> Runs { get; } = new();

    public List<bool> PausedCalls { get; } = new();

    public List<(string Owner, string Routine, bool Disabled)> DisabledCalls { get; } = new();

    public List<(string Owner, bool Blocked)> BlockedCalls { get; } = new();

    public List<(string Owner, string Routine, bool Presence)> RunNowCalls { get; } = new();

    public List<string> CancelCalls { get; } = new();

    /// <summary>What RunNowAsync returns; a running record by default.</summary>
    public Func<string, string, RoutineRun> RunNowResult { get; set; } = (owner, id) => new RoutineRun
    {
        RunId = "run-now",
        OwnerClientId = owner,
        RoutineId = id,
        Outcome = RoutineRunOutcomes.Running,
        Source = RoutineRunSources.ManualPcRunNow,
    };

    public bool ThrowOnMutate { get; set; }

    public RoutinesHostSnapshot GetSnapshot() => Snapshot;

    public IReadOnlyList<RoutineRun> GetHistory(string? ownerClientId = null, string? routineId = null) =>
        Runs.Where(r => (ownerClientId is null || r.OwnerClientId == ownerClientId)
                        && (routineId is null || r.RoutineId == routineId))
            .ToList();

    public Task SetHostPausedAsync(bool paused)
    {
        ThrowIfAsked();
        PausedCalls.Add(paused);
        Snapshot = Snapshot with { HostPaused = paused };
        return Task.CompletedTask;
    }

    public Task SetDisabledOnPcAsync(string ownerClientId, string routineId, bool disabled)
    {
        ThrowIfAsked();
        DisabledCalls.Add((ownerClientId, routineId, disabled));
        Snapshot = Snapshot with
        {
            Owners = Snapshot.Owners.Select(o => o.ClientId != ownerClientId ? o : o with
            {
                Routines = o.Routines.Select(e => e.Routine.Id == routineId ? e with { DisabledOnPc = disabled } : e).ToList(),
            }).ToList(),
        };
        return Task.CompletedTask;
    }

    public Task SetBlockedAsync(string ownerClientId, bool blocked)
    {
        ThrowIfAsked();
        BlockedCalls.Add((ownerClientId, blocked));
        Snapshot = Snapshot with
        {
            Owners = Snapshot.Owners.Select(o => o.ClientId == ownerClientId ? o with { BlockedByPc = blocked } : o).ToList(),
        };
        return Task.CompletedTask;
    }

    public Task<RoutineRun> RunNowAsync(string ownerClientId, string routineId, bool presenceConfirmed)
    {
        ThrowIfAsked();
        RunNowCalls.Add((ownerClientId, routineId, presenceConfirmed));
        return Task.FromResult(RunNowResult(ownerClientId, routineId));
    }

    public bool CancelRun(string runId)
    {
        CancelCalls.Add(runId);
        return true;
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfAsked()
    {
        if (ThrowOnMutate)
        {
            throw new InvalidOperationException("save failed");
        }
    }

    // ── Builders ──

    public static Routine Routine(string id, string name, params RoutineStep[] steps) => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        Revision = 1,
        UpdatedAtUnixMs = 1_758_000_000_000,
        Trigger = new RoutineTrigger { Type = RoutineTriggerTypes.PcIdle, IdleMinutes = 30 },
        Steps = steps.ToList(),
    };

    public static RoutineStep Power(string verb) => new() { Type = RoutineStepTypes.Power, Verb = verb };

    public static RoutineStep Notify() => new() { Type = RoutineStepTypes.Notify, Target = RoutineNotifyTargets.Pc, Title = "t" };

    public static RoutineOwnerView Owner(string clientId, string? phone, params RoutineHostEntry[] routines) =>
        new(clientId, phone, false, false, false, 1_758_000_000_000, routines.ToList());

    public static RoutineHostEntry Entry(Routine routine, bool disabledOnPc = false, bool running = false) =>
        new(routine, disabledOnPc, running);

    public void SetOwners(params RoutineOwnerView[] owners) => Snapshot = Snapshot with { Owners = owners.ToList() };
}
