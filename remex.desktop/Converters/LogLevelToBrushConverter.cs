using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Microsoft.Extensions.Logging;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps a <see cref="LogLevel"/> to a severity colour for the diagnostic log list. Colours are
/// fixed (not theme brushes) because the log panel always sits on a dark glass background and the
/// severity semantics — amber warnings, red errors — should read identically across all themes.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public static readonly LogLevelToBrushConverter Instance = new();

    private static readonly IBrush TraceBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush DebugBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F43F5E"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Trace => TraceBrush,
        LogLevel.Debug => DebugBrush,
        LogLevel.Information => InfoBrush,
        LogLevel.Warning => WarnBrush,
        LogLevel.Error or LogLevel.Critical => ErrorBrush,
        _ => InfoBrush,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
