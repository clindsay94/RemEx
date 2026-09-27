using System.Globalization;

namespace Remex.Core.Routines;

/// <summary>A validation outcome: <see cref="RoutineReasonCodes.Ok"/> or the FIRST rule that failed.</summary>
/// <param name="ReasonCode">A <see cref="RoutineReasonCodes"/> value.</param>
/// <param name="Detail">The offending field path (<c>steps[2].port</c>), English, never localized.</param>
public readonly record struct RoutineVerdict(string ReasonCode, string? Detail)
{
    public static RoutineVerdict Valid { get; } = new(RoutineReasonCodes.Ok, null);

    public bool IsValid => ReasonCode == RoutineReasonCodes.Ok;
}

/// <summary>The verdict for a whole set: set-level rules, then one verdict per routine in order.</summary>
public sealed record RoutineSetVerdict(string ReasonCode, string? Detail, IReadOnlyList<RoutineVerdict> Routines)
{
    /// <summary>The set-level code when it failed, else the first failing routine's, else <c>ok</c>.</summary>
    public string FirstFailure
    {
        get
        {
            if (ReasonCode != RoutineReasonCodes.Ok)
            {
                return ReasonCode;
            }

            foreach (var verdict in Routines)
            {
                if (!verdict.IsValid)
                {
                    return verdict.ReasonCode;
                }
            }

            return RoutineReasonCodes.Ok;
        }
    }
}

/// <summary>Facts the validator cannot know from the routine alone.</summary>
public sealed record RoutineValidationContext
{
    /// <summary>
    /// The phone's home ids. Null = not checked (the host never has homes, and a <c>home.*</c> routine
    /// reaching it fails <c>trigger_not_pc</c> in the host checks anyway).
    /// </summary>
    public IReadOnlyCollection<string>? KnownHomeIds { get; init; }
}

/// <summary>
/// The routine schema rules of spec §6.2-§6.5 (RemEx-pp0rt.3): field shapes, per-type allowed fields,
/// ranges, step caps, the D1 destructive rules and the static time budgets.
/// </summary>
/// <remarks>
/// <para>
/// <b>First failure wins, in a FIXED ORDER that the Kotlin mirror (<c>RoutineValidator.kt</c>) follows
/// line for line:</b> routine shape (id, name, hostIdentity, revision, timestamps, appearance), then
/// the trigger, then the step count, then each step in order, then the per-type step caps, then the
/// destructive rules, then the budget. The shared fixtures assert the same code on both sides, so a
/// reordering on one side shows up as a fixture failure rather than as a phone and a PC that disagree
/// about why a routine is invalid.
/// </para>
/// <para>
/// This is the SCHEMA validator, shared by the phone editor, the phone store, import, and the host's
/// sync. Checks that need host state - <c>wrong_pc</c>, <c>trigger_not_pc</c>, <c>power_unsupported</c>,
/// <c>launch_not_allowed</c>, source availability - are layered on top by the host (routines S1b/S4).
/// </para>
/// <para>NativeAOT-safe: string switches and char loops, no reflection, no regex.</para>
/// </remarks>
public static class RoutineValidator
{
    private static readonly string[] TriggerFieldOrder =
        ["homeId", "leaveDebounceSeconds", "sensorId", "sensorLabel", "direction", "threshold",
         "sustainSeconds", "idleMinutes", "ignoreWhileMediaPlaying", "sessionState"];

    private static readonly string[] StepFieldOrder =
        ["mac", "broadcastIp", "port", "timeoutSeconds", "seconds", "verb", "delaySeconds",
         "appId", "appLabel", "mediaAction", "target", "title", "body"];

    /// <summary>Validates a whole document: schema version, set-level limits, then every routine.</summary>
    public static RoutineSetVerdict ValidateSet(RoutineSet? set, RoutineValidationContext? context = null)
    {
        if (set is null)
        {
            return new RoutineSetVerdict(RoutineReasonCodes.InvalidField, "set", []);
        }

        if (set.SchemaVersion > RoutineSchema.CurrentVersion)
        {
            return new RoutineSetVerdict(RoutineReasonCodes.SchemaTooNew, "schemaVersion", []);
        }

        if (set.SchemaVersion < 1)
        {
            return new RoutineSetVerdict(RoutineReasonCodes.InvalidField, "schemaVersion", []);
        }

        return ValidateRoutines(set.Routines, context);
    }

