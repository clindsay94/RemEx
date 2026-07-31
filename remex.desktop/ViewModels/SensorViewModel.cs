using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

public partial class SensorViewModel : ObservableObject
{
    private const int MaxHistory = 30;

    // ═══════════════ Core sensor data ═══════════════

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// User-supplied title that overrides the raw hardware sensor name on the card.
    /// Null/blank = fall back to <see cref="Name"/>. Surfaced through <see cref="DisplayName"/>.
    /// </summary>
    [ObservableProperty]
    private string? _customTitle;

    /// <summary>The title shown on the card — the custom title if set, otherwise the sensor name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(CustomTitle) ? Name : CustomTitle!;

    /// <summary>
    /// Whether the numeric value/unit overlay is drawn on the card. When false the card shows only
    /// its title over the ambient sparkline. Defaults to true (the classic look).
    /// </summary>
    [ObservableProperty]
    private bool _showValueOverlay = true;

    /// <summary>True while the card title is being edited inline (swaps the label for a text box).</summary>
    [ObservableProperty]
    private bool _isEditingTitle;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _unit = string.Empty;

    [ObservableProperty]
    private string _category = "Other";

    /// <summary>
    /// Rolling window of normalized values (0.0–1.0 fraction) for the sparkline.
    /// The SparklineControl scales these to the actual bounds height at render time.
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

    // ═══════════════ Alert ═══════════════

    [ObservableProperty]
    private bool _isAlertActive;

    private SensorAlert? _alert;

    /// <summary>Configured threshold alert for this sensor (null = none).</summary>
    public SensorAlert? Alert
    {
        get => _alert;
        set => _alert = value;
    }

    /// <summary>Raised when the sensor value crosses its configured threshold.</summary>
    public event Action<SensorAlert>? AlertTriggered;

    // ═══════════════ Graph Type ═══════════════

    [ObservableProperty]
    private GraphType _selectedGraphType = GraphType.Auto;

    /// <summary>
    /// Persisted id (sensor name) of a second metric overlaid on this card when the
    /// chosen view is <see cref="GraphType.DualMetric"/>. Null = no secondary bound.
    /// </summary>
    [ObservableProperty]
    private string? _secondarySensorId;

    /// <summary>
    /// Resolved view-model for <see cref="SecondarySensorId"/>, wired by the dashboard.
    /// Drives the second series drawn by the DualMetric view. Not persisted directly.
    /// </summary>
    [ObservableProperty]
    private SensorViewModel? _secondarySensor;

    /// <summary>Second normalized history series for the DualMetric view (null when unbound).</summary>
    public ObservableCollection<double>? SecondaryHistory => SecondarySensor?.History;

    /// <summary>Accent hex for the DualMetric second series (falls back to a warm amber).</summary>
    public string SecondaryAccentHex => SecondarySensor?.Theme.AccentColor ?? "#FFB020";

    partial void OnSecondarySensorChanged(SensorViewModel? value)
    {
        OnPropertyChanged(nameof(SecondaryHistory));
        OnPropertyChanged(nameof(SecondaryAccentHex));
        OnPropertyChanged(nameof(HasSecondary));
    }

    /// <summary>
    /// The resolved graph type — if <see cref="SelectedGraphType"/> is Auto, this returns the best
    /// type based on the sensor's <see cref="MetricKind"/> (host-stamped), falling back to the
    /// unit string when the kind is Unknown (older hosts / unclassified sensors).
    /// </summary>
    public GraphType ResolvedGraphType => SelectedGraphType == GraphType.Auto
        ? ResolveGraphType(RawReading?.Kind ?? MetricKind.Unknown, Unit)
        : SelectedGraphType;

    /// <summary>
    /// Gauge/Ring/LED fill floor. Gauges read from a meaningful zero, NOT the narrow observed band —
    /// a CPU sitting steadily at 19% must read 19%, not "full" because its min/max both hovered near 19.
    /// </summary>
    public double MinSeenValue => 0;

    /// <summary>
    /// Gauge/Ring/LED fill ceiling on the metric's real scale: percentages fill 0–100, temperatures
    /// 0–100 °C, everything else 0–(peak seen). This is what makes the gauges represent the true value.
    /// </summary>
    public double MaxSeenValue =>
        IsPercentMetric ? 100 :
        IsTemperatureMetric ? 100 :
        (_maxSeen == double.MinValue ? 100 : Math.Max(_maxSeen, 1));

    private bool IsPercentMetric =>
        RawReading?.Kind is MetricKind.CpuLoad or MetricKind.GpuLoad or MetricKind.RamLoad
        || (!string.IsNullOrEmpty(Unit) && Unit.Contains('%'));

    private bool IsTemperatureMetric =>
        RawReading?.Kind is MetricKind.CpuTempC or MetricKind.GpuTempC or MetricKind.TempC
        || (!string.IsNullOrEmpty(Unit) && Unit.Contains("°C"));

    /// <summary>True when this card is showing the Dual Metric view — drives the on-card legend.</summary>
    public bool IsDualMetric => ResolvedGraphType == GraphType.DualMetric;

    /// <summary>True when a second metric is bound (the legend's second row is only shown then).</summary>
    public bool HasSecondary => SecondarySensor is not null;

