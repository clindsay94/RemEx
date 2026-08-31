using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Multi-value converter: subtracts the second bound height from the first, floored at 0.
/// </summary>
/// <remarks>
/// Built for <c>ShellView</c>'s settings side sheet (RemEx-zrlze): its <c>ScrollViewer</c> sits
/// inside Material.Styles' <c>SideSheet</c> template as a child of a vertical <c>StackPanel</c>,
/// which measures every child with infinite available height. Without an explicit, finite
/// <c>MaxHeight</c> the <c>ScrollViewer</c> sizes itself to its full content instead of scrolling,
/// and anything below the fold becomes unreachable. Subtracting the header's own live
/// <see cref="global::Avalonia.Visual.Bounds"/>.Height from the sheet's gives the real remaining
/// space rather than a hardcoded constant, which would be wrong the moment a translated header
/// subtitle wraps to a second line.
/// </remarks>
public sealed class SubtractHeightConverter : IMultiValueConverter
{
    public static readonly SubtractHeightConverter Instance = new();

    public object? Convert(IList<object?> values, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double total || values[1] is not double subtract)
        {
            // Bounds not measured yet (first layout pass) - 0 rather than a guess. Avalonia
            // re-evaluates the binding once real Bounds arrive, so this is transient, not final.
            return 0d;
        }

        var result = total - subtract;
        return result > 0 ? result : 0d;
    }
}
