using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Remex.Core.Routines;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// One owner phone's group on the PC Routines page (routines spec §2.3, §7.4.4): its name, its state
/// ("Paused from", blocked, suspended with last seen), and its routines.
/// </summary>
public sealed partial class RoutineOwnerGroupViewModel : ObservableObject
{
    private readonly RoutinesViewModel _page;

    internal RoutineOwnerGroupViewModel(RoutinesViewModel page, string clientId)
    {
        _page = page;
        ClientId = clientId;
    }

    /// <summary>The owner's paired client id. Never shown.</summary>
    public string ClientId { get; }

    /// <summary>The page, for bindings to its commands.</summary>
    public RoutinesViewModel Page => _page;

    public ObservableCollection<RoutineCardViewModel> Routines { get; } = new();

    [ObservableProperty]
    private string _phoneName = string.Empty;

    [ObservableProperty]
    private bool _paused;

    [ObservableProperty]
    private bool _blocked;

    [ObservableProperty]
    private bool _suspended;

    [ObservableProperty]
    private string _lastSeenText = string.Empty;

    /// <summary>The group's state line ("Paused from Pixel"), or empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _blockButtonText = string.Empty;

    [ObservableProperty]
    private string _blockButtonName = string.Empty;

    /// <summary>The list's selected card. Setting it selects that card on the page and clears the other groups.</summary>
    [ObservableProperty]
    private RoutineCardViewModel? _selectedRoutine;

    public bool HasStateText => !string.IsNullOrEmpty(StateText);

    partial void OnStateTextChanged(string value) => OnPropertyChanged(nameof(HasStateText));

    partial void OnSelectedRoutineChanged(RoutineCardViewModel? value) => _page.OnGroupSelectionChanged(this, value);

    /// <summary>Blocks or unblocks this phone's routines on this PC.</summary>
    [RelayCommand]
    private Task ToggleBlockAsync() => _page.SetBlockedAsync(this, !Blocked);

    internal void Apply(RoutineOwnerView owner, DateTimeOffset now)
    {
        var loc = LocalizationService.Instance;
        PhoneName = RoutinePresentation.PhoneName(owner.PhoneName);
        Paused = owner.Paused;
        Blocked = owner.BlockedByPc;
        Suspended = owner.Suspended;
        LastSeenText = owner.LastSeenUnixMs > 0
            ? RoutineStrings.Format("Routines_Group_LastSeen", RoutinePresentation.FormatTime(owner.LastSeenUnixMs, now))
            : string.Empty;

        // One state line, most severe first: a blocked phone runs nothing, a suspended one nothing
        // automatic, a paused one nothing automatic by its own choice.
        StateText = owner.BlockedByPc
            ? RoutineStrings.Format("Routines_Group_Blocked", PhoneName)
            : owner.Suspended
                ? RoutineStrings.Format("Routines_Group_Suspended", PhoneName, RoutinePresentation.FormatDate(owner.LastSeenUnixMs))
                : owner.Paused
                    ? RoutineStrings.Format("Routines_Group_Paused", PhoneName)
                    : string.Empty;

        BlockButtonText = owner.BlockedByPc ? loc["Routines_Unblock"] : loc["Routines_Block"];
        BlockButtonName = RoutineStrings.Format(owner.BlockedByPc ? "Routines_UnblockName" : "Routines_BlockName", PhoneName);
    }
}

/// <summary>One routine card: the list row and, when selected, the detail pane (routines spec §2.3 P1, P2).</summary>
public sealed partial class RoutineCardViewModel : ObservableObject
{
    private readonly RoutinesViewModel _page;
    private bool _applying;

    internal RoutineCardViewModel(RoutinesViewModel page, RoutineOwnerGroupViewModel group, string routineId)
    {
        _page = page;
        Group = group;
        RoutineId = routineId;
    }

    public RoutineOwnerGroupViewModel Group { get; }

    /// <summary>The page, for bindings to its commands.</summary>
    public RoutinesViewModel Page => _page;

    public string OwnerClientId => Group.ClientId;

    public string RoutineId { get; }

    /// <summary>The definition as last synced. Read-only on the PC (D8).</summary>
    public Routine Routine { get; private set; } = new();

    public ObservableCollection<RoutineChipViewModel> Chips { get; } = new();

    /// <summary>The first chips, as many as the card shows before "N more".</summary>
    public ObservableCollection<RoutineChipViewModel> CardChips { get; } = new();

    public ObservableCollection<RoutineRunViewModel> History { get; } = new();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _triggerText = string.Empty;

    [ObservableProperty]
    private string _moreChipsText = string.Empty;

    [ObservableProperty]
    private bool _running;

