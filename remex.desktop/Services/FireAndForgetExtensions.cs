using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Remex.Desktop.Services;

/// <summary>
/// Runs a task without awaiting it, while still noticing if it fails.
/// </summary>
/// <remarks>
/// <c>_ = SomethingAsync()</c> is the usual way to start work you cannot await — from a timer
/// callback, a <c>ct.Register</c> handler, or a UI post. It also throws the result away, including
/// the exception: the task faults, nothing observes it, and the operation silently did not happen.
/// The cases this was introduced for all lose real state that way — an unsent cancel leaves the peer
/// streaming, an unsent layout update loses a dashboard edit, a failed flush loses the saved layout
/// (RemEx-ajk3).
/// <para>
/// Deliberately NOT a general "handle the error" mechanism. It observes and reports; it cannot
/// retry, and it cannot tell the user, because the call sites have no UI context by construction.
/// Anything that needs a user-visible outcome should be awaited somewhere that can show one.
/// </para>
/// <para>
/// No <c>ConfigureAwait(false)</c> anywhere here, per <c>docs/ASYNC_GUIDELINES.md</c> — neither
/// Avalonia nor ASP.NET Core installs a <c>SynchronizationContext</c>, so it would be noise.
/// </para>
/// </remarks>
public static class FireAndForgetExtensions
{
    /// <summary>
    /// Starts <paramref name="task"/> unawaited, logging a fault instead of dropping it.
    /// </summary>
    /// <param name="context">
    /// What was being attempted, in plain terms — this is the only clue a reader gets, since the
    /// stack trace of an unawaited task points at machinery rather than at intent.
    /// </param>
    /// <param name="logger">
    /// Where to report. Null falls back to <see cref="Debug"/>, which is a floor rather than a
    /// choice: it compiles out of Release builds. Several call sites have no logger available yet,
    /// and giving them one is tracked separately (RemEx-t4tc).
    /// </param>
    /// <remarks>
    /// Cancellation is not a fault and is not reported: a cancelled task is cancelled, not faulted,
    /// so <see cref="TaskContinuationOptions.OnlyOnFaulted"/> already excludes it. That matters
    /// because most of these run on paths that are cancelled routinely — a transfer aborted by the
    /// user should not log an error.
    /// </remarks>
    public static void FireAndForget(this Task task, string context, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        _ = task.ContinueWith(
            completed =>
            {
                // Flatten so an AggregateException from a nested task does not hide the real cause.
                var error = completed.Exception?.Flatten().InnerException ?? completed.Exception;
                if (error is null)
                    return;

                if (logger is not null)
                    logger.LogWarning(error, "Background operation failed: {Context}", context);
                else
                    Debug.WriteLine($"[FireAndForget] {context} failed: {error}");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
