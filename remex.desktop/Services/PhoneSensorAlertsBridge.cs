using System;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services.Alerts;
using Remex.Core.Validation;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services;

/// <summary>
/// Joins the PC's sensor alerts to the host's <see cref="IPhoneSensorAlerts"/>, so paired phones hear
/// the PC's alerts and can edit the PC's rules (RemEx-pp4cm.12). Built by <c>ShellViewModel</c> when the
/// embedded host is running; with no host there is nothing to publish to and no bridge.
/// </summary>
/// <remarks>
/// <para>
/// **ONE SET OF RULES, ONE EVALUATOR.** A phone alert is sent from the same
/// <see cref="CanvasDashboardViewModel.SensorAlertFired"/> event that raises the PC's own toast, which
/// the canvas raises only when <see cref="SensorAlertTracker"/> says to notify. The tracker's 60 second
/// per-sensor cooldown therefore applies to phones for free, and nothing here compares a reading with a
/// threshold.
/// </para>
/// <para>
/// **A PHONE'S EDIT IS APPLIED THROUGH THE CANVAS**, <see cref="CanvasDashboardViewModel.ApplySensorAlert"/>,
/// which writes <see cref="SensorAlertStore"/> and so triggers the same re-apply and profile save a PC
/// edit does; the PC's Alerts list refreshes from the store's own event. Everything runs on the UI
/// thread, which <see cref="SensorAlertStore"/> requires.
/// </para>
/// <para>
/// **EVERY PHONE REQUEST IS ANSWERED BY PUBLISHING THE RULES**, accepted or refused, straight after it
/// is applied. A refused edit (an unknown sensor, a full list) therefore puts the phone's optimistic
/// change back. Rule changes by any other route (the PC's own dialog, an import) are published too, so
/// a connected phone never shows a rule list the PC no longer has.
/// </para>
/// </remarks>
public sealed class PhoneSensorAlertsBridge : IDisposable
{
    private readonly IPhoneSensorAlerts _alerts;
    private readonly SensorAlertStore _store;
    private readonly CanvasDashboardViewModel _canvas;
    private readonly Action<Action> _post;
    private readonly ILogger<PhoneSensorAlertsBridge>? _logger;
    private int _publishScheduled;
    private bool _applying;
    private bool _disposed;

    /// <param name="post">
    /// How work reaches the UI thread. Production always queues on <see cref="Dispatcher.UIThread"/>.
    /// Tests pass a synchronous invoker, because this assembly's tests have no dispatcher to pump.
    /// </param>
    public PhoneSensorAlertsBridge(
        IPhoneSensorAlerts alerts,
        SensorAlertStore store,
        CanvasDashboardViewModel canvas,
        Action<Action>? post = null,
        ILogger<PhoneSensorAlertsBridge>? logger = null)
    {
        _alerts = Guard.NotNull(alerts);
        _store = Guard.NotNull(store);
        _canvas = Guard.NotNull(canvas);
        _post = post ?? (static work => Dispatcher.UIThread.Post(work));
        _logger = logger;

        _canvas.SensorAlertFired += OnAlertFired;
        _store.Changed += OnStoreChanged;
        _alerts.PhoneRequested += OnPhoneRequested;
    }

    private void OnAlertFired(SensorAlert alert, double value)
    {
        if (_disposed) return;

        var resolved = _canvas.TryResolve(alert.SensorName, out var info) ? info : null;
        _alerts.PublishFiredAsync(new SensorAlertFiredEvent
        {
            SensorName = alert.SensorName,
            DisplayName = resolved?.DisplayName ?? alert.SensorName,
            Value = value,
            Unit = resolved?.Unit,
            Threshold = alert.Threshold,
            Direction = alert.Direction,
            Severity = alert.Severity,
            FiredAtUtc = DateTimeOffset.UtcNow,
        }).FireAndForget("sending a sensor alert to paired phones", _logger);
    }

    private void OnStoreChanged()
    {
        // A phone's own edit is answered once, by ApplyFromPhone; publishing here as well would send
        // every phone the same list twice.
        if (_applying) return;
        RequestPublish();
    }

    private void OnPhoneRequested(PhoneSensorAlertRequest request) => _post(() => ApplyFromPhone(request));

    private void ApplyFromPhone(PhoneSensorAlertRequest request)
    {
        if (_disposed) return;

        _applying = true;
        try
        {
            switch (request.Kind)
            {
                case PhoneSensorAlertRequestKind.Set:
                    ApplySet(request.Change);
                    break;
                case PhoneSensorAlertRequestKind.Remove:
                    ApplyRemove(request.SensorName);
                    break;
            }
        }
        finally
        {
            _applying = false;
        }

        PublishNow();
    }

    private void ApplySet(SensorAlertChange? change)
    {
        if (change is null) return;

        var known = _canvas.TryResolve(change.SensorName, out var info);
        var replaces = _store.TryGet(change.SensorName, out var existing);
        var verdict = SensorAlertValidation.Check(change, known, replaces, _store.All.Count);
        if (verdict != SensorAlertVerdict.Accepted)
        {
            _logger?.LogInformation("A phone asked for an alert on \"{Sensor}\" that the PC refused: {Verdict}.", change.SensorName, verdict);
            return;
        }

        // The PC's own spelling of the name, so the rule keys the same as a rule made on the PC.
        var name = existing?.SensorName ?? info?.Name ?? change.SensorName;
        _canvas.ApplySensorAlert(name, SensorAlertValidation.ToRule(change, name));
    }

    private void ApplyRemove(string? sensorName)
    {
        if (sensorName is null || !_store.TryGet(sensorName, out var existing)) return;
        _canvas.ApplySensorAlert(existing.SensorName, null);
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

        var rules = _store.All.Select(alert =>
        {
            var resolved = _canvas.TryResolve(alert.SensorName, out var info) ? info : null;
            return new SensorAlertRule
            {
                SensorName = alert.SensorName,
                DisplayName = resolved?.DisplayName ?? alert.SensorName,
                Unit = resolved?.Unit,
                CurrentValue = _canvas.TryGetLiveValue(alert.SensorName, out var live) ? live : null,
                Threshold = alert.Threshold,
                Direction = alert.Direction,
                Severity = alert.Severity,
            };
        }).ToList();

        _alerts.PublishRulesAsync(rules).FireAndForget("sending the sensor alert rules to paired phones", _logger);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _canvas.SensorAlertFired -= OnAlertFired;
        _store.Changed -= OnStoreChanged;
        _alerts.PhoneRequested -= OnPhoneRequested;
    }
}
