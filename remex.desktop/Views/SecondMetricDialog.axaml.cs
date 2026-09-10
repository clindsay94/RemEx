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

    /// <summary>
    /// Escape dismisses (RemEx-xxifk) and Enter applies the highlighted candidate (RemEx-df08), both
    /// routed to the same handlers the Cancel/Set buttons use.
    /// </summary>
    /// <remarks>
    /// An override rather than a KeyBinding because this dialog has no view model - Cancel/Set/Clear
    /// are code-behind handlers, and a KeyBinding can only reach a bound Command.
    /// FIX ROUND 1 (RemEx-df08): the first pass called <c>Apply(SensorList.SelectedItem as string)</c>
    /// unconditionally, on the reasoning that it mirrors "Set" including its null case. That reasoning
    /// missed that <see cref="Filter"/> rebuilds <c>_filtered</c> on every keystroke in
    /// <c>SearchBox</c> - typing a query that excludes the pre-selected item nulls out
    /// <c>SelectedItem</c>, so Enter silently produced Clear's outcome (closing the dialog and clearing
    /// the overlay) while the user was mid-search for a different sensor. Enter now requires
    /// <c>SelectedItem</c> to actually be a string before doing anything; with nothing selected it is a
    /// no-op rather than a reflex "Clear". SearchBox, the other focusable control, is single-line and
    /// does not consume Enter itself, so this still gets it.
    /// </remarks>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Avalonia.Input.Key.Enter && SensorList.SelectedItem is string sel)
        {
            e.Handled = true;
            Apply(sel);
            return;
        }

        base.OnKeyDown(e);
    }


    private void OnSet(object? sender, RoutedEventArgs e) => Apply(SensorList.SelectedItem as string);
    private void OnClear(object? sender, RoutedEventArgs e) => Apply(null);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
