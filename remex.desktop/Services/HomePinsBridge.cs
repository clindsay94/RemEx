using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services.Home;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services;

/// <summary>
/// Joins the PC Home's pinned sensors to the host's <see cref="IHomePinnedSensorsStore"/>, so a phone
/// sees the same list and can change it (RemEx-wqo7a.5). Built by <c>ShellViewModel</c> when the
/// embedded host is running; with no host there is no store and no bridge.
/// </summary>
/// <remarks>
/// <para>
/// THE PC IS THE ONLY OWNER. What gets published is <see cref="DashboardLayoutService.CurrentProfile"/>'s
/// <c>PinnedSensorIds</c>, and a phone's request is applied through the canvas card
/// (<see cref="CanvasDashboardViewModel.ApplyHomePinChangeFromPeer"/>), never by writing the profile,
/// because the next canvas save treats the live cards as the truth and would undo a profile-only edit.
/// Nothing but the name lists ever crosses: no cards, no positions, no themes.
/// </para>
/// <para>
/// WHAT CAN BE PINNED is the sensor cards placed on the canvas, the same set the Layout checklist
/// offers. Sensor cards come back lazily as telemetry arrives, so the canvas's card list is watched
/// as well as the profile's save and replace events, and a burst of arrivals in one tick is coalesced
/// into one publish.
/// </para>
/// <para>
/// EVERY PHONE REQUEST IS ANSWERED BY A PUBLISH, accepted or refused, straight after it is applied
/// rather than after the debounced save. The store broadcasts the first publish after a request even
/// when nothing changed, which is how a refused request puts the phone's optimistic toggle back.
/// </para>
/// </remarks>
public sealed class HomePinsBridge : IDisposable
{
    private readonly IHomePinnedSensorsStore _store;
    private readonly DashboardLayoutService _layoutService;
    private readonly CanvasDashboardViewModel _canvas;
    private readonly Action<Action> _post;
    private readonly ILogger<HomePinsBridge>? _logger;
    private int _publishScheduled;
    private bool _disposed;

    /// <param name="post">
    /// How work reaches the UI thread. Production always queues on <see cref="Dispatcher.UIThread"/>,
    /// even from the UI thread, which is what lets a burst of card arrivals collapse into one publish.
    /// Tests pass a synchronous invoker, because this assembly's tests have no dispatcher to pump.
    /// </param>
    public HomePinsBridge(
        IHomePinnedSensorsStore store,
        DashboardLayoutService layoutService,
        CanvasDashboardViewModel canvas,
        Action<Action>? post = null,
        ILogger<HomePinsBridge>? logger = null)
    {
        _store = Guard.NotNull(store);
        _layoutService = Guard.NotNull(layoutService);
        _canvas = Guard.NotNull(canvas);
        _post = post ?? (static work => Dispatcher.UIThread.Post(work));
        _logger = logger;

        _layoutService.ProfileSaved += OnProfileChanged;
        _layoutService.ProfileReplaced += OnProfileChanged;
        _canvas.Cards.CollectionChanged += OnCardsChanged;
        _store.PhoneChangeRequested += OnPhoneChangeRequested;

        RequestPublish();
    }

    private void OnProfileChanged() => RequestPublish();

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A reorder changes neither list (pinnable is published sorted), so it is not worth a publish.
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        RequestPublish();
    }

    private void OnPhoneChangeRequested(HomePinChange change, string clientId) =>
        _post(() => ApplyFromPhone(change, clientId));

    private void ApplyFromPhone(HomePinChange change, string clientId)
    {
        if (_disposed) return;

        if (!_canvas.ApplyHomePinChangeFromPeer(change.SensorName, change.Pinned))
        {
            _logger?.LogInformation(
                "A phone ({ClientId}) asked to {Action} \"{Sensor}\", which has no card on the dashboard; keeping the PC's list.",
                clientId, change.Pinned ? "pin" : "unpin", change.SensorName);
        }

        PublishNow();
    }

    private void RequestPublish()
    {
        if (Interlocked.Exchange(ref _publishScheduled, 1) == 1) return;
        _post(PublishNow);
    }

    private void PublishNow()
    {
        Interlocked.Exchange(ref _publishScheduled, 0);
        if (_disposed) return;

        var pinned = _layoutService.CurrentProfile?.PinnedSensorIds ?? [];
        var pinnable = _canvas.Cards
            .Where(c => c.CardType == "Sensor" && c.Sensor is not null && !string.IsNullOrWhiteSpace(c.Sensor.Name))
            .Select(c => c.Sensor!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _store.PublishFromPc([.. pinned], pinnable);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _layoutService.ProfileSaved -= OnProfileChanged;
        _layoutService.ProfileReplaced -= OnProfileChanged;
        _canvas.Cards.CollectionChanged -= OnCardsChanged;
        _store.PhoneChangeRequested -= OnPhoneChangeRequested;
    }
}
