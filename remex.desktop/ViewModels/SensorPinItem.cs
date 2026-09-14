using CommunityToolkit.Mvvm.ComponentModel;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Represents a sensor that can be pinned/unpinned to Home from the Layout section
/// (RemEx-4kv0g.4.2 — moved out of <c>SettingsViewModel</c> unchanged).
/// </summary>
public partial class SensorPinItem : ObservableObject
{
    public string SensorName { get; }

    /// <summary>Telemetry data source: "HWInfo", "WindowsPerf", "Linux", or "Unknown".</summary>
    public string Source { get; }

    [ObservableProperty]
    private bool _isPinned;

    public event System.EventHandler<bool>? PinChanged;

    public SensorPinItem(string sensorName, bool isPinned, string source = "Unknown")
    {
        SensorName = sensorName;
        _isPinned = isPinned;
        Source = source;
    }

    partial void OnIsPinnedChanged(bool value) => PinChanged?.Invoke(this, value);
}
