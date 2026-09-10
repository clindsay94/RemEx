using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Remex.Agent.Services.RemoteDesktop;

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
    /// Cancels the streaming loop for a client whose pairing has been revoked (RemEx-6nkht).
    /// Returns true when there was one to cancel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CANCEL, NOT ABORT, and that difference is the whole reason this lives here rather than being
    /// done to the socket from outside. <see cref="TakeOverAsync"/>'s contract is that a loop ends by
    /// its token and then calls <see cref="MarkDrained"/> in a <c>finally</c>; killing the socket
    /// underneath it would end the loop through an exception path instead, leaving the capture
    /// session to be torn down by whatever unwinding happened to run. Cancelling uses the drain the
    /// handler already implements.
    /// </para>
    /// <para>
    /// NOT REMOVED FROM <c>_activeSessions</c> HERE. The loop's own <see cref="MarkDrained"/> does
    /// that, and only when it is still the registered CTS — removing it here would let a reconnect
    /// racing the revocation register a fresh session that the draining old one then evicts.
    /// </para>
    /// <para>
    /// A revoked client cannot reconnect: <c>/ws/desktop</c> checks <c>IsClientPaired</c> before it
    /// reaches <see cref="TakeOverAsync"/>. This closes the window where it is already inside.
    /// </para>
    /// <para>
    /// <c>CancelAsync</c>, NOT <c>Cancel</c>, AND IT IS LOAD-BEARING RATHER THAN STYLISTIC. <c>Cancel</c>
    /// runs every token registration INLINE and does not return until they have all finished — so this
    /// method would not yield a Task until the streaming teardown was already complete, and
    /// <c>PairedDeviceDisconnector</c>'s drain budget would be applied to something that had by then
    /// finished. Swapping it back would silently un-bound that wait while leaving the code that bounds
    /// it in place and looking correct: a wedged capture teardown would hang the confirmation with a
    /// timeout sitting right there (review). It also matches <see cref="TakeOverAsync"/> sixty lines
    /// above, which uses it for the inline-callback reason.
    /// </para>
    /// </remarks>
    public async Task<bool> CancelSessionsForAsync(string clientId)
    {
        // A blank id is loopback, which is keyed synthetically and has no pairing to revoke.
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        if (!_activeSessions.TryGetValue(clientId, out var cts)) return false;

        _logger.LogInformation(
            "Cancelling desktop session for clientId={ClientIdPrefix}: pairing revoked.",
            RedactKey(clientId));

        // A handler that has finished can dispose its CTS between the lookup and here — the `using
        // var sessionCts` in the /ws/desktop delegate disposes AFTER its finally calls MarkDrained,
        // so there is a real window where the entry is gone but this reference is not.
        try { await cts.CancelAsync(); }
        catch (ObjectDisposedException) { return false; }

        return true;
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