    /// <summary>
    /// Short accessible description of the sensor's current reading, e.g. "CPU Temp: 72 °C".
    /// Used as AutomationProperties.HelpText on sensor cards.
    /// </summary>
    public string HistorySummary =>
        string.IsNullOrWhiteSpace(Unit)
            ? $"{Name}: {Value:F1}"
            : $"{Name}: {Value:F1} {Unit}";

    partial void OnSelectedGraphTypeChanged(GraphType value)
    {
        OnPropertyChanged(nameof(ResolvedGraphType));
        OnPropertyChanged(nameof(IsDualMetric));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(HistorySummary));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnCustomTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(HistorySummary));
    partial void OnUnitChanged(string value) => OnPropertyChanged(nameof(HistorySummary));

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

    /// <summary>Begins inline title editing — prefills the buffer with the current display name.</summary>
    [RelayCommand]
    private void BeginRename()
    {
        if (string.IsNullOrWhiteSpace(CustomTitle))
            CustomTitle = Name;
        IsEditingTitle = true;
    }

    /// <summary>Commits inline title editing; a blank title reverts to the raw sensor name.</summary>
    [RelayCommand]
    private void EndRename()
    {
        if (string.IsNullOrWhiteSpace(CustomTitle))
            CustomTitle = null;
        else
            CustomTitle = CustomTitle!.Trim();
        IsEditingTitle = false;
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
        Name = string.IsNullOrWhiteSpace(reading.Name) ? LocalizationService.Instance["Sensor_Unknown"] : reading.Name;
        Value = reading.Value;
        Unit = string.IsNullOrWhiteSpace(reading.Unit) ? "" : reading.Unit;
        Category = string.IsNullOrWhiteSpace(reading.Category) ? "Other" : reading.Category;
        RawReading = reading;

        // Track local min/max to normalize the sparkline 0–24px
        if (reading.Value < _minSeen) _minSeen = reading.Value;
        if (reading.Value > _maxSeen) _maxSeen = reading.Value;

        if (History.Count >= MaxHistory)
            History.RemoveAt(0);

        // Normalize to 0.0–1.0 fraction
        double range = _maxSeen - _minSeen;
        double normalized = 0.05; // Minimal baseline so it's visible
        if (range > 0)
        {
            normalized = (reading.Value - _minSeen) / range;
            if (normalized < 0.05) normalized = 0.05; // Floor so it's visible
        }

        History.Add(normalized);

        // Notify gauge-related properties
        OnPropertyChanged(nameof(MinSeenValue));
        OnPropertyChanged(nameof(MaxSeenValue));
        OnPropertyChanged(nameof(ResolvedGraphType));
        OnPropertyChanged(nameof(IsDualMetric));

        CheckAlert();
    }

    private void CheckAlert()
    {
        if (_alert is null)
        {
            IsAlertActive = false;
            return;
        }

        bool triggered = _alert.Direction == AlertDirection.Above
            ? Value > _alert.Threshold
            : Value < _alert.Threshold;

        if (triggered && !IsAlertActive)
        {
            IsAlertActive = true;
            AlertTriggered?.Invoke(_alert);
        }
        else if (!triggered)
        {
            IsAlertActive = false;
        }
    }

    // ═══════════════ Auto Resolution ═══════════════

    /// <summary>
    /// Picks the best view for a sensor from its host-stamped <see cref="MetricKind"/>. This is the
    /// PC mirror of Android's <c>bestDisplayModeFor</c> — loads read as rings, memory/throughput as
    /// value+spark, temperatures/voltage as lines, clock/power as filled areas. Unknown kinds defer
    /// to the legacy unit-string heuristic so older hosts keep working.
    /// </summary>
    private static GraphType ResolveGraphType(MetricKind kind, string unit) => kind switch
    {
        MetricKind.CpuLoad or MetricKind.GpuLoad or MetricKind.RamLoad => GraphType.Ring,
        MetricKind.RamUsedGb or MetricKind.RamTotalGb                  => GraphType.GlowArea,
        MetricKind.CpuTempC or MetricKind.GpuTempC or MetricKind.TempC => GraphType.Line,
        MetricKind.ClockMhz                                            => GraphType.Line,
        MetricKind.PowerW                                             => GraphType.GlowArea,
        MetricKind.FanRpm                                             => GraphType.Bar,
        MetricKind.NetThroughputMbps
            or MetricKind.NetDownMbps
            or MetricKind.NetUpMbps                                    => GraphType.GlowArea,
        MetricKind.VoltageV                                            => GraphType.Line,
        MetricKind.DiskRateMBs                                         => GraphType.GlowArea,
        _                                                             => ResolveGraphTypeFromUnit(unit),
    };

    private static GraphType ResolveGraphTypeFromUnit(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return GraphType.Bar;

        if (unit.Contains("°C") || unit.Contains("°F") || unit.Contains("V"))
            return GraphType.Line;

        if (unit.Contains('%'))
            return GraphType.Gauge;

        if (unit.Contains("MHz") || unit.Contains("GHz") || unit.Contains("W"))
            return GraphType.GlowArea;

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
