using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Remex.Core.Exceptions;

/// <summary>
/// Centralized exception handling utilities for RemEx application.
/// Provides helpers for logging, filtering, and managing expected vs unexpected exceptions.
/// </summary>
public static class RemexExceptionHandlingExtensions
{
    /// <summary>
    /// Exception filter for logging exceptions without catching them.
    /// This allows you to log an exception while still letting it propagate.
    /// </summary>
    /// <param name="logger">Logger instance for recording the exception</param>
    /// <param name="ex">Exception to log</param>
    /// <returns>Always returns false, ensuring the exception is not caught</returns>
    /// <example>
    /// catch (SocketException ex) when (LogAndReturnFalse(logger, ex))
    /// {
    ///     // This block never executes - exception propagates to caller
    /// }
    /// </example>
    public static bool LogAndReturnFalse(ILogger logger, Exception ex)
    {
        logger.LogError(ex, "Exception occurred");
        return false; // Never actually catches, allows exception to propagate
    }

    /// <summary>
    /// Executes async operation with specific exception handling for expected network/I/O errors.
    /// Logs expected exceptions and returns null, while letting unexpected exceptions propagate.
    /// </summary>
    /// <typeparam name="T">Return type of the operation (must be reference type)</typeparam>
    /// <param name="operation">Async operation to execute</param>
    /// <param name="logger">Logger for recording exceptions</param>
    /// <param name="operationName">Descriptive name for logging context</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Result of operation, or null if an expected exception occurred</returns>
    public static async Task<T?> HandleExpectedExceptionsAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("{Operation} was cancelled", operationName);
            return null;
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "{Operation} failed due to network issue", operationName);
            return null;
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "{Operation} failed due to WebSocket issue", operationName);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "{Operation} failed due to I/O issue", operationName);
            return null;
        }
        // Let unexpected exceptions (OutOfMemoryException, NullReferenceException, etc.) propagate
    }

    /// <summary>
    /// Safe fire-and-forget task execution with top-level exception logging.
    /// Use this for background tasks that should not block the caller but need error tracking.
    /// </summary>
    /// <param name="task">Task to execute in the background</param>
    /// <param name="logger">Logger for recording unhandled exceptions</param>
    /// <param name="operationName">Descriptive name for logging context</param>
    /// <example>
    /// Task.Run(() => ProcessBackgroundWork())
    ///     .FireAndForget(_logger, "ProcessBackgroundWork");
    /// </example>
    public static void FireAndForget(this Task task, ILogger logger, string operationName)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                logger.LogError(t.Exception, "Unhandled exception in {Operation}", operationName);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Flattens an AggregateException and logs all inner exceptions individually.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="aggregateException">AggregateException containing multiple failures</param>
    /// <param name="operationName">Descriptive name for logging context</param>
    public static void LogAggregateException(ILogger logger, AggregateException aggregateException, string operationName)
    {
        foreach (var innerException in aggregateException.InnerExceptions)
        {
            logger.LogError(innerException, "{Operation} failed with exception: {ExceptionType}",
                operationName, innerException.GetType().Name);
        }
    }
}

/// <summary>
/// Exception thrown when network operations fail.
/// Use this for domain-specific network errors that are not covered by SocketException or WebSocketException.
/// </summary>
public class NetworkException : Exception
{
    public NetworkException(string message) : base(message) { }
    public NetworkException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exception thrown when telemetry collection or reporting fails.
/// Use this to distinguish telemetry failures from other system errors.
/// </summary>
public class TelemetryException : Exception
{
    public TelemetryException(string message) : base(message) { }
    public TelemetryException(string message, Exception inner) : base(message, inner) { }
}
