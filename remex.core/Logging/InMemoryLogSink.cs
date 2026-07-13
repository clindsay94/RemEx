using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Remex.Core.Logging;

public record LogEntry(DateTime TimeStamp, LogLevel Level, string Category, string Message, Exception? Exception)
{
    public override string ToString()
    {
        var excText = Exception != null ? $"\n{Exception}" : string.Empty;
        var shortLevel = Level.ToString();
        if (shortLevel.Length > 3) shortLevel = shortLevel[..3];
        return $"[{TimeStamp:HH:mm:ss}] [{shortLevel.ToUpperInvariant()}] [{Category}] {Message}{excText}";
    }
}

public static class InMemoryLogSink
{
    private static readonly object LockObject = new object();
    private static readonly List<LogEntry> EntriesList = new List<LogEntry>();
    private const int MaxEntries = 3000;

    /// <summary>
    /// The capture floor: entries below this level are never stored. Kept low (Debug) so the
    /// diagnostics UI can filter the retained buffer live and non-destructively — raising the
    /// on-screen display level no longer discards anything already captured. Set to Trace for
    /// deep-dive capture, or higher to reduce retention overhead.
    /// </summary>
    public static LogLevel MinimumLogLevel { get; set; } = LogLevel.Debug;

    public static event Action<LogEntry>? LogAdded;

    public static void Append(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < MinimumLogLevel) return;

        var entry = new LogEntry(DateTime.Now, level, category, message, exception);
        lock (LockObject)
        {
            EntriesList.Add(entry);
            if (EntriesList.Count > MaxEntries)
            {
                EntriesList.RemoveAt(0);
            }
        }
        LogAdded?.Invoke(entry);
    }

    public static List<LogEntry> GetEntries()
    {
        lock (LockObject)
        {
            return new List<LogEntry>(EntriesList);
        }
    }

    public static void Clear()
    {
        lock (LockObject)
        {
            EntriesList.Clear();
        }
    }
}

public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName);
    public void Dispose() { }
}

public sealed class InMemoryLogger : ILogger
{
    private readonly string _category;

    public InMemoryLogger(string category)
    {
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= InMemoryLogSink.MinimumLogLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        if (formatter == null) return;

        var message = formatter(state, exception);
        InMemoryLogSink.Append(logLevel, _category, message, exception);
    }
}
