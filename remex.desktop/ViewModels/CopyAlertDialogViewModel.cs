using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Backs the Copy-to picker (RemEx-8wpvr.6): applies one sensor alert's threshold, direction and
/// severity to other sensors, defaulting to sensors that share the source's unit. Constructed fresh
/// per open, the same way <see cref="SetAlertViewModel"/> is — SettingsView.axaml.cs news it up from
/// the row's stored alert and the section's <see cref="ISensorCatalog"/> / <see cref="SensorAlertStore"/>,
/// wiring <see cref="RequestClose"/> to close the window (there is no result to hand back: Apply
/// writes straight through the store).
/// </summary>
public partial class CopyAlertDialogViewModel : ObservableObject
{
    private readonly SensorAlert _source;
    private readonly SensorAlertStore _store;
    private readonly string? _sourceUnit;
    private readonly System.Collections.Generic.List<CopyAlertCandidateViewModel> _allCandidates;

    /// <summary>Fired once, by either <see cref="ApplyCommand"/> or <see cref="CancelCommand"/>, so
    /// the view can close the window. No subscriber (as in tests) simply means nothing closes.</summary>
    public event Action? RequestClose;

    /// <summary>"Copy alert from {DisplayName}: {direction} {threshold} · {severity}" — the same
    /// summary composition as <c>SensorAlertsSectionViewModel.BuildRow</c> (reusing
    /// <c>Canvas_AlertBellConfigured</c> and <c>SensorReadingFormat</c> rather than recomposing it).</summary>
    public string Header { get; }

    /// <summary>False — and <see cref="SameUnitOnly"/> forced false — when the source sensor's unit
    /// is unknown (not currently connected): there is nothing to match same-unit against.</summary>
    public bool SameUnitToggleEnabled { get; }

    [ObservableProperty]
    private bool _sameUnitOnly;

