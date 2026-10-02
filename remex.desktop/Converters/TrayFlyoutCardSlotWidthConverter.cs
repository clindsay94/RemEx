using System.Globalization;
using Avalonia.Data.Converters;
using Remex.Desktop.Services;

namespace Remex.Desktop.Converters;

/// <summary>
/// Turns the tray flyout's cards panel width into the width of one card slot, so the sensor cards
/// scale up with the flyout and fill each row (RemEx-8tm8l). Bind the cards <c>WrapPanel</c>'s
/// <c>ItemWidth</c> to the cards <c>ItemsControl</c>'s <c>Bounds.Width</c> through this.
/// </summary>
/// <remarks>
/// A thin wrapper: the arithmetic is <see cref="TrayFlyoutGeometry.CardSlotWidth"/>, next to the
/// column-fit constants the flyout's default and maximum widths are already pinned against, so the
/// cards have one sizing model rather than a second copy in a converter.
/// </remarks>
public sealed class TrayFlyoutCardSlotWidthConverter : IValueConverter
{
    public static readonly TrayFlyoutCardSlotWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => TrayFlyoutGeometry.CardSlotWidth(value is double width ? width : double.NaN);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
