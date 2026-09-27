using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Routines;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// The PC Routines page (routines spec §2.3, S4b, RemEx-pp0rt.9): the routines phones have synced to this
/// PC, grouped by phone, with the four things the PC may do to them: switch one off, Run now, Pause all,
/// and block a phone. <b>The PC never edits a routine</b> (D8, R-UX-35): there is no text entry, no add,
/// no delete, and the header says the phone is the editor.
/// </summary>
/// <remarks>
/// <para>
/// The backend is <see cref="IRoutinesHost"/>, which the embedded host registers in ITS container and
/// publishes after it starts. It is therefore resolved through <c>_resolveHost</c> on every use and never
/// cached as null: a page built before the host came up would otherwise stay "unavailable" for the whole
/// session (the RemEx-n8xk mistake). Once found it is subscribed exactly once.
/// </para>
/// <para>
/// <see cref="IRoutinesHost.Changed"/> fires on a background thread; every refresh is posted to the UI
/// thread and coalesced, so a burst of run updates repaints once.
/// </para>
/// </remarks>
public sealed partial class RoutinesViewModel : ObservableObject, IDisposable
{
    /// <summary>The <see cref="Remex.Core.Models.DashboardProfile.SeenCoachMarks"/> key (§5.3).</summary>
    internal const string CoachKey = "routines";

    /// <summary>History rows shown per routine. The PC keeps 20 per routine (§8.8).</summary>
    internal const int HistoryLimit = 20;

    private readonly Func<IRoutinesHost?> _resolveHost;
    private readonly DashboardLayoutService? _layoutService;
    private readonly ShellViewModel? _shell;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _refreshGate = new();

    private IRoutinesHost? _host;
    private bool _refreshQueued;
    private bool _applyingSnapshot;
    private bool _syncingSelection;
    private bool _disposed;

    public RoutinesViewModel(
        Func<IRoutinesHost?> resolveHost,
        DashboardLayoutService? layoutService,
        ShellViewModel? shell,
        Action<Action>? post = null,
        TimeProvider? time = null,
        ILogger<RoutinesViewModel>? logger = null)
    {
        _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
        _layoutService = layoutService;
        _shell = shell;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;

        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
        }

