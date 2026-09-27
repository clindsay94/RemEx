using Remex.Core.Routines;

namespace Remex.Desktop.Services.Routines;

/// <summary>How a routine chip, run or step is tinted on the PC Routines page (routines spec §2.3, R-UX-54).</summary>
public enum RoutineTone
{
    /// <summary>The neutral plate.</summary>
    Neutral,

    /// <summary>A power step that discards unsaved work (the error role).</summary>
    Destructive,
}

/// <summary>
/// The text the PC Routines page shows for triggers, steps, outcomes and reasons (routines spec §2.3,
/// §10.1). Pure functions over <see cref="LocalizationService"/>, so the view model stays testable and no
/// sentence is ever built by concatenation (R-UX-56): every piece is its own localized token or a format
/// string.
/// </summary>
public static class RoutinePresentation
{
    /// <summary>
    /// Power verbs that throw away unsaved work: they carry the error tint on chips and a "primary danger"
    /// Run now confirm (R-UX-36, R-UX-54). Sleep and hibernate keep the session, so they confirm with
    /// "primary warning" instead.
    /// </summary>
    private static readonly HashSet<string> DiscardsWorkVerbs = new(StringComparer.Ordinal)
    {
        RoutinePowerVerbs.Shutdown,
        RoutinePowerVerbs.ForceShutdown,
        RoutinePowerVerbs.Restart,
        RoutinePowerVerbs.ForceRestart,
        RoutinePowerVerbs.RestartToUefi,
        RoutinePowerVerbs.SignOut,
    };

    /// <summary>True when <paramref name="verb"/> ends the session and loses unsaved work.</summary>
    public static bool DiscardsWork(string? verb) => verb is not null && DiscardsWorkVerbs.Contains(verb);

    /// <summary>The chip text of a trigger ("PC idle 30 min").</summary>
    public static string TriggerLabel(RoutineTrigger? trigger)
    {
        var loc = LocalizationService.Instance;
        return trigger?.Type switch
        {
            RoutineTriggerTypes.Manual => loc["Routine_Trigger_Manual"],
            RoutineTriggerTypes.HomeArrive => loc["Routine_Trigger_HomeArrive"],
            RoutineTriggerTypes.HomeLeave => loc["Routine_Trigger_HomeLeave"],
            RoutineTriggerTypes.NfcTap => loc["Routine_Trigger_Nfc"],
            RoutineTriggerTypes.PcIdle => RoutineStrings.Format("Routine_Trigger_Idle", trigger.IdleMinutes ?? 0),
            RoutineTriggerTypes.PcSession => trigger.SessionState == RoutineSessionStates.Unlocked
                ? loc["Routine_Trigger_Unlocked"]
                : loc["Routine_Trigger_Locked"],
            RoutineTriggerTypes.PcSensor => RoutineStrings.Format(
                trigger.Direction == RoutineSensorDirections.Below ? "Routine_Trigger_SensorBelow" : "Routine_Trigger_SensorAbove",
                string.IsNullOrWhiteSpace(trigger.SensorLabel) ? trigger.SensorId : trigger.SensorLabel,
                (trigger.Threshold ?? 0).ToString("0.##", loc.Culture)),
            _ => loc["Routine_Trigger_Unknown"],
        };
    }

    /// <summary>The chip text of one step ("Shut down", "Open Steam").</summary>
    public static string StepLabel(RoutineStep? step)
    {
        var loc = LocalizationService.Instance;
        return step?.Type switch
        {
            RoutineStepTypes.Power => RoutinePowerVerbs.IsAllowed(step.Verb)
                ? RoutineStrings.ActionLabel(step.Verb)
                : loc["Routine_Step_Unknown"],
            RoutineStepTypes.LaunchApp => RoutineStrings.Format("Routine_Step_LaunchApp",
                string.IsNullOrWhiteSpace(step.AppLabel) ? step.AppId : step.AppLabel),
            RoutineStepTypes.Media => step.MediaAction switch
            {
                RoutineMediaActions.Next => loc["Routine_Step_MediaNext"],
                RoutineMediaActions.Previous => loc["Routine_Step_MediaPrevious"],
                _ => loc["Routine_Step_MediaPlayPause"],
            },
            RoutineStepTypes.Notify => step.Target == RoutineNotifyTargets.Pc
                ? loc["Routine_Step_NotifyPc"]
                : loc["Routine_Step_NotifyPhone"],
            RoutineStepTypes.Delay => RoutineStrings.Format("Routine_Step_Delay", step.Seconds ?? 0),
            RoutineStepTypes.Wake => loc["Routine_Step_Wake"],
            RoutineStepTypes.WaitOnline => loc["Routine_Step_WaitOnline"],
            _ => loc["Routine_Step_Unknown"],
        };
    }

