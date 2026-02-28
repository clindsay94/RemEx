using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Core.Messages;
using System.Collections.ObjectModel;

namespace Remex.Client.ViewModels;

public partial class SensorViewModel : ObservableObject
{
    private const int MaxHistory = 30;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _unit = string.Empty;

    [ObservableProperty]
    private string _category = "Other";

    /// <summary>
    /// Rolling window of normalized values (0-1) for a minimalist sparkline.
    /// To do a simple sparkline with an ItemsControl binding to Height.
    /// </summary>
    public ObservableCollection<double> History { get; } = new();

    private double _minSeen = double.MaxValue;
    private double _maxSeen = double.MinValue;

    public SensorReading? RawReading { get; private set; }

    public void Update(SensorReading reading)
    {
        Name = string.IsNullOrWhiteSpace(reading.Name) ? "Unknown" : reading.Name;
        Value = reading.Value;
        Unit = string.IsNullOrWhiteSpace(reading.Unit) ? "" : reading.Unit;
        Category = string.IsNullOrWhiteSpace(reading.Category) ? "Other" : reading.Category;
        RawReading = reading;

        // Track local min/max to normalize the sparkline 0-1
        if (reading.Value < _minSeen) _minSeen = reading.Value;
        if (reading.Value > _maxSeen) _maxSeen = reading.Value;

        if (History.Count >= MaxHistory)
            History.RemoveAt(0);

        // Normalize logic for a 0-24 pixel height block
        double range = _maxSeen - _minSeen;
        double normalized = 2.0; // Minimal baseline pixel height
        if (range > 0)
        {
            normalized = ((reading.Value - _minSeen) / range) * 24.0;
            if (normalized < 2) normalized = 2; // Floor to 2px so it's visible
        }

        History.Add(normalized);
    }
}

public class SensorGroupViewModel
{
    public string CategoryName { get; init; } = "Other";
    public ObservableCollection<SensorViewModel> Sensors { get; } = new();
}
