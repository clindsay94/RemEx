using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Backs the Settings "Sensor alerts" card (RemEx-8wpvr.5): every configured alert, sorted by
/// display name, with Edit / Copy to… / Remove per row, plus Acknowledge all and a confirmed Reset
/// all. Reads <see cref="SensorAlertStore"/> and <see cref="SensorAlertTracker"/> live and resolves
/// display name, unit and connectivity through <see cref="ISensorCatalog"/> — it never references
/// <c>CanvasDashboardViewModel</c> directly, which is the whole point of the catalog seam (RemEx-8wpvr.2).
/// </summary>
public partial class SensorAlertsSectionViewModel : ObservableObject, IDisposable
{
    private readonly SensorAlertStore _store;
    private readonly SensorAlertTracker _tracker;
    private readonly ISensorCatalog _catalog;

    public SensorAlertsSectionViewModel(SensorAlertStore store, SensorAlertTracker tracker, ISensorCatalog catalog)
    {
        _store = store;
        _tracker = tracker;
        _catalog = catalog;

        _store.Changed += OnStoreChanged;
        _tracker.TrippedChanged += OnTrippedChanged;

        RebuildRows();
    }

    /// <summary>Every configured alert, sorted by display name (stored name when the sensor is not
    /// resolvable through <see cref="ISensorCatalog"/>).</summary>
    public ObservableCollection<SensorAlertRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private bool _isEmpty = true;

    partial void OnIsEmptyChanged(bool value) => ResetAllCommand.NotifyCanExecuteChanged();

    /// <summary>Mirrors <see cref="SensorAlertTracker.TrippedCount"/>. Backs <see cref="AcknowledgeAllCommand"/>'s
    /// CanExecute the same way <c>ShellViewModel.AlertBadgeCount</c> mirrors the tracker for the sidebar badge.</summary>
    [ObservableProperty]
    private int _trippedCount;

    partial void OnTrippedCountChanged(int value) => AcknowledgeAllCommand.NotifyCanExecuteChanged();

    /// <summary>Raised when a row's Edit button is clicked. The view opens <c>SetAlertDialog</c> the
    /// same way <c>CanvasView.axaml.cs</c> does and reports the result back through
    /// <see cref="ApplyEditResult"/>.</summary>
    public event Action<string, SensorAlert?>? EditRequested;

    /// <summary>Raised when a row's Copy to… button is clicked. Intentionally has no subscriber yet —
    /// wiring the copy-to picker is RemEx-8wpvr.6.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>
    /// Delegate set by the View to display a confirmation dialog (RemEx-07jx's pattern, built through
    /// <see cref="Remex.Desktop.Views.ConfirmationDialogHost"/>). Parameters: (title, message,
    /// confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    [RelayCommand(CanExecute = nameof(CanAcknowledgeAll))]
    private void AcknowledgeAll() => _tracker.AcknowledgeAll();

    private bool CanAcknowledgeAll() => TrippedCount > 0;