    /// <summary>The tone of one step's chip.</summary>
    public static RoutineTone StepTone(RoutineStep? step) =>
        step?.Type == RoutineStepTypes.Power && DiscardsWork(step.Verb) ? RoutineTone.Destructive : RoutineTone.Neutral;

    /// <summary>The routine's destructive (D1) step, if any: the one Run now must confirm.</summary>
    public static RoutineStep? DestructiveStep(Routine routine) =>
        routine.Steps?.FirstOrDefault(s => s is not null && s.IsDestructive);

    /// <summary>The short label of a run outcome ("Done", "Failed").</summary>
    public static string OutcomeLabel(string? outcome)
    {
        var key = outcome switch
        {
            RoutineRunOutcomes.Running => "Routine_Outcome_Running",
            RoutineRunOutcomes.Succeeded => "Routine_Outcome_Succeeded",
            RoutineRunOutcomes.Failed => "Routine_Outcome_Failed",
            RoutineRunOutcomes.Cancelled => "Routine_Outcome_Cancelled",
            RoutineRunOutcomes.Skipped => "Routine_Outcome_Skipped",
            _ => "Routine_Outcome_Interrupted",
        };
        return LocalizationService.Instance[key];
    }

    /// <summary>The short label of a step status ("Done", "Simulated").</summary>
    public static string StepStatusLabel(string? status)
    {
        var key = status switch
        {
            RoutineStepStatuses.Pending => "Routine_StepStatus_Pending",
            RoutineStepStatuses.Running => "Routine_StepStatus_Running",
            RoutineStepStatuses.Succeeded => "Routine_StepStatus_Succeeded",
            RoutineStepStatuses.Failed => "Routine_StepStatus_Failed",
            RoutineStepStatuses.Skipped => "Routine_StepStatus_Skipped",
            RoutineStepStatuses.Cancelled => "Routine_StepStatus_Cancelled",
            RoutineStepStatuses.Simulated => "Routine_StepStatus_Simulated",
            RoutineStepStatuses.Expired => "Routine_StepStatus_Expired",
            _ => "Routine_StepStatus_Pending",
        };
        return LocalizationService.Instance[key];
    }

    /// <summary>
    /// The history text of a reason code (§10.1 "History text"), or null when there is none to show:
    /// <c>ok</c> (the outcome already says Done) and any code this build has no text for.
    /// </summary>
    public static string? ReasonText(string? reasonCode)
    {
        if (string.IsNullOrEmpty(reasonCode) || reasonCode == RoutineReasonCodes.Ok)
        {
            return null;
        }

        var key = "Routine_History_" + reasonCode;
        var text = LocalizationService.Instance[key];
        // LocalizationService returns the key itself for a missing entry. A code from a newer phone or
        // host must not put the raw key on screen.
        return text == key ? null : text;
    }

    /// <summary>The run's status line: the reason's history text when it has one, else the outcome label.</summary>
    public static string RunStatusText(RoutineRun run) => ReasonText(run.ReasonCode) ?? OutcomeLabel(run.Outcome);

    /// <summary>The history texts of a run's attributes ("Simulated", "Dry run"), in a stable order.</summary>
    public static IReadOnlyList<string> AttributeTexts(RoutineRun run) =>
        (run.Attributes ?? [])
            .Select(ReasonText)
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A run time as the page shows it: the short time for today, the short date and time otherwise,
    /// in the app's own culture (not the OS's).
    /// </summary>
    public static string FormatTime(long unixMs, DateTimeOffset now)
    {
        var culture = LocalizationService.Instance.Culture;
        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("t", culture)
            : local.ToString("g", culture);
    }

    /// <summary>A date only, for "not seen since" and "updated".</summary>
    public static string FormatDate(long unixMs)
    {
        var culture = LocalizationService.Instance.Culture;
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("d", culture);
    }

    /// <summary>The phone's display name, or the shared "a paired phone" fallback.</summary>
    public static string PhoneName(string? phoneName) =>
        string.IsNullOrWhiteSpace(phoneName)
            ? LocalizationService.Instance["Routine_Countdown_PhoneUnknown"]
            : phoneName;

    /// <summary>The steps as the Run now confirm lists them ("Message, Message, Sleep").</summary>
    public static string StepList(Routine routine) =>
        string.Join(", ", (routine.Steps ?? []).Select(StepLabel));
}
