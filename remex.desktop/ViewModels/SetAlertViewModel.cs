using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>ViewModel for the "Set Alert" threshold configuration dialog.</summary>
public partial class SetAlertViewModel : ObservableObject
{
    private readonly Action<SensorAlert?> _close;

    public string SensorName { get; }

    /// <summary>The sensor's friendly name for the header, or <see cref="SensorName"/> when none was given.</summary>
    public string HeaderName { get; }

    /// <summary>The sensor's unit, shown after the threshold box. Null or empty shows nothing.</summary>
    public string? Unit { get; }

    /// <summary>The live reading with its unit, or null when the sensor has not reported.</summary>
    public string? CurrentReadingText { get; }

    /// <summary>True when editing an alert that already exists (only then is there something to clear).</summary>
    public bool HasExisting { get; }

    [ObservableProperty]
    private double _threshold;

    [ObservableProperty]
    private AlertDirection _direction = AlertDirection.Above;

    [ObservableProperty]
    private AlertSeverity _severity = AlertSeverity.Warning;

    public IReadOnlyList<AlertDirection> Directions { get; } =
        (AlertDirection[])Enum.GetValues(typeof(AlertDirection));

    public IReadOnlyList<AlertSeverity> Severities { get; } =
        (AlertSeverity[])Enum.GetValues(typeof(AlertSeverity));

    /// <param name="sensorName">Sensor this alert is for.</param>
    /// <param name="existing">Pre-populate from an existing alert (edit mode).</param>
    /// <param name="close">Called with the result; null means cancel, non-null means save.</param>
    /// <param name="displayName">Friendly name for the header (falls back to the raw sensor name).</param>
    /// <param name="unit">Unit shown beside the threshold and the live reading.</param>
    /// <param name="currentValue">Live reading; a new alert starts its threshold here.</param>
    public SetAlertViewModel(string sensorName, SensorAlert? existing, Action<SensorAlert?> close,
        string? displayName = null, string? unit = null, double? currentValue = null)
    {
        SensorName = sensorName;
        HeaderName = string.IsNullOrWhiteSpace(displayName) ? sensorName : displayName;
        Unit = unit;
        HasExisting = existing != null;
        _close = close;

        if (currentValue is { } live)
            CurrentReadingText = SensorReadingFormat.FormatReading(live, unit);

        if (existing != null)
        {
            _threshold = existing.Threshold;
            _direction = existing.Direction;
            _severity  = existing.Severity;
        }
        else if (currentValue is { } start)
        {
            _threshold = Math.Round(start, MidpointRounding.AwayFromZero);
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        _close(new SensorAlert
        {
            SensorName = SensorName,
            Threshold  = Threshold,
            Direction  = Direction,
            Severity   = Severity,
        });
    }

    [RelayCommand]
    private void Cancel() => _close(null);

    /// <summary>Removes the alert entirely (passes back a sentinel null via the close action).</summary>
    [RelayCommand]
    private void ClearAlert()
    {
        // Use a removed-marker: pass a SensorAlert with an empty SensorName
        _close(new SensorAlert { SensorName = string.Empty });
    }
}
