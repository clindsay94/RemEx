using System.Net.WebSockets;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Home;

namespace Remex.Agent.Services.Home;

/// <summary>
/// Keeps one authenticated phone connection told about the PC Home's pinned sensors (RemEx-wqo7a.5):
/// sends <c>home_pins_sync</c> with the store's current list when attached, and again every time the
/// store changes, until disposed.
/// </summary>
/// <remarks>
/// <para>
/// ONE IN FLIGHT, LATEST WINS. A burst of publishes (the canvas restoring a hundred sensor cards in
/// one telemetry tick) collapses to whatever is newest when the previous send finishes, so a slow
/// phone is never queued a backlog of lists it will throw away. Sends never go backwards: a snapshot
/// whose revision is not newer than the last one queued is dropped, which also covers the race where
/// a change lands between subscribing and reading <see cref="IHomePinnedSensorsStore.Current"/>.
/// </para>
/// <para>
/// NOTHING BEFORE THE FIRST PUBLISH. Revision 0 means the desktop has not published yet, and empty
/// lists sent then would read on the phone as "this PC has no pins and nothing to pin" for the second
/// or two before the real list arrives. The first real publish reaches this link through
/// <see cref="IHomePinnedSensorsStore.Changed"/> anyway.
/// </para>
/// <para>
/// A FAILED SEND ENDS THE LINK QUIETLY. It means the socket is going away, and the connection's own
/// receive loop is what reports that; logging it here as well would double every disconnect.
/// </para>
/// </remarks>
public sealed class HomePinsSessionLink : IDisposable
{
    private readonly IHomePinnedSensorsStore _store;
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly object _gate = new();
    private HomePinnedSensors? _pending;
    private long _lastQueuedRevision;
    private bool _pumping;
    private bool _disposed;   // no more sends: set by Dispose or by a failed send
    private bool _tornDown;   // Dispose has run
    private Task _pumpTask = Task.CompletedTask;

    public HomePinsSessionLink(
        IHomePinnedSensorsStore store, WebSocket socket, ILogger logger, CancellationToken connectionToken)
    {
        _store = Guard.NotNull(store);
        _socket = Guard.NotNull(socket);
        _logger = Guard.NotNull(logger);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
    }

    /// <summary>
    /// Subscribes to the store and sends its current list. The returned task completes when that
    /// first send has finished, so a caller that awaits it knows the phone has been told.
    /// </summary>
    public Task AttachAsync()
    {
        _store.Changed += OnChanged;
        return Offer(_store.Current);
    }

    private void OnChanged(HomePinnedSensors snapshot) => _ = Offer(snapshot);

    private Task Offer(HomePinnedSensors snapshot)
    {
        lock (_gate)
        {
            if (_disposed || snapshot.Revision <= _lastQueuedRevision)
            {
                return _pumpTask;
            }

            _lastQueuedRevision = snapshot.Revision;
            _pending = snapshot;
            if (_pumping)
            {
                return _pumpTask;
            }

            _pumping = true;
            _pumpTask = Task.Run(PumpAsync);
            return _pumpTask;
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            HomePinnedSensors next;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _pumping = false;
                    return;
                }

                next = _pending;
                _pending = null;
            }

            try
            {
                await MessageSerializer.SendAsync(
                    _socket,
                    new RemexMessage { Type = MessageTypes.HomePinsSync, HomePins = next },
                    _cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException
                                           or OperationCanceledException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Stopped sending pinned sensors: the connection is closing.");
                lock (_gate)
                {
                    _disposed = true;
                    _pending = null;
                    _pumping = false;
                }

                _store.Changed -= OnChanged;
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_tornDown)
            {
                return;
            }

            _tornDown = true;
            _disposed = true;
            _pending = null;
        }

        _store.Changed -= OnChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
