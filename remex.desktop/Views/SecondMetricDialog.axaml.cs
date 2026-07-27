using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Remex.Desktop.Views;

/// <summary>
/// Modal picker for a card's DualMetric "second metric". Lists every other live sensor with a search
/// filter (there can be hundreds), so the user can overlay a second series or clear it. Replaces the
/// old flat context-menu submenu, which was both unreliable and unusable at that scale.
/// </summary>
public partial class SecondMetricDialog : Window
{
    private readonly Action<string?>? _onResult;
    private readonly List<string> _all = new();
    private readonly ObservableCollection<string> _filtered = new();

    public SecondMetricDialog()
    {
        InitializeComponent();
    }

    public SecondMetricDialog(string cardTitle, IEnumerable<string> candidates, string? current,
        Action<string?> onResult) : this()
    {
        _onResult = onResult;
        _all.AddRange(candidates);
        foreach (var c in _all) _filtered.Add(c);

        CardTitleText.Text = cardTitle;
        SensorList.ItemsSource = _filtered;
        if (current is not null) SensorList.SelectedItem = current;

        SearchBox.TextChanged += (_, _) => Filter();
        SensorList.DoubleTapped += (_, _) => Apply(SensorList.SelectedItem as string);
    }

    private void Filter()
    {
        var q = SearchBox.Text?.Trim() ?? string.Empty;
        _filtered.Clear();
        foreach (var s in _all)
        {
            if (q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase))
                _filtered.Add(s);
        }
    }

    private void Apply(string? name)
    {
        _onResult?.Invoke(name);
        Close();
    }

    private void OnSet(object? sender, RoutedEventArgs e) => Apply(SensorList.SelectedItem as string);
    private void OnClear(object? sender, RoutedEventArgs e) => Apply(null);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