    /// <summary>
    /// Validates a list of routines as one set: the 32-per-phone and 16-per-PC limits, then every
    /// routine, with a repeated id reported on its second and later occurrences.
    /// </summary>
    public static RoutineSetVerdict ValidateRoutines(IReadOnlyList<Routine>? routines, RoutineValidationContext? context = null)
    {
        routines ??= [];

        var verdicts = new List<RoutineVerdict>(routines.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var routine in routines)
        {
            var verdict = ValidateRoutine(routine, context);
            var id = routine?.Id;
            if (verdict.IsValid && id is not null && seenIds.Contains(id))
            {
                verdict = new RoutineVerdict(RoutineReasonCodes.DuplicateId, "id");
            }

            if (id is not null)
            {
                seenIds.Add(id);
            }

            verdicts.Add(verdict);
        }

        if (routines.Count > RoutineLimits.MaxRoutinesPerPhone)
        {
            return new RoutineSetVerdict(RoutineReasonCodes.TooManyRoutines, "routines", verdicts);
        }

        var pcRunPerHost = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var routine in routines)
        {
            if (routine?.HostIdentity is { } host && RoutineTriggerTypes.IsHostRun(routine.Trigger?.Type))
            {
                pcRunPerHost[host] = pcRunPerHost.GetValueOrDefault(host) + 1;
                if (pcRunPerHost[host] > RoutineLimits.MaxPcRoutinesPerHost)
                {
                    return new RoutineSetVerdict(RoutineReasonCodes.TooManyRoutines, "routines", verdicts);
                }
            }
        }