    /// <summary>Switched off on this PC (<c>pcDisabled</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn))]
    private bool _disabledOnPc;

    [ObservableProperty]
    private bool _canToggle;

    /// <summary>Why the card cannot run, or what switched it off, when that is not obvious from the switch.</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _noteIsProblem;

    [ObservableProperty]
    private string _lastRunText = string.Empty;

    [ObservableProperty]
    private MaterialIconKind _lastRunIcon = MaterialIconKind.ClockOutline;

    [ObservableProperty]
    private string _lastRunKind = RoutineRunViewModel.KindNone;

    [ObservableProperty]
    private string _fromText = string.Empty;

    [ObservableProperty]
    private string _toggleName = string.Empty;

    [ObservableProperty]
    private string _runNowName = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasDestructiveStep;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool HasMoreChips => !string.IsNullOrEmpty(MoreChipsText);

    public bool HasHistory => History.Count > 0;

    /// <summary>The enable switch. On means "may run on this PC"; switching it goes to the host and the phone.</summary>
    public bool IsOn
    {
        get => !DisabledOnPc;
        set
        {
            if (value == !DisabledOnPc)
            {
                return;
            }

            DisabledOnPc = !value;
            if (!_applying)
            {
                _ = _page.SetEnabledAsync(this, value);
            }
        }
    }

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    partial void OnMoreChipsTextChanged(string value) => OnPropertyChanged(nameof(HasMoreChips));

    /// <summary>Runs the routine now from the PC (confirmation first when it has a power step).</summary>
    [RelayCommand]
    private Task RunNowAsync() => _page.RunNowAsync(this);

    /// <summary>Flips the enable switch (the context menu's Turn off / Turn on, and Space in the list).</summary>
    [RelayCommand]
    private void ToggleEnabled()
    {
        if (CanToggle)
        {
            IsOn = !IsOn;
        }
    }

    /// <summary>The number of chips a card shows before collapsing the rest into "N more".</summary>
    internal const int CardChipLimit = 3;

