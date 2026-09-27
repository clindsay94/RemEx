using Remex.Core.Messages.Routines;
using Remex.Core.Routines;
using Remex.Core.Services;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// This PC's routine identity: <c>HostIdentity.KeyFor(own SPKI)</c>, the key every synced routine must carry
/// (routines spec §6.2, <c>wrong_pc</c>). Derived once; the certificate is never rotated silently
/// (docs/REGRESSION-GUARDS.md).
/// </summary>
public sealed class RoutineHostIdentity(Func<string?> spkiPin)
{
    private string? _identity;

    /// <summary>The 16-hex identity, or null while the certificate cannot be read.</summary>
    public string? Get()
    {
        if (_identity is not null)
        {
            return _identity;
        }

        try
        {
            return _identity = Remex.Core.Security.HostIdentity.KeyFor(spkiPin());
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The host's checks on a synced set (routines spec §7.4.2 step 7, T5): the shared schema validator first,
/// then what only this PC can know.
/// </summary>
/// <remarks>
/// <para>
/// Host checks, in order, after the schema: the trigger is <c>pc.*</c> (<c>trigger_not_pc</c>), the routine
/// is bound to THIS PC (<c>wrong_pc</c>: <c>hostIdentity == HostIdentity.KeyFor(own SPKI)</c>), the trigger's
/// source exists here (<c>idle_source_unavailable</c>, <c>session_source_unavailable</c>,
/// <c>sensor_unavailable</c>), every power verb is one this PC advertises (<c>power_unsupported</c>), and
/// every <c>launchApp.appId</c> names an entry in <c>launchers.json</c> whose target is not a network path
/// (<c>launch_not_allowed</c>).
/// </para>
/// <para>
/// Passing here does not make a routine safe to run forever: everything is checked again at run time
/// (T6), because the launcher list and the verb table can change after the sync.
/// </para>
/// </remarks>
public sealed class RoutineHostValidator
{
    private readonly ILauncherStorageService _launchers;
    private readonly IHostCapabilitiesProvider _capabilities;
    private readonly RoutineTriggerAvailability _availability;
    private readonly Func<string?> _ownIdentity;
    private readonly RoutineSensorCatalog? _sensors;

    /// <param name="sensors">
    /// This PC's sensor catalog (routines S5). Null means no sensor source: every <c>pc.sensor</c> routine
    /// is refused with <c>sensor_unavailable</c>, as before S5.
    /// </param>
    public RoutineHostValidator(
        ILauncherStorageService launchers,
        IHostCapabilitiesProvider capabilities,
        RoutineTriggerAvailability availability,
        Func<string?> ownIdentity,
        RoutineSensorCatalog? sensors = null)
    {
        _launchers = launchers;
        _capabilities = capabilities;
        _availability = availability;
        _ownIdentity = ownIdentity;
        _sensors = sensors;
    }

    /// <summary>One result per routine, in request order.</summary>
    public async Task<List<RoutineSyncItemResult>> ValidateAsync(IReadOnlyList<Routine> routines)
    {
        var setVerdict = RoutineValidator.ValidateRoutines(routines);
        var ownIdentity = _ownIdentity();
        var advertised = _capabilities.GetCurrent().RoutinePowerVerbs ?? [];
        var entries = routines.Any(r => r?.Steps?.Any(s => s?.Type == RoutineStepTypes.LaunchApp) == true)
            ? await _launchers.LoadEntriesAsync()
            : [];
        var sensorIds = _sensors is not null && _availability.SensorAvailable
            && routines.Any(r => r?.Trigger?.Type == RoutineTriggerTypes.PcSensor)
            ? await _sensors.GetSensorIdsAsync()
            : null;

        var results = new List<RoutineSyncItemResult>(routines.Count);
        for (var i = 0; i < routines.Count; i++)
        {
            var routine = routines[i];
            var verdict = setVerdict.ReasonCode != RoutineReasonCodes.Ok
                ? new RoutineVerdict(setVerdict.ReasonCode, setVerdict.Detail)
                : setVerdict.Routines[i];
            if (verdict.IsValid)
            {
                verdict = HostCheck(routine, ownIdentity, advertised, entries, sensorIds);
            }

            results.Add(new RoutineSyncItemResult
            {
                RoutineId = routine?.Id,
                Accepted = verdict.IsValid,
                ReasonCode = verdict.ReasonCode,
                Detail = verdict.Detail,
            });
        }

        return results;
    }

    private RoutineVerdict HostCheck(
        Routine routine, string? ownIdentity, List<string> advertised, List<Remex.Core.Models.AppEntry> entries, IReadOnlySet<string>? sensorIds)
    {
        var trigger = routine.Trigger!.Type;
        if (!RoutineTriggerTypes.IsHostRun(trigger))
        {
            return new RoutineVerdict(RoutineReasonCodes.TriggerNotPc, "trigger.type");
        }

        if (ownIdentity is null || !string.Equals(routine.HostIdentity, ownIdentity, StringComparison.Ordinal))
        {
            return new RoutineVerdict(RoutineReasonCodes.WrongPc, "hostIdentity");
        }

        switch (trigger)
        {
            case RoutineTriggerTypes.PcIdle when _availability.IdleSourceId is null:
                return new RoutineVerdict(RoutineReasonCodes.IdleSourceUnavailable, "trigger.type");
            case RoutineTriggerTypes.PcSession when _availability.SessionSourceId is null:
                return new RoutineVerdict(RoutineReasonCodes.SessionSourceUnavailable, "trigger.type");
            case RoutineTriggerTypes.PcSensor when !_availability.SensorAvailable || _sensors is null:
                return new RoutineVerdict(RoutineReasonCodes.SensorUnavailable, "trigger.type");

            // §8.5.1: refused only when this PC's catalog genuinely lacks the sensor. No sample in time
            // (a sampler that failed) is the same answer; the sync handler treats it as transient.
            case RoutineTriggerTypes.PcSensor when sensorIds is null || !sensorIds.Contains(routine.Trigger.SensorId!):
                return new RoutineVerdict(RoutineReasonCodes.SensorUnavailable, "trigger.sensorId");
        }

        var steps = routine.Steps!;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Type == RoutineStepTypes.Power && !advertised.Contains(step.Verb!, StringComparer.Ordinal))
            {
                return new RoutineVerdict(RoutineReasonCodes.PowerUnsupported, $"steps[{i}].verb");
            }

            if (step.Type == RoutineStepTypes.LaunchApp && !IsLaunchable(step.AppId, entries))
            {
                return new RoutineVerdict(RoutineReasonCodes.LaunchNotAllowed, $"steps[{i}].appId");
            }
        }

        return RoutineVerdict.Valid;
    }

    private static bool IsLaunchable(string? appId, List<Remex.Core.Models.AppEntry> entries)
    {
        if (!Guid.TryParse(appId, out var id))
        {
            return false;
        }

        var entry = entries.FirstOrDefault(e => e is not null && e.Id == id);
        if (entry is null || string.IsNullOrWhiteSpace(entry.TargetPath))
        {
            return false;
        }

        try
        {
            return !AppLauncherService.IsRejectedNetworkPath(Path.GetFullPath(entry.TargetPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