        return new RoutineSetVerdict(RoutineReasonCodes.Ok, null, verdicts);
    }

    /// <summary>Validates one routine in isolation. See the class remarks for the check order.</summary>
    public static RoutineVerdict ValidateRoutine(Routine? routine, RoutineValidationContext? context = null)
    {
        if (routine is null || routine.IsMalformed)
        {
            return Invalid("routine");
        }

        if (!IsLowerUuidV4(routine.Id))
        {
            return Invalid("id");
        }

        if (!IsValidName(routine.Name))
        {
            return Invalid("name");
        }

        if (!IsHostIdentity(routine.HostIdentity))
        {
            return Invalid("hostIdentity");
        }

        if (routine.Revision < 1)
        {
            return Invalid("revision");
        }

        if (routine.CreatedAtUnixMs < 0)
        {
            return Invalid("createdAtUnixMs");
        }

        if (routine.UpdatedAtUnixMs < routine.CreatedAtUnixMs)
        {
            return Invalid("updatedAtUnixMs");
        }

        if (routine.Appearance is { } appearance)
        {
            if (appearance.Icon is not null && !IsIconToken(appearance.Icon))
            {
                return Invalid("appearance.icon");
            }

            if (appearance.Color is not null && !IsHexColor(appearance.Color))
            {
                return Invalid("appearance.color");
            }
        }

        var triggerVerdict = ValidateTrigger(routine.Trigger, context);
        if (!triggerVerdict.IsValid)
        {
            return triggerVerdict;
        }

        var hostRun = RoutineTriggerTypes.IsHostRun(routine.Trigger!.Type);
        var steps = routine.Steps;
        if (steps is null || steps.Count == 0)
        {
            return Invalid("steps");
        }

        if (steps.Count > RoutineLimits.MaxSteps)
        {
            return new RoutineVerdict(RoutineReasonCodes.TooManySteps, "steps");
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var stepVerdict = ValidateStep(steps[i], hostRun, $"steps[{i}]");
            if (!stepVerdict.IsValid)
            {
                return stepVerdict;
            }
        }

        if (Count(steps, RoutineStepTypes.Wake) > RoutineLimits.MaxWakeSteps
            || Count(steps, RoutineStepTypes.WaitOnline) > RoutineLimits.MaxWaitOnlineSteps
            || Count(steps, RoutineStepTypes.Notify) > RoutineLimits.MaxNotifySteps)
        {
            return Invalid("steps");
        }

        var destructiveCount = 0;
        var lastDestructiveIndex = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].IsDestructive)
            {
                destructiveCount++;
                lastDestructiveIndex = i;
            }
        }

        if (destructiveCount > RoutineLimits.MaxDestructiveSteps)
        {
            return new RoutineVerdict(RoutineReasonCodes.TooManyDestructive, "steps");
        }

        // PC-run only: nothing can run after the host process is gone or suspended. A phone-run
        // routine may continue after it (a later host step without a waitOnline in between is
        // skipped at run time with after_power_off, and the editor warns).
        if (hostRun && destructiveCount == 1 && lastDestructiveIndex != steps.Count - 1)
        {
            return new RoutineVerdict(RoutineReasonCodes.DestructiveNotLast, $"steps[{lastDestructiveIndex}]");
        }

        var budget = BudgetSeconds(routine);
        var max = hostRun ? RoutineLimits.MaxHostRunBudgetSeconds : RoutineLimits.MaxPhoneRunBudgetSeconds;
        if (budget > max)
        {
            return new RoutineVerdict(RoutineReasonCodes.BudgetExceeded, "steps");
        }

        return RoutineVerdict.Valid;
    }

    /// <summary>
    /// Validates one step on its own: <paramref name="hostRun"/> is whether it belongs to a PC-run
    /// routine. The host uses this for <c>routine_step_request</c> (§7.3.3).
    /// </summary>
    public static RoutineVerdict ValidateStep(RoutineStep? step, bool hostRun)
        => ValidateStep(step, hostRun, "step");

    /// <summary>
    /// The static time budget (§6.5). Phone-run: Σ delay + Σ waitOnline (default 300) + 60 s per
    /// host-executed step + 15 s when a destructive step exists. PC-run: Σ delay + 30 s per step +
    /// 15 s when a destructive step exists. Steps that are not valid contribute what they can.
    /// </summary>
    public static int BudgetSeconds(Routine routine)
    {
        var steps = routine.Steps ?? [];
        var hostRun = RoutineTriggerTypes.IsHostRun(routine.Trigger?.Type);
        long total = 0;
        var anyDestructive = false;
        foreach (var step in steps)
        {
            if (step is null)
            {
                continue;
            }

            if (step.Type == RoutineStepTypes.Delay)
            {
                total += Math.Max(0, step.Seconds ?? 0);
            }

            if (hostRun)
            {
                total += RoutineLimits.HostBudgetPerStepSeconds;
            }
            else if (step.Type == RoutineStepTypes.WaitOnline)
            {
                total += Math.Max(0, step.TimeoutSeconds ?? RoutineLimits.DefaultWaitOnlineSeconds);
            }
            else if (step.IsHostExecuted)
            {
                total += RoutineLimits.PhoneBudgetPerHostStepSeconds;
            }

            anyDestructive |= step.IsDestructive;
        }

        if (anyDestructive)
        {
            total += RoutineLimits.CountdownSeconds;
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    private static RoutineVerdict ValidateTrigger(RoutineTrigger? trigger, RoutineValidationContext? context)
    {
        if (trigger is null || trigger.IsMalformed || trigger.Type is null)
        {
            return Invalid("trigger");
        }

        if (!RoutineTriggerTypes.IsKnown(trigger.Type))
        {
            return new RoutineVerdict(RoutineReasonCodes.UnsupportedTrigger, "trigger.type");
        }

        var present = TriggerFieldsPresent(trigger);
        for (var i = 0; i < TriggerFieldOrder.Length; i++)
        {
            if (present[i] && !IsTriggerFieldAllowed(trigger.Type, TriggerFieldOrder[i]))
            {
                return new RoutineVerdict(RoutineReasonCodes.FieldNotAllowed, "trigger." + TriggerFieldOrder[i]);
            }
        }

        switch (trigger.Type)
        {
            case RoutineTriggerTypes.HomeArrive:
            case RoutineTriggerTypes.HomeLeave:
                if (string.IsNullOrEmpty(trigger.HomeId))
                {
                    return new RoutineVerdict(RoutineReasonCodes.HomeNotSet, "trigger.homeId");
                }

                if (!IsLowerUuidV4(trigger.HomeId))
                {
                    return Invalid("trigger.homeId");
                }

                if (context?.KnownHomeIds is { } homes && !homes.Contains(trigger.HomeId))
                {
                    return new RoutineVerdict(RoutineReasonCodes.HomeNotSet, "trigger.homeId");
                }

                if (trigger.LeaveDebounceSeconds is { } debounce
                    && (debounce < RoutineLimits.MinLeaveDebounceSeconds || debounce > RoutineLimits.MaxLeaveDebounceSeconds))
                {
                    return Invalid("trigger.leaveDebounceSeconds");
                }

                break;

            case RoutineTriggerTypes.PcSensor:
                if (string.IsNullOrEmpty(trigger.SensorId) || trigger.SensorId.Length > RoutineLimits.MaxSensorIdLength)
                {
                    return Invalid("trigger.sensorId");
                }

                if (trigger.SensorLabel is not null && TextLength(trigger.SensorLabel) > RoutineLimits.MaxLabelLength)
                {
                    return Invalid("trigger.sensorLabel");
                }

                if (!RoutineSensorDirections.IsKnown(trigger.Direction))
                {
                    return Invalid("trigger.direction");
                }

                if (trigger.Threshold is not { } threshold || !double.IsFinite(threshold))
                {
                    return Invalid("trigger.threshold");
                }

                if (trigger.SustainSeconds is { } sustain
                    && (sustain < RoutineLimits.MinSustainSeconds || sustain > RoutineLimits.MaxSustainSeconds))
                {
                    return Invalid("trigger.sustainSeconds");
                }

                break;

            case RoutineTriggerTypes.PcIdle:
                if (trigger.IdleMinutes is not { } idle
                    || idle < RoutineLimits.MinIdleMinutes || idle > RoutineLimits.MaxIdleMinutes)
                {
                    return Invalid("trigger.idleMinutes");
                }

                break;

            case RoutineTriggerTypes.PcSession:
                if (!RoutineSessionStates.IsKnown(trigger.SessionState))
                {
                    return Invalid("trigger.sessionState");
                }

                break;
        }

        return RoutineVerdict.Valid;
    }

    private static RoutineVerdict ValidateStep(RoutineStep? step, bool hostRun, string path)
    {
        if (step is null || step.IsMalformed || step.Type is null)
        {
            return Invalid(path);
        }

        if (!RoutineStepTypes.IsKnown(step.Type))
        {
            return new RoutineVerdict(RoutineReasonCodes.UnsupportedStep, path + ".type");
        }

        if (hostRun && RoutineStepTypes.IsPhoneOnly(step.Type))
        {
            return new RoutineVerdict(RoutineReasonCodes.StepNotAllowedOnPc, path + ".type");
        }

        var present = StepFieldsPresent(step);
        for (var i = 0; i < StepFieldOrder.Length; i++)
        {
            if (present[i] && !IsStepFieldAllowed(step.Type, StepFieldOrder[i]))
            {
                return new RoutineVerdict(RoutineReasonCodes.FieldNotAllowed, path + "." + StepFieldOrder[i]);
            }
        }

        switch (step.Type)
        {
            case RoutineStepTypes.Wake:
                if (step.Mac is null)
                {
                    return new RoutineVerdict(RoutineReasonCodes.WakeNoMac, path + ".mac");
                }

                if (!IsUpperMac(step.Mac))
                {
                    return Invalid(path + ".mac");
                }

                if (step.BroadcastIp is not null && !IsIpv4(step.BroadcastIp))
                {
                    return Invalid(path + ".broadcastIp");
                }

                if (step.Port is { } port && (port < RoutineLimits.MinPort || port > RoutineLimits.MaxPort))
                {
                    return Invalid(path + ".port");
                }

                break;

            case RoutineStepTypes.WaitOnline:
                if (step.TimeoutSeconds is { } timeout
                    && (timeout < RoutineLimits.MinWaitOnlineSeconds || timeout > RoutineLimits.MaxWaitOnlineSeconds))
                {
                    return Invalid(path + ".timeoutSeconds");
                }

                break;

            case RoutineStepTypes.Delay:
                if (step.Seconds is not { } seconds
                    || seconds < RoutineLimits.MinDelaySeconds || seconds > RoutineLimits.MaxDelaySeconds)
                {
                    return Invalid(path + ".seconds");
                }

                break;

            case RoutineStepTypes.Power:
                // WAKEONLAN is not in the allowed set: reserved in v1 (D5), invalid_field like any
                // unknown verb.
                if (!RoutinePowerVerbs.IsAllowed(step.Verb))
                {
                    return Invalid(path + ".verb");
                }

                if (step.DelaySeconds is { } delay)
                {
                    if (!RoutinePowerVerbs.IsDelayable(step.Verb))
                    {
                        return new RoutineVerdict(RoutineReasonCodes.FieldNotAllowed, path + ".delaySeconds");
                    }

                    if (delay < RoutineLimits.MinPowerDelaySeconds || delay > RoutineLimits.MaxPowerDelaySeconds)
                    {
                        return Invalid(path + ".delaySeconds");
                    }
                }

                break;

            case RoutineStepTypes.LaunchApp:
                if (!IsGuid(step.AppId))
                {
                    return Invalid(path + ".appId");
                }

                if (step.AppLabel is not null && TextLength(step.AppLabel) > RoutineLimits.MaxLabelLength)
                {
                    return Invalid(path + ".appLabel");
                }

                break;

            case RoutineStepTypes.Media:
                if (!RoutineMediaActions.IsKnown(step.MediaAction))
                {
                    return Invalid(path + ".mediaAction");
                }

                break;

            case RoutineStepTypes.Notify:
                if (!RoutineNotifyTargets.IsKnown(step.Target))
                {
                    return Invalid(path + ".target");
                }

                var titleLength = TextLength(RoutineText.Sanitize(step.Title));
                if (titleLength < 1 || titleLength > RoutineLimits.MaxNotifyTitleLength)
                {
                    return Invalid(path + ".title");
                }

                if (TextLength(RoutineText.Sanitize(step.Body)) > RoutineLimits.MaxNotifyBodyLength)
                {
                    return Invalid(path + ".body");
                }

                break;
        }

        return RoutineVerdict.Valid;
    }

    private static bool[] TriggerFieldsPresent(RoutineTrigger t) =>
    [
        t.HomeId is not null, t.LeaveDebounceSeconds is not null, t.SensorId is not null,
        t.SensorLabel is not null, t.Direction is not null, t.Threshold is not null,
        t.SustainSeconds is not null, t.IdleMinutes is not null, t.IgnoreWhileMediaPlaying is not null,
        t.SessionState is not null,
    ];

    private static bool[] StepFieldsPresent(RoutineStep s) =>
    [
        s.Mac is not null, s.BroadcastIp is not null, s.Port is not null, s.TimeoutSeconds is not null,
        s.Seconds is not null, s.Verb is not null, s.DelaySeconds is not null, s.AppId is not null,
        s.AppLabel is not null, s.MediaAction is not null, s.Target is not null, s.Title is not null,
        s.Body is not null,
    ];

    private static bool IsTriggerFieldAllowed(string type, string field) => type switch
    {
        RoutineTriggerTypes.HomeArrive => field is "homeId",
        RoutineTriggerTypes.HomeLeave => field is "homeId" or "leaveDebounceSeconds",
        RoutineTriggerTypes.PcSensor => field is "sensorId" or "sensorLabel" or "direction" or "threshold" or "sustainSeconds",
        RoutineTriggerTypes.PcIdle => field is "idleMinutes" or "ignoreWhileMediaPlaying",
        RoutineTriggerTypes.PcSession => field is "sessionState",
        _ => false, // nfc.tap and manual take no fields
    };

    private static bool IsStepFieldAllowed(string type, string field) => type switch
    {
        RoutineStepTypes.Wake => field is "mac" or "broadcastIp" or "port",
        RoutineStepTypes.WaitOnline => field is "timeoutSeconds",
        RoutineStepTypes.Delay => field is "seconds",
        RoutineStepTypes.Power => field is "verb" or "delaySeconds",
        RoutineStepTypes.LaunchApp => field is "appId" or "appLabel",
        RoutineStepTypes.Media => field is "mediaAction",
        RoutineStepTypes.Notify => field is "target" or "title" or "body",
        _ => false,
    };

    private static int Count(List<RoutineStep> steps, string type)
    {
        var n = 0;
        foreach (var step in steps)
        {
            if (step?.Type == type)
            {
                n++;
            }
        }

        return n;
    }

    private static RoutineVerdict Invalid(string detail) => new(RoutineReasonCodes.InvalidField, detail);

    private static bool IsValidName(string? name)
    {
        if (name is null || RoutineText.HasControlOrLineBreak(name))
        {
            return false;
        }

        var length = TextLength(name.Trim());
        return length >= 1 && length <= RoutineLimits.MaxNameLength;
    }

    /// <summary>User-perceived characters (extended grapheme clusters).</summary>
    private static int TextLength(string text) =>
        text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    private static bool IsHexLower(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    private static bool IsHexAny(char c) => IsHexLower(c) || c is >= 'A' and <= 'F';

    private static bool IsHostIdentity(string? value)
    {
        if (value is null || value.Length != 16)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!IsHexLower(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>xxxxxxxx-xxxx-4xxx-[89ab]xxx-xxxxxxxxxxxx, lower-case.</summary>
    private static bool IsLowerUuidV4(string? value)
    {
        if (!IsGuidShape(value, lowerOnly: true))
        {
            return false;
        }

        return value![14] == '4' && value[19] is '8' or '9' or 'a' or 'b';
    }

    /// <summary>Any-case 8-4-4-4-12 GUID (an <c>AppEntry.Id</c>).</summary>
    private static bool IsGuid(string? value) => IsGuidShape(value, lowerOnly: false);

    private static bool IsGuidShape(string? value, bool lowerOnly)
    {
        if (value is null || value.Length != 36)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-')
                {
                    return false;
                }
            }
            else if (lowerOnly ? !IsHexLower(c) : !IsHexAny(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>AA:BB:CC:DD:EE:FF, upper-case.</summary>
    private static bool IsUpperMac(string value)
    {
        if (value.Length != 17)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i % 3 == 2)
            {
                if (c != ':')
                {
                    return false;
                }
            }
            else if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Dotted quad; each part 0-255 without leading zeros.</summary>
    private static bool IsIpv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            var n = 0;
            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }

                n = (n * 10) + (c - '0');
            }

            if (n > 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>1-32 characters of [a-z0-9_].</summary>
    private static bool IsIconToken(string value)
    {
        if (value.Length is < 1 or > RoutineLimits.MaxIconLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>#RRGGBB, either case.</summary>
    private static bool IsHexColor(string value)
    {
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < 7; i++)
        {
            if (!IsHexAny(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Plain-text rules for routine user text (names, notify text).</summary>
public static class RoutineText
{
    /// <summary>
    /// C0 (U+0000-U+001F), DEL and C1 (U+007F-U+009F), and the Unicode line/paragraph separators
    /// (U+2028, U+2029). Mirrored in Kotlin.
    /// </summary>
    public static bool IsControlOrLineBreak(char c) =>
        c < (char)0x20 || (c >= (char)0x7F && c <= (char)0x9F) || c == (char)0x2028 || c == (char)0x2029;

    public static bool HasControlOrLineBreak(string text)
    {
        foreach (var c in text)
        {
            if (IsControlOrLineBreak(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>notify</c> text as it is presented: control characters and line breaks removed, then trimmed.
    /// Null becomes empty.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!HasControlOrLineBreak(text))
        {
            return text.Trim();
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsControlOrLineBreak(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }
}