    internal void Apply(RoutineHostEntry entry, RoutineOwnerGroupViewModel group, RoutineRun? latest, DateTimeOffset now)
    {
        var loc = LocalizationService.Instance;
        _applying = true;
        try
        {
            Routine = entry.Routine;
            Name = string.IsNullOrWhiteSpace(entry.Routine.Name) ? loc["Routines_Card_Unnamed"] : entry.Routine.Name!;
            TriggerText = RoutinePresentation.TriggerLabel(entry.Routine.Trigger);
            Running = entry.Running || latest?.Outcome == RoutineRunOutcomes.Running;
            DisabledOnPc = entry.DisabledOnPc;
            OnPropertyChanged(nameof(IsOn));
            CanToggle = !entry.Routine.IsMalformed && !group.Blocked;
            HasDestructiveStep = RoutinePresentation.DestructiveStep(entry.Routine) is not null;

            ApplyChips(entry.Routine, latest);

            (Note, NoteIsProblem) = entry.Routine.IsMalformed
                ? (loc["Routines_Card_Malformed"], true)
                : group.Blocked
                    ? (loc["Routines_Card_Blocked"], true)
                    : !entry.Routine.Enabled
                        ? (RoutineStrings.Format("Routines_Card_OffOnPhone", group.PhoneName), false)
                        : (string.Empty, false);

            if (Running)
            {
                LastRunText = loc["Routines_Card_Running"];
                LastRunKind = RoutineRunViewModel.KindRunning;
                LastRunIcon = RoutineRunViewModel.IconFor(RoutineRunViewModel.KindRunning);
            }
            else if (latest is not null)
            {
                var kind = RoutineRunViewModel.KindOf(latest.Outcome);
                LastRunText = RoutineStrings.Format("Routines_Card_LastRun",
                    RoutinePresentation.RunStatusText(latest),
                    RoutinePresentation.FormatTime(latest.EndedAtUnixMs ?? latest.StartedAtUnixMs, now));
                LastRunKind = kind;
                LastRunIcon = RoutineRunViewModel.IconFor(kind);
            }
            else
            {
                LastRunText = loc["Routines_Card_NeverRan"];
                LastRunKind = RoutineRunViewModel.KindNone;
                LastRunIcon = RoutineRunViewModel.IconFor(RoutineRunViewModel.KindNone);
            }

            FromText = entry.Routine.UpdatedAtUnixMs > 0
                ? RoutineStrings.Format("Routines_Detail_From", group.PhoneName, RoutinePresentation.FormatDate(entry.Routine.UpdatedAtUnixMs))
                : RoutineStrings.Format("Routines_Detail_FromNoDate", group.PhoneName);
            ToggleName = RoutineStrings.Format("Routines_Card_ToggleName", Name);
            RunNowName = RoutineStrings.Format("Routines_RunNowName", Name);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyChips(Routine routine, RoutineRun? latest)
    {
        var steps = routine.Steps ?? [];
        var live = latest?.Outcome == RoutineRunOutcomes.Running ? latest : null;

        var chips = new List<RoutineChipViewModel>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var status = live?.Steps?.FirstOrDefault(s => s.Index == i)?.Status;
            chips.Add(new RoutineChipViewModel(
                RoutinePresentation.StepLabel(steps[i]),
                RoutinePresentation.StepTone(steps[i]) == RoutineTone.Destructive,
                status));
        }

        // Update in place when the chain is the same shape, so a live run's chips recolour through
        // their BrushTransition (P-M4) instead of being replaced with no transition at all.
        if (Chips.Count == chips.Count && Chips.Select(c => (c.Text, c.IsDestructive)).SequenceEqual(chips.Select(c => (c.Text, c.IsDestructive))))
        {
            for (var i = 0; i < chips.Count; i++)
            {
                Chips[i].SetStatus(chips[i].Status);
            }
        }
        else
        {
            Chips.Clear();
            CardChips.Clear();
            foreach (var chip in chips)
            {
                Chips.Add(chip);
            }

            foreach (var chip in chips.Take(CardChipLimit))
            {
                CardChips.Add(chip);
            }
        }

        MoreChipsText = chips.Count > CardChipLimit
            ? RoutineStrings.Format("Routines_Card_MoreChips", chips.Count - CardChipLimit)
            : string.Empty;
    }

    /// <summary>Replaces the history rows, keeping each existing row (and whether it is expanded) by run id.</summary>
    internal void ApplyHistory(IReadOnlyList<RoutineRun> runs, DateTimeOffset now)
    {
        var existing = History.ToDictionary(r => r.RunId, StringComparer.Ordinal);
        var rows = new List<RoutineRunViewModel>(runs.Count);
        foreach (var run in runs.Take(RoutinesViewModel.HistoryLimit))
        {
            var id = run.RunId ?? string.Empty;
            var row = existing.TryGetValue(id, out var kept) ? kept : new RoutineRunViewModel(_page, id);
            row.Apply(run, Routine, Group.PhoneName, now);
            rows.Add(row);
        }

        if (!History.SequenceEqual(rows))
        {
            History.Clear();
            foreach (var row in rows)
            {
                History.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasHistory));
    }
}

/// <summary>One step token in a routine's chip chain (routines spec §2.5 "Chip chain").</summary>
public sealed partial class RoutineChipViewModel : ObservableObject
{
    public RoutineChipViewModel(string text, bool isDestructive, string? status)
    {
        Text = text;
        IsDestructive = isDestructive;
        SetStatus(status);
    }

    public string Text { get; }

    /// <summary>A power step that discards unsaved work: the error role, never colour alone (the text says the verb).</summary>
    public bool IsDestructive { get; }

    public string? Status { get; private set; }

    /// <summary>The step is running in a live run (P-M4).</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The step finished in a live run.</summary>
    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isFailed;

    internal void SetStatus(string? status)
    {
        Status = status;
        IsActive = status == RoutineStepStatuses.Running;
        IsDone = status is RoutineStepStatuses.Succeeded or RoutineStepStatuses.Simulated;
        IsFailed = status == RoutineStepStatuses.Failed;
    }
}

/// <summary>One run in a routine's history, expandable to its steps (routines spec §8.8, R-UX-50).</summary>
public sealed partial class RoutineRunViewModel : ObservableObject
{
    internal const string KindNone = "none";
    internal const string KindRunning = "running";
    internal const string KindSucceeded = "succeeded";
    internal const string KindFailed = "failed";
    internal const string KindCancelled = "cancelled";
    internal const string KindSkipped = "skipped";
    internal const string KindInterrupted = "interrupted";

    private readonly RoutinesViewModel _page;

    internal RoutineRunViewModel(RoutinesViewModel page, string runId)
    {
        _page = page;
        RunId = runId;
    }

    public string RunId { get; }

    public ObservableCollection<RoutineRunStepViewModel> Steps { get; } = new();

    [ObservableProperty]
    private string _whenText = string.Empty;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _attributesText = string.Empty;

    [ObservableProperty]
    private string _kind = KindNone;

    [ObservableProperty]
    private MaterialIconKind _icon = MaterialIconKind.ClockOutline;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _expandName = string.Empty;

    [ObservableProperty]
    private string _cancelName = string.Empty;

    public bool HasSteps => Steps.Count > 0;

