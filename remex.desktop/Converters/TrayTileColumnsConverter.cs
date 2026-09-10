using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Turns the tray flyout's available width into the number of columns its action grid should use.
/// </summary>
/// <remarks>
/// This is what makes the flyout worth resizing. Without it a wider window only stretches the same
/// tiles; with it, widening the window reveals more of the grid per row.
/// <para>
/// NEVER RETURNS ZERO. <c>UniformGrid.Columns = 0</c> puts every tile in one row running off the
/// edge of the window, and <c>Bounds.Width</c> is <c>NaN</c> until the first layout pass — so the
/// degenerate case is reached on every single show, not just in theory.
/// </para>
/// </remarks>
public sealed class TrayTileColumnsConverter : IValueConverter
{
    public static readonly TrayTileColumnsConverter Instance = new();

    private const double ThreeColumnWidth = 380;
    private const double FourColumnWidth = 520;
    private const int FallbackColumns = 2;

    public static int ColumnsFor(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return FallbackColumns;

        if (width < ThreeColumnWidth) return 2;
        if (width < FourColumnWidth) return 3;
        return 4;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width ? ColumnsFor(width) : FallbackColumns;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
