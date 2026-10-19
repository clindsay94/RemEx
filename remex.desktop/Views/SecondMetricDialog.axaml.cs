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
    private readonly string? _current;

    public SecondMetricDialog()
    {
        InitializeComponent();
    }

    public SecondMetricDialog(string cardTitle, IEnumerable<string> candidates, string? current,
        Action<string?> onResult) : this()
    {
        _onResult = onResult;
        _current = current;
        _all.AddRange(candidates);
        foreach (var c in _all) _filtered.Add(c);

        CardTitleText.Text = cardTitle;
        SensorList.ItemsSource = _filtered;
        if (current is not null) SensorList.SelectedItem = current;

        SearchBox.TextChanged += (_, _) => Filter();
        SearchBox.KeyDown += OnSearchKeyDown;
        SensorList.SelectionChanged += (_, _) => UpdateButtons();
        SensorList.DoubleTapped += (_, _) => OnSet(null, new RoutedEventArgs());

        // Nothing to clear when the card has no second metric yet.
        ClearButton.IsEnabled = current is not null;
        UpdateButtons();
    }

    /// <summary>Set needs a highlighted sensor; with none it would only ever mean "clear".</summary>
    private void UpdateButtons() => SetButton.IsEnabled = SensorList.SelectedItem is string;

    private void Filter()
    {
        var q = SearchBox.Text?.Trim() ?? string.Empty;
        var previous = SensorList.SelectedItem as string;
        _filtered.Clear();
        foreach (var s in _all)
        {
            if (q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase))
                _filtered.Add(s);
        }

        SensorList.SelectedItem = SecondMetricSelection.AfterFilter(previous, _filtered);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SearchBox.Focus();
        if (SensorList.SelectedItem is { } selected)
            SensorList.ScrollIntoView(selected);
    }

    private void OnSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Down || _filtered.Count == 0)
            return;

        SensorList.SelectedItem ??= _filtered[0];
        var target = SensorList.SelectedItem is { } item ? SensorList.ContainerFromItem(item) : null;
        (target ?? SensorList).Focus();
        e.Handled = true;
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


    private void OnSet(object? sender, RoutedEventArgs e)
    {
        if (SensorList.SelectedItem is string name)
            Apply(name);
    }
    private void OnClear(object? sender, RoutedEventArgs e) => Apply(null);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>The list highlight after the search text changes.</summary>
internal static class SecondMetricSelection
{
    /// <summary>Keeps the previous pick if it survived the filter, else the first match, else nothing.</summary>
    internal static string? AfterFilter(string? previous, IReadOnlyList<string> filtered)
    {
        if (previous is not null && filtered.Contains(previous))
            return previous;

        return filtered.Count > 0 ? filtered[0] : null;
    }
}
