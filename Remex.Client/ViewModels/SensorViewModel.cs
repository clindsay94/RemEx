using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

public partial class SensorViewModel : ObservableObject
{
    private const int MaxHistory = 30;

    // ═══════════════ Core sensor data ═══════════════

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _unit = string.Empty;

    [ObservableProperty]
    private string _category = "Other";

    /// <summary>
    /// Rolling window of normalized values (0–24px) for the sparkline.
    /// </summary>
    public ObservableCollection<double> History { get; } = new();

    private double _minSeen = double.MaxValue;
    private double _maxSeen = double.MinValue;

    public SensorReading? RawReading { get; private set; }

    // ═══════════════ Card Sizing ═══════════════

    [ObservableProperty]
    private double _cardWidth = 130;

    [ObservableProperty]
    private double _cardHeight = 68;

    [ObservableProperty]
    private string _cardSizeLabel = "Small";

    // ═══════════════ Theme / Colors ═══════════════

    [ObservableProperty]
    private SensorCardTheme _theme = SensorCardTheme.Presets[0];

    // ═══════════════ Graph Type ═══════════════

    [ObservableProperty]
    private GraphType _selectedGraphType = GraphType.Auto;

    /// <summary>
    /// The resolved graph type — if <see cref="SelectedGraphType"/> is Auto,
    /// this returns the best type based on the sensor's unit.
    /// </summary>
    public GraphType ResolvedGraphType => SelectedGraphType == GraphType.Auto
        ? ResolveGraphTypeFromUnit(Unit)
        : SelectedGraphType;

    /// <summary>
    /// Exposed for the SparklineControl Gauge mode — the raw min ever seen.
    /// </summary>
    public double MinSeenValue => _minSeen == double.MaxValue ? 0 : _minSeen;

    /// <summary>
    /// Exposed for the SparklineControl Gauge mode — the raw max ever seen.
    /// </summary>
    public double MaxSeenValue => _maxSeen == double.MinValue ? 100 : _maxSeen;

    partial void OnSelectedGraphTypeChanged(GraphType value)
    {
        OnPropertyChanged(nameof(ResolvedGraphType));
    }

    // ═══════════════ Commands ═══════════════

    [RelayCommand]
    private void SetSize(string size)
    {
        switch (size)
        {
            case "Small":
                CardWidth = 130;
                CardHeight = 68;
                break;
            case "Medium":
                CardWidth = 200;
                CardHeight = 100;
                break;
            case "Large":
                CardWidth = 280;
                CardHeight = 140;
                break;
        }
        CardSizeLabel = size;
    }

    [RelayCommand]
    private void ApplyTheme(string themeName)
    {
        var preset = SensorCardTheme.Presets.FirstOrDefault(t => t.Name == themeName);
        if (preset != null)
            Theme = preset;
    }

    [RelayCommand]
    private void SetGraphType(string graphTypeName)
    {
        if (Enum.TryParse<GraphType>(graphTypeName, true, out var gt))
        {
            SelectedGraphType = gt;
        }
    }

    /// <summary>
    /// Sets a single element color on the theme, producing a "Custom" theme.
    /// </summary>
    public void SetElementColor(string element, string hex)
    {
        Theme = element switch
        {
            "Background" => Theme with { Name = "Custom", CardBackground = hex },
            "Accent" => Theme with { Name = "Custom", AccentColor = hex },
            "Value" => Theme with { Name = "Custom", ValueColor = hex },
            "Label" => Theme with { Name = "Custom", LabelColor = hex },
            "Unit" => Theme with { Name = "Custom", UnitColor = hex },
            _ => Theme,
        };
    }

    // ═══════════════ Update ═══════════════

    public void Update(SensorReading reading)
    {
        Name = string.IsNullOrWhiteSpace(reading.Name) ? "Unknown" : reading.Name;
        Value = reading.Value;
        Unit = string.IsNullOrWhiteSpace(reading.Unit) ? "" : reading.Unit;
        Category = string.IsNullOrWhiteSpace(reading.Category) ? "Other" : reading.Category;
        RawReading = reading;

        // Track local min/max to normalize the sparkline 0–24px
        if (reading.Value < _minSeen) _minSeen = reading.Value;
        if (reading.Value > _maxSeen) _maxSeen = reading.Value;

        if (History.Count >= MaxHistory)
            History.RemoveAt(0);

        // Normalize to 0–24 pixel height
        double range = _maxSeen - _minSeen;
        double normalized = 2.0; // Minimal baseline pixel height
        if (range > 0)
        {
            normalized = ((reading.Value - _minSeen) / range) * 24.0;
            if (normalized < 2) normalized = 2; // Floor to 2px so it's visible
        }

        History.Add(normalized);

        // Notify gauge-related properties
        OnPropertyChanged(nameof(MinSeenValue));
        OnPropertyChanged(nameof(MaxSeenValue));
        OnPropertyChanged(nameof(ResolvedGraphType));
    }

    // ═══════════════ Auto Resolution ═══════════════

    private static GraphType ResolveGraphTypeFromUnit(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return GraphType.Bar;

        if (unit.Contains("°C") || unit.Contains("°F") || unit.Contains("V"))
            return GraphType.Line;

        if (unit.Contains('%'))
            return GraphType.Gauge;

        if (unit.Contains("MHz") || unit.Contains("GHz") || unit.Contains("W"))
            return GraphType.Area;

        if (unit.Contains("RPM"))
            return GraphType.Bar;

        return GraphType.Bar;
    }
}

public class SensorGroupViewModel
{
    public string CategoryName { get; init; } = "Other";
    public ObservableCollection<SensorViewModel> Sensors { get; } = new();
}
