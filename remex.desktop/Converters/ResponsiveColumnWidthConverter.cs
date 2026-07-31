using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Converts an available content width into the width of a single card column so that the
/// non-feature screens (About, Settings, …) fill the available space with two balanced columns
/// when wide and collapse to a single full-width column when narrow.
///
/// Bind a column's <c>Width</c> to the containing panel's <c>Bounds.Width</c> through this
/// converter. Above <see cref="Breakpoint"/> it returns <c>(width - ColumnGap) / 2</c> (two
/// columns); below it, the full width (one column, so the second column wraps beneath the first).
/// This replaces the old fixed <c>MinWidth</c>/<c>MaxWidth</c> columns that left empty gutters.
/// </summary>
public sealed class ResponsiveColumnWidthConverter : IValueConverter
{
    public static readonly ResponsiveColumnWidthConverter Instance = new();

    /// <summary>Below this available width the layout collapses to a single full-width column.</summary>
    public double Breakpoint { get; set; } = 900;

    /// <summary>Horizontal gap between the two columns; keep in sync with the left column's right margin.</summary>
    public double ColumnGap { get; set; } = 24;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0)
            return double.NaN; // width not yet known — let the panel auto-size until layout settles

        if (width < Breakpoint)
            return width; // one column, full width (the second column wraps beneath)

        return Math.Floor((width - ColumnGap) / 2); // two filling columns with a gap between
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
