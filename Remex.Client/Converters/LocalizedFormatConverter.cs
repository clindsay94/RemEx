using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Client.Converters;

/// <summary>
/// Formats a localized template string with one or more arguments.
/// values[0] is the format template (e.g. "Sensors ({0})"); values[1..] are the args.
/// Use via a MultiBinding with the localized string first, then the value(s) to format in.
/// This replaces the invalid pattern of nesting a Localize binding inside StringFormat,
/// which throws at view construction (StringFormat is a plain string, not a binding target).
/// </summary>
public class LocalizedFormatConverter : IMultiValueConverter
{
    public static readonly LocalizedFormatConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string format)
            return string.Empty;

        var args = new object?[values.Count - 1];
        for (var i = 1; i < values.Count; i++)
            args[i - 1] = values[i];

        try
        {
            return string.Format(culture, format, args);
        }
        catch (FormatException)
        {
            // Template/arg mismatch — fall back to the raw template rather than crashing.
            return format;
        }
    }
}
