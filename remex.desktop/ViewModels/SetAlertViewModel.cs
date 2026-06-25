using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

/// <summary>ViewModel for the "Set Alert" threshold configuration dialog.</summary>
public partial class SetAlertViewModel : ObservableObject
{
    private readonly Action<SensorAlert?> _close;

    public string SensorName { get; }

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
    public SetAlertViewModel(string sensorName, SensorAlert? existing, Action<SensorAlert?> close)
    {
        SensorName = sensorName;
        _close = close;

        if (existing != null)
        {
            _threshold = existing.Threshold;
            _direction = existing.Direction;
            _severity  = existing.Severity;
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
