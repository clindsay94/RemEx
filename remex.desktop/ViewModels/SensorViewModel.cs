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

    /// <summary>
    /// Accent hex for the DualMetric second series. Falls back to the theme's Tertiary role
    /// (<c>PaletteTertiary</c>) rather than a hardcoded amber — the same literal reused here would be
    /// the fix from RemEx-qljv surviving one layer up, since this binding wins over SparklineControl's
    /// own theme-derived Style default whenever it resolves to a value (Bindings always do).
    /// </summary>
    public string SecondaryAccentHex => SecondarySensor?.Theme.AccentColor
        ?? ThemeResources.Color("PaletteTertiary", DefaultSecondaryAccent).ToString();

    private static readonly Avalonia.Media.Color DefaultSecondaryAccent = Avalonia.Media.Color.Parse("#FFB020");

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

    // ── What was last announced, so a tick only announces what moved (RemEx-atgvl) ──
    private double? _lastMaxSeenValue;
    private GraphType? _lastResolvedGraphType;
    private bool? _lastIsDualMetric;

    /// <summary>
    /// Raises the gauge-bound properties, but only the ones whose value actually changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THIS RAN ONCE A SECOND PER SENSOR AND ANNOUNCED FOUR PROPERTIES, USUALLY NONE OF WHICH HAD
    /// MOVED.** Telemetry ticks at 1 Hz and one view model is shared by every card for a sensor, so
    /// twenty sensors meant eighty binding invalidations a second to redeliver values already on
    /// screen. An earlier draft of this remark said "four times a second per card", which is wrong on
    /// both halves and disagreed with the arithmetic in this change's own tests.
    /// </para>
    /// <para>
    /// **<see cref="MinSeenValue"/> IS NOT HERE AT ALL, BECAUSE IT IS A CONSTANT.** Its entire body is
    /// <c>=&gt; 0</c>, deliberately — the remark on it explains that a gauge must not rescale to its
    /// own observed floor, or a CPU sitting steadily at 19% reads as full. So that raise could never,
    /// in the life of the app, have delivered a different number. Bindings read it once when they
    /// attach and that is the only time it can matter.
    /// </para>
    /// <para>
    /// Compared against the last announced value rather than reasoned about per property. MaxSeenValue
    /// is 100 for any percent or temperature metric and otherwise moves only on a new peak;
    /// ResolvedGraphType changes when the user picks one, which a telemetry tick does not do. Both are
    /// true today and comparing does not depend on their staying true.
    /// </para>
    /// </remarks>
    private void RaiseGaugePropertiesThatActuallyChanged()
    {
        // NULLABLE RATHER THAN A NaN SENTINEL. MaxSeenValue provably cannot be NaN today - Update
        // compares with `>`, which is false for NaN, so _maxSeen never takes one - but that argument
        // lives in a different method, and "safe because of something over there" is the shape that
        // stops being true quietly. Null means "nothing announced yet" and cannot collide with a value.
        var maxSeen = MaxSeenValue;
        if (_lastMaxSeenValue != maxSeen)
        {
            _lastMaxSeenValue = maxSeen;
            OnPropertyChanged(nameof(MaxSeenValue));
        }

        var graphType = ResolvedGraphType;
        if (_lastResolvedGraphType != graphType)
        {
            _lastResolvedGraphType = graphType;
            OnPropertyChanged(nameof(ResolvedGraphType));
        }

        // DERIVED FROM THE LOCAL, NOT RE-READ. IsDualMetric is `ResolvedGraphType == DualMetric` by
        // definition, so reading the property would resolve the graph type a second time - avoidable
        // per-tick work added by a method whose whole purpose is to remove some.
        var dual = graphType == GraphType.DualMetric;
        if (_lastIsDualMetric != dual)
        {
            _lastIsDualMetric = dual;
            OnPropertyChanged(nameof(IsDualMetric));
        }
    }

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
    ///
    /// **NOT CURRENTLY BOUND BY ANYTHING.** This said it was used as AutomationProperties.HelpText on
    /// sensor cards; no .axaml in the repo has one. Kept because it is a reasonable description for a
    /// card to expose and the sparkline cards genuinely lack one - but it is not announced on change,
    /// so a binding added later must restore the raises in OnValueChanged/OnUnitChanged (RemEx-atgvl).
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
    // HistorySummary IS NOT RAISED, BECAUSE NOTHING READS IT (RemEx-atgvl). Its own doc said it was
    // used as AutomationProperties.HelpText on sensor cards; there is no AutomationProperties.HelpText
    // in any .axaml, and repo-wide the only mentions of the property are its declaration and the
    // raises that used to be here. So this was the same per-tick waste this bead exists to remove,
    // three lines below it. The property is left in place rather than deleted - it is a reasonable
    // accessible description and someone may yet bind it - but it is no longer announced to nobody.
    // If it acquires a binding, restore these two raises with it.

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

        RaiseGaugePropertiesThatActuallyChanged();

        // The ONLY call site, which makes threshold evaluation a side effect of the per-sensor
        // update work rather than something independent of it. That coupling is easy to break by
        // accident: the NAIVE ways of narrowing the telemetry tick — skip sensors with no placed
        // card, sample the tick while the window is hidden — stop evaluating alerts for whatever
        // they skip, and a hidden window is where alerts matter most.
        //
        // NOT a claim that the tick cannot be narrowed safely. Splitting this method into an
        // evaluate half (Value, RawReading, CheckAlert — always) and a present half (History plus
        // the gauge notifications — only when something is on screen) would keep alerts at full
        // rate. That was measured and judged not worth writing: the whole tick is ~0.14 ms at 250
        // sensors. See RemEx-4q6l; DashboardTickCostTests pins the coupling from the outside.
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