    /// <summary>
    /// Resets every configured alert after a confirmation. Fails closed: with no dialog wired, or the
    /// user declining, nothing is cleared — the same pattern as every other destructive action in
    /// Settings (RemEx-6p1f). Counts the alerts about to be removed itself rather than trusting a
    /// caller-supplied count, so the confirmation body can never drift from what Clear() actually does.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResetAll))]
    private async Task ResetAllAsync()
    {
        var count = _store.All.Count;
        if (count == 0) return;

        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Settings_Alerts_ResetConfirmTitle"],
                string.Format(LocalizationService.Instance["Settings_Alerts_ResetConfirmBody"], count),
                LocalizationService.Instance["Settings_Alerts_ResetAll"]))
        {
            return;
        }

        _store.Clear();
    }

    private bool CanResetAll() => !IsEmpty;

    /// <summary>
    /// Applies a SetAlertDialog result the same way <c>CanvasDashboardViewModel.ApplySensorAlert</c>
    /// does: null, or the empty-SensorName sentinel ClearAlert passes for explicit removal, removes the
    /// stored alert; anything else upserts it.
    /// </summary>
    public void ApplyEditResult(string sensorName, SensorAlert? result)
    {
        if (result is null || string.IsNullOrEmpty(result.SensorName))
            _store.Remove(sensorName);
        else
            _store.Set(result);
    }

    /// <summary>
    /// Rebuilds every row from the store. Called on a language change: each row holds an
    /// ALREADY-FORMATTED <see cref="SensorAlertRowViewModel.Summary"/> (and tripped summary), so
    /// nothing in the binding layer can re-translate them — they have to be rebuilt, exactly as
    /// <c>SettingsViewModel.RefreshPairedDevices</c> has to for the paired-device rows (RemEx-q3h0's
    /// pattern). Deliberately the same code path as the <see cref="SensorAlertStore.Changed"/>
    /// handler rather than a second one, so the two can never format a row differently.
    /// </summary>
    public void Refresh() => RebuildRows();

    private void OnStoreChanged() => RebuildRows();

    private void OnTrippedChanged() => RefreshTripState();

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var row in _store.All
                     .Select(BuildRow)
                     .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Rows.Add(row);
        }

        IsEmpty = Rows.Count == 0;
        RefreshTripState();
    }

    private SensorAlertRowViewModel BuildRow(SensorAlert alert)
    {
        var resolved = _catalog.TryResolve(alert.SensorName, out var info) ? info : null;
        var displayName = resolved?.DisplayName ?? alert.SensorName;
        var isDisconnected = resolved is null || !resolved.IsConnected;

        // Same composition as CanvasCardViewModel.AlertTooltip's configured branch (RemEx-8wpvr.4):
        // AlertDirection_*/AlertSeverity_* for the words, SensorReadingFormat for the threshold, and
        // the already-localized Canvas_AlertBellConfigured template so the two surfaces read
        // identically in every language without a second "Settings_Alerts_Above/Below" key.
        var direction = LocalizationService.Instance[$"{nameof(AlertDirection)}_{alert.Direction}"];
        var severity = LocalizationService.Instance[$"{nameof(AlertSeverity)}_{alert.Severity}"];
        var threshold = SensorReadingFormat.FormatReading(alert.Threshold, resolved?.Unit);
        var summary = string.Format(
            LocalizationService.Instance["Canvas_AlertBellConfigured"], direction, threshold, severity);

        return new SensorAlertRowViewModel(
            alert.SensorName,
            displayName,
            isDisconnected,
            summary,
            resolved?.Unit,
            onEdit: name => EditRequested?.Invoke(name, _store.TryGet(name, out var current) ? current : null),
            onCopyTo: name => CopyRequested?.Invoke(name),
            onRemove: name => _store.Remove(name));
    }

    /// <summary>
    /// Refreshes each row's tripped state and the section's tripped count from the tracker without
    /// rebuilding <see cref="Rows"/> — <see cref="SensorAlertTracker.TrippedChanged"/> fires far more
    /// often than the store changes (every trip/acknowledge), and reallocating the row list and its
    /// commands on every one of those would be wasteful and would drop the ListBox's selection.
    /// </summary>
    private void RefreshTripState()
    {
        var tripped = _tracker.Tripped;
        foreach (var row in Rows)
        {
            var trip = tripped.FirstOrDefault(t =>
                string.Equals(t.SensorName, row.SensorName, StringComparison.OrdinalIgnoreCase));
            row.ApplyTrip(trip);
        }

        TrippedCount = _tracker.TrippedCount;
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _tracker.TrippedChanged -= OnTrippedChanged;
    }
}

/// <summary>One row of the Settings "Sensor alerts" card: a single configured <see cref="SensorAlert"/>
/// plus whatever <see cref="ISensorCatalog"/> could resolve about the sensor it names.</summary>
public partial class SensorAlertRowViewModel : ObservableObject
{
    private readonly string? _unit;
    private readonly Action<string> _onEdit;
    private readonly Action<string> _onCopyTo;
    private readonly Action<string> _onRemove;

    public SensorAlertRowViewModel(
        string sensorName,
        string displayName,
        bool isDisconnected,
        string summary,
        string? unit,
        Action<string> onEdit,
        Action<string> onCopyTo,
        Action<string> onRemove)
    {
        SensorName = sensorName;
        DisplayName = displayName;
        IsDisconnected = isDisconnected;
        Summary = summary;
        _unit = unit;
        _onEdit = onEdit;
        _onCopyTo = onCopyTo;
        _onRemove = onRemove;
    }

    /// <summary>The raw sensor name — the key used everywhere else (alerts, cards, pins).</summary>
    public string SensorName { get; }

    /// <summary>The catalog's display name, or the stored sensor name when the catalog cannot resolve
    /// it (unknown or currently disconnected sensor).</summary>
    public string DisplayName { get; }

    /// <summary>True when <see cref="ISensorCatalog"/> could not resolve the sensor, or resolved it as
    /// not currently reporting. Rows in this state show <see cref="DisplayName"/> in the muted colour.</summary>
    public bool IsDisconnected { get; }

    /// <summary>"{direction} {threshold} · {severity}", e.g. "Above 90 °C · Critical".</summary>
    public string Summary { get; }

    [ObservableProperty]
    private bool _isTripped;

    /// <summary>"Tripped at {time} · {value}" while <see cref="IsTripped"/>, empty otherwise.</summary>
    [ObservableProperty]
    private string _trippedSummary = string.Empty;

    [RelayCommand]
    private void Edit() => _onEdit(SensorName);

    [RelayCommand]
    private void CopyTo() => _onCopyTo(SensorName);

    [RelayCommand]
    private void Remove() => _onRemove(SensorName);

    /// <summary>Applies (or clears, passing null) the tracker's current trip snapshot for this sensor.</summary>
    public void ApplyTrip(TrippedAlert? trip)
    {
        IsTripped = trip is not null;
        TrippedSummary = trip is null
            ? string.Empty
            : string.Format(
                LocalizationService.Instance["Settings_Alerts_TrippedAt"],
                trip.At.LocalDateTime.ToString("t", LocalizationService.Instance.Culture),
                SensorReadingFormat.FormatReading(trip.Value, _unit));
    }
}