        InitCoachMark();
        Refresh();
    }

    public ObservableCollection<RoutineOwnerGroupViewModel> Groups { get; } = new();

    /// <summary>Localized problems from the host (an unreadable store, T14).</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>The host backend is running in this process.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>PC Pause all (§8.7). Two-way with the header switch; a change goes to the host.</summary>
    [ObservableProperty]
    private bool _hostPaused;

    /// <summary>Started with <c>--routines-dry-run</c>: the persistent banner (§8.6, §8.9).</summary>
    [ObservableProperty]
    private bool _isDryRun;

    [ObservableProperty]
    private bool _idleSourceMissing;

    [ObservableProperty]
    private bool _sessionSourceMissing;

    [ObservableProperty]
    private RoutineCardViewModel? _selectedRoutine;

    /// <summary>Content at least 900 wide: list and detail side by side (P1); narrower, one column (P2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailPane))]
    private bool _isWideLayout = true;

    /// <summary>The last action's problem ("Couldn't start: already running"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCoachStepOne), nameof(IsCoachStepTwo))]
    private int _coachStep = 1;

    [ObservableProperty]
    private bool _showCoachMark;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowDetailPane))]
    private bool _hasRoutines;

    public bool IsEmpty => IsAvailable && !HasRoutines;

    public bool ShowDetailPane => IsWideLayout && HasRoutines;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool HasWarnings => Warnings.Count > 0;

    public bool IsCoachStepOne => CoachStep == 1;

    public bool IsCoachStepTwo => CoachStep == 2;

    public bool IsReducedMotion => _shell?.IsReducedMotion ?? false;

    /// <summary>
    /// Supplied by the view: shows the Run now / Block confirmation (title, message, confirm text, the
    /// confirm button's classes) and returns whether the person confirmed. Null, or a dialog that cannot
    /// show, means "not confirmed": the destructive action does not happen (fail closed, T21).
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnHostPausedChanged(bool value)
    {
        if (!_applyingSnapshot)
        {
            _ = SetHostPausedAsync(value);
        }
    }

    partial void OnSelectedRoutineChanged(RoutineCardViewModel? oldValue, RoutineCardViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            LoadHistory(newValue);
        }

        _syncingSelection = true;
        try
        {
            foreach (var group in Groups)
            {
                group.SelectedRoutine = newValue is not null && ReferenceEquals(newValue.Group, group) ? newValue : null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>Called by a group's list when its selection changes.</summary>
    internal void OnGroupSelectionChanged(RoutineOwnerGroupViewModel group, RoutineCardViewModel? card)
    {
        if (_syncingSelection)
        {
            return;
        }

        // A list losing its selection because another group's card was picked must not clear the page's.
        if (card is null)
        {
            if (SelectedRoutine is not null && ReferenceEquals(SelectedRoutine.Group, group))
            {
                SelectedRoutine = null;
            }

            return;
        }

        SelectedRoutine = card;
    }

    // ═══════════════ Refresh ═══════════════

    /// <summary>Re-reads the host. Safe to call at any time on the UI thread.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var host = AttachHost();
        IsAvailable = host is not null;
        if (host is null)
        {
            ClearAll();
            return;
        }

        RoutinesHostSnapshot snapshot;
        IReadOnlyList<RoutineRun> history;
        try
        {
            snapshot = host.GetSnapshot();
            history = host.GetHistory();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: reading the routines host failed.");
            StatusMessage = LocalizationService.Instance["Routines_ActionFailed"];
            return;
        }

        Apply(snapshot, history);
    }

    private IRoutinesHost? AttachHost()
    {
        if (_host is not null)
        {
            return _host;
        }

        IRoutinesHost? host;
        try
        {
            host = _resolveHost();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Routines page: the routines host could not be resolved yet.");
            return null;
        }

        if (host is null)
        {
            return null;
        }

        _host = host;
        _host.Changed += OnHostChanged;
        return _host;
    }

    private void OnHostChanged(object? sender, EventArgs e)
    {
        lock (_refreshGate)
        {
            if (_refreshQueued)
            {
                return;
            }

            _refreshQueued = true;
        }

        _post(() =>
        {
            lock (_refreshGate)
            {
                _refreshQueued = false;
            }

            Refresh();
        });
    }

    private void Apply(RoutinesHostSnapshot snapshot, IReadOnlyList<RoutineRun> history)
    {
        var now = _time.GetUtcNow();

        _applyingSnapshot = true;
        try
        {
            HostPaused = snapshot.HostPaused;
        }
        finally
        {
            _applyingSnapshot = false;
        }

        IsDryRun = snapshot.DryRun;

        var routines = snapshot.Owners.SelectMany(o => o.Routines).ToList();
        IdleSourceMissing = snapshot.IdleSource is null
            && routines.Any(r => r.Routine.Trigger?.Type == RoutineTriggerTypes.PcIdle);
        SessionSourceMissing = snapshot.SessionSource is null
            && routines.Any(r => r.Routine.Trigger?.Type == RoutineTriggerTypes.PcSession);

        if (!Warnings.SequenceEqual(snapshot.Warnings))
        {
            Warnings.Clear();
            foreach (var warning in snapshot.Warnings)
            {
                Warnings.Add(warning);
            }

            OnPropertyChanged(nameof(HasWarnings));
        }

        // Newest first per (owner, routine); GetHistory returns newest first already.
        var latestByRoutine = new Dictionary<(string?, string?), RoutineRun>();
        foreach (var run in history)
        {
            latestByRoutine.TryAdd((run.OwnerClientId, run.RoutineId), run);
        }

        ReconcileGroups(snapshot.Owners, latestByRoutine, now);
        HasRoutines = Groups.Any(g => g.Routines.Count > 0);

        if (SelectedRoutine is { } selected)
        {
            var still = Groups.SelectMany(g => g.Routines).FirstOrDefault(c => ReferenceEquals(c, selected));
            if (still is null)
            {
                SelectedRoutine = null;
            }
            else
            {
                selected.ApplyHistory(
                    history.Where(r => r.OwnerClientId == selected.OwnerClientId && r.RoutineId == selected.RoutineId).ToList(),
                    now);
            }
        }
    }

    private void ReconcileGroups(
        IReadOnlyList<RoutineOwnerView> owners,
        IReadOnlyDictionary<(string?, string?), RoutineRun> latest,
        DateTimeOffset now)
    {
        // Keep existing view models by key, so a toggle or a live-run repaint does not rebuild the
        // list under the keyboard focus (R-UX-48) or restart a chip's colour transition.
        var existingGroups = Groups.ToDictionary(g => g.ClientId, StringComparer.Ordinal);
        var groups = new List<RoutineOwnerGroupViewModel>(owners.Count);
        foreach (var owner in owners)
        {
            var group = existingGroups.TryGetValue(owner.ClientId, out var kept)
                ? kept
                : new RoutineOwnerGroupViewModel(this, owner.ClientId);
            group.Apply(owner, now);

            var existingCards = group.Routines.ToDictionary(c => c.RoutineId, StringComparer.Ordinal);
            var cards = new List<RoutineCardViewModel>(owner.Routines.Count);
            foreach (var entry in owner.Routines)
            {
                var id = entry.Routine.Id ?? string.Empty;
                if (cards.Any(c => c.RoutineId == id))
                {
                    continue; // a duplicate id is the validator's problem; show the first
                }

                var card = existingCards.TryGetValue(id, out var keptCard)
                    ? keptCard
                    : new RoutineCardViewModel(this, group, id);
                latest.TryGetValue((owner.ClientId, id), out var run);
                card.Apply(entry, group, run, now);
                cards.Add(card);
            }

            ReplaceIfChanged(group.Routines, cards);
            groups.Add(group);
        }

        ReplaceIfChanged(Groups, groups);
    }

    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
        where T : class
    {
        if (target.Count == items.Count && target.Zip(items).All(p => ReferenceEquals(p.First, p.Second)))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void ClearAll()
    {
        SelectedRoutine = null;
        Groups.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        HasRoutines = false;
        IsDryRun = false;
        IdleSourceMissing = false;
        SessionSourceMissing = false;
    }

    private void LoadHistory(RoutineCardViewModel card)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            card.ApplyHistory(_host.GetHistory(card.OwnerClientId, card.RoutineId), _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: reading a routine's history failed.");
        }
    }

    // ═══════════════ Actions ═══════════════

    /// <summary>PC Pause all, from the header switch or the command palette.</summary>
    internal async Task SetHostPausedAsync(bool paused)
    {
        var host = AttachHost();
        if (host is null)
        {
            return;
        }

        try
        {
            await host.SetHostPausedAsync(paused);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: Pause all could not be saved.");
            StatusMessage = LocalizationService.Instance["Routines_ActionFailed"];
        }

        Refresh();
    }

    /// <summary>The "Pause all routines" palette command.</summary>
    [RelayCommand]
    public Task PauseAll() => SetHostPausedAsync(true);

    /// <summary>The "Resume routines" palette command and the paused banner's Resume.</summary>
    [RelayCommand]
    public Task ResumeAll() => SetHostPausedAsync(false);

    internal async Task SetEnabledAsync(RoutineCardViewModel card, bool enabled)
    {
        var host = AttachHost();
        if (host is null)
        {
            return;
        }

        try
        {
            await host.SetDisabledOnPcAsync(card.OwnerClientId, card.RoutineId, !enabled);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: switching a routine could not be saved.");
            StatusMessage = LocalizationService.Instance["Routines_ActionFailed"];
        }

        // Re-read even on success: the host is the truth, and a failed save must put the switch back.
        Refresh();
    }

    internal async Task SetBlockedAsync(RoutineOwnerGroupViewModel group, bool blocked)
    {
        var host = AttachHost();
        if (host is null)
        {
            return;
        }

        if (blocked)
        {
            // Blocking stops every routine of a phone, including one in progress: confirm it, and a
            // dialog that cannot show declines (the same fail-closed rule as Run now).
            var loc = LocalizationService.Instance;
            if (OnConfirmationRequested is null
                || !await OnConfirmationRequested(
                    RoutineStrings.Format("Routines_BlockConfirm_Title", group.PhoneName),
                    loc["Routines_BlockConfirm_Message"],
                    loc["Routines_BlockConfirm_Btn"],
                    "primary danger"))
            {
                return;
            }
        }

        try
        {
            await host.SetBlockedAsync(group.ClientId, blocked);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: blocking a phone could not be saved.");
            StatusMessage = LocalizationService.Instance["Routines_ActionFailed"];
        }

        Refresh();
    }

    /// <summary>
    /// PC Run now (§8.6, D3, R-UX-36). A routine with a destructive step is confirmed first; the
    /// confirmation is the person's presence at the PC, so only a confirmed run passes
    /// <c>presenceConfirmed: true</c> and skips the countdown. No confirmation wired, a dialog that
    /// could not show, or Cancel: nothing runs.
    /// </summary>
    internal async Task RunNowAsync(RoutineCardViewModel card)
    {
        var host = AttachHost();
        if (host is null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var presenceConfirmed = false;
        if (RoutinePresentation.DestructiveStep(card.Routine) is { } destructive)
        {
            var classes = RoutinePresentation.DiscardsWork(destructive.Verb) ? "primary danger" : "primary warning";
            if (OnConfirmationRequested is null
                || !await OnConfirmationRequested(
                    RoutineStrings.Format("Routines_RunNow_ConfirmTitle", card.Name),
                    RoutineStrings.Format("Routines_RunNow_ConfirmMessage",
                        RoutineStrings.ActionLabel(destructive.Verb),
                        RoutinePresentation.StepList(card.Routine)),
                    loc["Routines_RunNow"],
                    classes))
            {
                return;
            }

            presenceConfirmed = true;
        }

        try
        {
            var run = await host.RunNowAsync(card.OwnerClientId, card.RoutineId, presenceConfirmed);
            StatusMessage = run.Outcome == RoutineRunOutcomes.Skipped
                ? RoutineStrings.Format("Routines_RunNow_Skipped", card.Name, RoutinePresentation.RunStatusText(run))
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: Run now failed to start.");
            StatusMessage = loc["Routines_ActionFailed"];
        }

        Refresh();
    }

    internal void CancelRun(RoutineRunViewModel run)
    {
        if (AttachHost() is not { } host || string.IsNullOrEmpty(run.RunId))
        {
            return;
        }

        try
        {
            host.CancelRun(run.RunId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routines page: cancelling a run failed.");
            StatusMessage = LocalizationService.Instance["Routines_ActionFailed"];
        }

        Refresh();
    }

    [RelayCommand]
    private void DismissStatus() => StatusMessage = null;

    /// <summary>The empty state's "Learn about routines": the app tutorial.</summary>
    [RelayCommand]
    private void LearnAboutRoutines() => _shell?.ReplayTutorial();

    // ═══════════════ Coach mark (§5.3, R-UX-40) ═══════════════

    private void InitCoachMark()
    {
        var seen = _layoutService?.CurrentProfile.SeenCoachMarks;
        CoachStep = 1;
        ShowCoachMark = _layoutService is not null && (seen is null || !seen.Contains(CoachKey));
    }

    /// <summary>Step 1's "Next".</summary>
    [RelayCommand]
    private void NextCoachStep() => CoachStep = 2;

    /// <summary>"Got it" on the last card: hides the tips and records them as seen, so they never auto-show again.</summary>
    [RelayCommand]
    private void DismissCoachMark()
    {
        ShowCoachMark = false;
        CoachStep = 1;
        if (_layoutService is null)
        {
            return;
        }

        var profile = _layoutService.CurrentProfile;
        var seen = profile.SeenCoachMarks ?? new();
        if (seen.Contains(CoachKey))
        {
            return;
        }

        // A copy, not an in-place Add: CurrentProfile is shared, and mutating it would make the save
        // look like a no-op to anything comparing profiles.
        _layoutService.RequestSave(profile with { SeenCoachMarks = [.. seen, CoachKey] });
    }

    /// <summary>The header's "?" replays the tips from the first card.</summary>
    [RelayCommand]
    private void ReplayCoachMark()
    {
        CoachStep = 1;
        ShowCoachMark = true;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsReducedMotion))
        {
            OnPropertyChanged(nameof(IsReducedMotion));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_host is not null)
        {
            _host.Changed -= OnHostChanged;
        }

        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }
    }
}
