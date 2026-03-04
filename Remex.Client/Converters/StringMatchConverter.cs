using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Client.Converters;

/// <summary>
/// Converts a string to a boolean by checking against known card type identifiers.
/// Used in DataTemplates to conditionally show/hide card content based on CardType.
/// </summary>
public static class StringMatchConverter
{
    /// <summary>Returns true when the bound string equals "Connection".</summary>
    public static readonly IValueConverter IsConnection =
        new StringEqualsConverter("Connection");

    /// <summary>Returns true when the bound string equals "Actions".</summary>
    public static readonly IValueConverter IsActions =
        new StringEqualsConverter("Actions");

    /// <summary>Returns true when the bound string equals "Latency".</summary>
    public static readonly IValueConverter IsLatency =
        new StringEqualsConverter("Latency");

    /// <summary>Returns true when the bound string equals "Sensor".</summary>
    public static readonly IValueConverter IsSensor =
        new StringEqualsConverter("Sensor");

    private sealed class StringEqualsConverter : IValueConverter
    {
        private readonly string _target;

        public StringEqualsConverter(string target) => _target = target;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string s && string.Equals(s, _target, StringComparison.Ordinal);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