    public bool HasAttributes => !string.IsNullOrEmpty(AttributesText);

    partial void OnAttributesTextChanged(string value) => OnPropertyChanged(nameof(HasAttributes));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Cancels this run from the PC (<c>cancelled_on_pc</c>).</summary>
    [RelayCommand]
    private void Cancel() => _page.CancelRun(this);

    internal static string KindOf(string? outcome) => outcome switch
    {
        RoutineRunOutcomes.Running => KindRunning,
        RoutineRunOutcomes.Succeeded => KindSucceeded,
        RoutineRunOutcomes.Failed => KindFailed,
        RoutineRunOutcomes.Cancelled => KindCancelled,
        RoutineRunOutcomes.Skipped => KindSkipped,
        _ => KindInterrupted,
    };

    /// <summary>Every outcome has its own icon as well as its text (R-UX-50: never colour alone).</summary>
    internal static MaterialIconKind IconFor(string kind) => kind switch
    {
        KindRunning => MaterialIconKind.PlayCircleOutline,
        KindSucceeded => MaterialIconKind.CheckCircleOutline,
        KindFailed => MaterialIconKind.AlertCircleOutline,
        KindCancelled => MaterialIconKind.CancelBold,
        KindSkipped => MaterialIconKind.SkipNextCircleOutline,
        KindInterrupted => MaterialIconKind.StopCircleOutline,
        _ => MaterialIconKind.ClockOutline,
    };

    internal void Apply(RoutineRun run, Routine routine, string phoneName, DateTimeOffset now)
    {
        var loc = LocalizationService.Instance;
        Kind = KindOf(run.Outcome);
        Icon = IconFor(Kind);
        IsRunning = run.Outcome == RoutineRunOutcomes.Running;
        WhenText = RoutinePresentation.FormatTime(run.StartedAtUnixMs > 0 ? run.StartedAtUnixMs : run.TriggeredAtUnixMs, now);
        SourceText = run.TestRun
            ? RoutineStrings.Format("Routines_History_TestFrom", phoneName)
            : RoutineStrings.SourceLabel(run.Source);
        StatusText = RoutinePresentation.RunStatusText(run);
        AttributesText = string.Join(", ", RoutinePresentation.AttributeTexts(run));
        ExpandName = RoutineStrings.Format("Routines_History_ExpandName", WhenText);
        CancelName = RoutineStrings.Format("Routines_History_CancelName", WhenText);

        var steps = routine.Steps ?? [];
        var rows = (run.Steps ?? [])
            .OrderBy(s => s.Index)
            .Select(s => new RoutineRunStepViewModel(
                RoutineStrings.Format("Routines_History_StepNumber", s.Index + 1),
                s.Index >= 0 && s.Index < steps.Count ? RoutinePresentation.StepLabel(steps[s.Index]) : loc["Routine_Step_Unknown"],
                RoutinePresentation.StepStatusLabel(s.Status),
                RoutinePresentation.ReasonText(s.ReasonCode) ?? string.Empty,
                s.Status ?? RoutineStepStatuses.Pending))
            .ToList();

        if (!Steps.Select(s => (s.Label, s.StatusText, s.ReasonText)).SequenceEqual(rows.Select(s => (s.Label, s.StatusText, s.ReasonText))))
        {
            Steps.Clear();
            foreach (var row in rows)
            {
                Steps.Add(row);
            }

            OnPropertyChanged(nameof(HasSteps));
        }
    }
}

/// <summary>One step of a run, as the expanded history row lists it.</summary>
public sealed record RoutineRunStepViewModel(string NumberText, string Label, string StatusText, string ReasonText, string Status)
{
    public bool HasReason => !string.IsNullOrEmpty(ReasonText);

    public bool IsFailed => Status == RoutineStepStatuses.Failed;

    public bool IsDone => Status is RoutineStepStatuses.Succeeded or RoutineStepStatuses.Simulated;

    public MaterialIconKind Icon => Status switch
    {
        RoutineStepStatuses.Succeeded => MaterialIconKind.CheckCircleOutline,
        RoutineStepStatuses.Simulated => MaterialIconKind.TestTube,
        RoutineStepStatuses.Failed => MaterialIconKind.AlertCircleOutline,
        RoutineStepStatuses.Running => MaterialIconKind.PlayCircleOutline,
        RoutineStepStatuses.Cancelled => MaterialIconKind.CancelBold,
        RoutineStepStatuses.Skipped => MaterialIconKind.SkipNextCircleOutline,
        RoutineStepStatuses.Expired => MaterialIconKind.TimerOffOutline,
        _ => MaterialIconKind.CircleOutline,
    };
}