    partial void OnSameUnitOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAllSensors));
        RebuildCandidates();
    }

    /// <summary>Bound to the picker's "Show all sensors" ToggleSwitch — the inverse of
    /// <see cref="SameUnitOnly"/>, kept separate so the toggle's ON state reads as "show everything"
    /// while the underlying flag names what it actually restricts.</summary>
    public bool ShowAllSensors
    {
        get => !SameUnitOnly;
        set => SameUnitOnly = !value;
    }

    [ObservableProperty]
    private string _filter = string.Empty;

    partial void OnFilterChanged(string value) => RebuildCandidates();

    /// <summary>The filtered, unit-scoped candidate list the ListBox shows.</summary>
    public ObservableCollection<CopyAlertCandidateViewModel> Candidates { get; } = new();

    /// <summary>Bound to the ListBox's <c>SelectionMode="Multiple"</c> SelectedItems —
    /// DiagnosticLogsView's pattern, since Copy to… is inherently multi-target.</summary>
    public ObservableCollection<CopyAlertCandidateViewModel> SelectedCandidates { get; } = new();

    [ObservableProperty]
    private string _applyLabel = string.Empty;

    /// <summary>The overwrite-warning tooltip text when any selected candidate already has a
    /// configured alert; null otherwise so the ToolTip does not open on an empty string.</summary>
    [ObservableProperty]
    private string? _overwriteHint;

    public CopyAlertDialogViewModel(SensorAlert source, ISensorCatalog catalog, SensorAlertStore store)
    {
        _source = source;
        _store = store;

        var resolved = catalog.TryResolve(source.SensorName, out var info) ? info : null;
        _sourceUnit = resolved?.Unit;
        var sourceUnitKnown = !string.IsNullOrEmpty(_sourceUnit);

        // Same composition as SensorAlertsSectionViewModel.BuildRow's Summary (RemEx-8wpvr's notes):
        // AlertDirection_*/AlertSeverity_* for the words, SensorReadingFormat for the threshold, and
        // the already-localized Canvas_AlertBellConfigured template rather than a second one.
        var direction = LocalizationService.Instance[$"{nameof(AlertDirection)}_{source.Direction}"];
        var severity = LocalizationService.Instance[$"{nameof(AlertSeverity)}_{source.Severity}"];
        var threshold = SensorReadingFormat.FormatReading(source.Threshold, _sourceUnit);
        var summary = string.Format(
            LocalizationService.Instance["Canvas_AlertBellConfigured"], direction, threshold, severity);
        var sourceDisplayName = resolved?.DisplayName ?? source.SensorName;

        Header = string.Format(LocalizationService.Instance["CopyAlert_Title"], sourceDisplayName, summary);

        SameUnitToggleEnabled = sourceUnitKnown;
        _sameUnitOnly = sourceUnitKnown; // direct field set — no OnSameUnitOnlyChanged before _allCandidates exists

        _allCandidates = catalog.Known
            .Where(s => !string.Equals(s.Name, source.SensorName, StringComparison.OrdinalIgnoreCase))
            .Select(s => new CopyAlertCandidateViewModel(s, store.TryGet(s.Name, out _)))
            .ToList();

        SelectedCandidates.CollectionChanged += OnSelectionChanged;

        RebuildCandidates();
        UpdateApplyState();
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateApplyState();

    private void RebuildCandidates()
    {
        var query = _allCandidates.AsEnumerable();

        if (SameUnitOnly)
            query = query.Where(c => string.Equals(c.Unit?.Trim(), _sourceUnit?.Trim(), StringComparison.OrdinalIgnoreCase));

        var filter = Filter.Trim();
        if (filter.Length > 0)
            query = query.Where(c => c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var scoped = query.ToList();

        // Diff Candidates against the newly-scoped list rather than Clear()+refill: Clear() raises a
        // Reset, which empties the ListBox's SelectedItems and, through its two-way binding,
        // SelectedCandidates too — silently dropping the whole multi-selection on every keystroke in
        // Filter or every flip of Show all sensors. Removing what fell out and inserting what's new,
        // in place, leaves surviving items untouched so their selection survives.
        for (var i = Candidates.Count - 1; i >= 0; i--)
        {
            if (!scoped.Contains(Candidates[i]))
                Candidates.RemoveAt(i);
        }

        for (var i = 0; i < scoped.Count; i++)
        {
            if (i >= Candidates.Count || !ReferenceEquals(Candidates[i], scoped[i]))
                Candidates.Insert(i, scoped[i]);
        }

        // A selection that fell out of the filtered/scoped view no longer applies.
        for (var i = SelectedCandidates.Count - 1; i >= 0; i--)
        {
            if (!Candidates.Contains(SelectedCandidates[i]))
                SelectedCandidates.RemoveAt(i);
        }
    }

    private void UpdateApplyState()
    {
        ApplyLabel = string.Format(LocalizationService.Instance["CopyAlert_Apply"], SelectedCandidates.Count);
        OverwriteHint = SelectedCandidates.Any(c => c.HasAlert)
            ? LocalizationService.Instance["CopyAlert_OverwriteHint"]
            : null;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes one <see cref="SensorAlert"/> per selected candidate, carrying the source's
    /// threshold, direction and severity, through <see cref="SensorAlertStore.Set"/> — overwriting
    /// any existing alert on that sensor.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        foreach (var candidate in SelectedCandidates.ToList())
        {
            _store.Set(_source with { SensorName = candidate.Name });
        }

        RequestClose?.Invoke();
    }

    private bool CanApply() => SelectedCandidates.Count > 0;

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}

/// <summary>One selectable row in the Copy-to picker: a candidate sensor plus whether it already has
/// a configured alert (drives the row's "has alert" marker and the overwrite hint).</summary>
public sealed class CopyAlertCandidateViewModel
{
    public CopyAlertCandidateViewModel(SensorInfo info, bool hasAlert)
    {
        Name = info.Name;
        DisplayName = info.DisplayName;
        Unit = info.Unit;
        HasAlert = hasAlert;
    }

    /// <summary>The raw sensor name — the key used everywhere else (alerts, cards, pins).</summary>
    public string Name { get; }

    public string DisplayName { get; }

    public string? Unit { get; }

    /// <summary>True when this candidate already has a configured alert; drives the row's bell
    /// marker and (in aggregate) <see cref="CopyAlertDialogViewModel.OverwriteHint"/>.</summary>
    public bool HasAlert { get; }
}
