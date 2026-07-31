using System.Globalization;
using Avalonia.Data.Converters;
using Remex.Desktop.Services;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps a raw identifier to its localized display label, using a resource-key prefix supplied as the
/// ConverterParameter — parameter <c>Custom_BgType_</c> and value <c>Mica</c> resolve
/// <c>Custom_BgType_Mica</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE PICKERS NEED THIS AT ALL: <c>SelectedItem</c> must keep binding the RAW identifier,
/// because that is what is written to and compared from the saved dashboard profile
/// (<c>MainWindow.axaml.cs</c> tests <c>== "Mica"</c>, <c>DynamicColorGenerator</c> switches on the
/// raw variant string). Localizing the bound value would silently reset every user's backdrop and
/// scheme. Only the shown text may change.
/// </para>
/// <para>
/// WHY IT IS A MULTI-VALUE CONVERTER: values[1] is the item, which is a <see cref="string"/> and
/// therefore raises no change notification — a single-value binding would render once and never
/// refresh, so switching language would leave these two pickers stale while the panel around them
/// re-rendered. The language picker sits a dozen lines above them, which makes that especially
/// visible. values[0] exists solely to subscribe to <see cref="LocalizationService"/>, which raises
/// PropertyChanged for all bindings on a culture change; its content is unused. This mirrors how
/// <see cref="LocalizedFormatConverter"/> is bound at <c>CanvasView.axaml:198</c>, where the first
/// binding is likewise a service-indexer lookup.
/// </para>
/// <para>
/// Falls back to the raw identifier when no label resource exists, so a newly added option shows
/// something usable. Note the check cannot be a null/empty test: the
/// <see cref="LocalizationService"/> indexer returns THE KEY ITSELF for a missing resource, so a
/// naive test would render <c>Custom_BgType_Foo</c> instead of <c>Foo</c>.
/// </para>
/// </remarks>
public sealed class PrefixedLabelConverter : IMultiValueConverter
{
    public static readonly PrefixedLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // values[0] is the culture tripwire and is deliberately ignored; values[1] is the identifier.
        var id = (values.Count > 1 ? values[1] as string : null) ?? string.Empty;
        var prefix = parameter as string;
        if (string.IsNullOrEmpty(prefix) || id.Length == 0)
            return id;

        var key = prefix + id;
        try
        {
            var label = LocalizationService.Instance[key];
            if (label.Length > 0 && !string.Equals(label, key, StringComparison.Ordinal))
                return label;
        }
        catch
        {
            // Fall through to the raw identifier rather than failing the whole ItemsSource render.
        }

        return id;
    }
}
