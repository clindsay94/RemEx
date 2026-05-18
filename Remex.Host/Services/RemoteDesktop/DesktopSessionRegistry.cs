using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.RemoteDesktop;

/// <summary>
/// Singleton registry that enforces the invariant of at most one active
/// <c>StreamFramesAsync</c> loop per <c>clientId</c> at any moment in time.
///
/// When a new <c>/ws/desktop</c> connection arrives for a <c>clientId</c> that
/// already has an active loop, <see cref="TakeOverAsync"/> cancels the prior
/// <see cref="CancellationTokenSource"/> and awaits its drain signal (with a
/// configurable timeout) before returning the new one.
///
/// Thread safety: all public methods are safe to call from any thread.
/// </summary>
public sealed class DesktopSessionRegistry
{
    // Maps clientId → the CTS that is currently active for that client.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeSessions = new();

    // Maps CTS → its drain signal. Keyed by CTS reference so MarkDrained can fire
    // the signal for a superseded session even after it has been evicted from _activeSessions.
    private readonly ConditionalWeakTable<CancellationTokenSource, TaskCompletionSource> _drainSignals = new();

    private readonly ILogger<DesktopSessionRegistry> _logger;

    public DesktopSessionRegistry(ILogger<DesktopSessionRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a new session for <paramref name="clientId"/>, cancels any prior
    /// session for the same key, and awaits its drain (bounded by <paramref name="drainTimeout"/>).
    ///
    /// Returns a fresh <see cref="CancellationTokenSource"/> linked to <paramref name="ct"/>.
    /// The caller is responsible for passing this CTS's <see cref="CancellationTokenSource.Token"/>
    /// to the handler loop and for calling <see cref="MarkDrained"/> in a <c>finally</c> block.
    /// </summary>
    public async Task<CancellationTokenSource> TakeOverAsync(
        string clientId,
        TimeSpan drainTimeout,
        CancellationToken ct)
    {
        // Empty clientId arises from loopback/in-process connections. Assign a unique
        // synthetic key so multiple concurrent loopback sessions don't cancel each other.
        var registryKey = string.IsNullOrEmpty(clientId)
            ? $"__loopback__:{Guid.NewGuid()}"
            : clientId;

        var newCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var newDrain = new TaskCompletionSource();
        _drainSignals.Add(newCts, newDrain);

        // Snapshot the old CTS, then atomically replace it.
        _activeSessions.TryGetValue(registryKey, out var priorCts);
        _activeSessions[registryKey] = newCts;

        if (priorCts is not null && !ReferenceEquals(priorCts, newCts))
        {
            _logger.LogInformation(
                "Taking over desktop session for clientId={ClientIdPrefix}; cancelling prior loop.",
                RedactKey(registryKey));

            try { await priorCts.CancelAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Prior CTS cancel threw."); }

            // Wait for the prior session's handler to call MarkDrained.
            if (_drainSignals.TryGetValue(priorCts, out var priorDrain))
            {
                var drainTask = priorDrain.Task;
                var timeoutTask = Task.Delay(drainTimeout, ct);
                var winner = await Task.WhenAny(drainTask, timeoutTask);

                if (!ReferenceEquals(winner, drainTask))
                {
                    _logger.LogWarning(
                        "Prior session for clientId={ClientIdPrefix} did not drain within {TimeoutMs}ms; proceeding anyway.",
                        RedactKey(registryKey),
                        (int)drainTimeout.TotalMilliseconds);
                }
            }

            // Dispose the superseded CTS now that we're done waiting.
            try { priorCts.Dispose(); }
            catch { /* best effort */ }
        }

        return newCts;
    }

    /// <summary>
    /// Signals that the session associated with <paramref name="ownedCts"/> has finished
    /// draining, and removes it from the active session registry.
    ///
    /// Must be called from the handler's <c>finally</c> block, passing the same
    /// <see cref="CancellationTokenSource"/> returned by <see cref="TakeOverAsync"/>.
    /// </summary>
    public void MarkDrained(string clientId, CancellationTokenSource ownedCts)
    {
        // Fire the drain signal so any concurrent TakeOverAsync can proceed.
        if (_drainSignals.TryGetValue(ownedCts, out var signal))
            signal.TrySetResult();

        // Remove from the active map only if this CTS is still the registered one.
        // A newer TakeOver may have already replaced it.
        var registryKey = string.IsNullOrEmpty(clientId)
            ? null
            : clientId;

        if (registryKey is not null &&
            _activeSessions.TryGetValue(registryKey, out var current) &&
            ReferenceEquals(current, ownedCts))
        {
            _activeSessions.TryRemove(registryKey, out _);
        }
        // For loopback sessions, the synthetic key is unknown at call-site, so we
        // skip the removal. The ConditionalWeakTable entry is GC'd when the CTS is
        // collected (after the caller disposes it in the using block).
    }

    private static string RedactKey(string key)
    {
        if (key.StartsWith("__loopback__:", StringComparison.Ordinal))
            return "<loopback>";
        return key.Length > 8 ? key[..8] + "..." : key;
    }
}
