using System;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>
/// Tells the in-process host when the PC's own UI actually needs telemetry samples (perf audit P0-10).
/// </summary>
/// <remarks>
/// <para>
/// The host's sensor sampler now runs only while something holds a demand lease
/// (<see cref="ITelemetryBroadcaster.AcquireDemand"/>). Phones take theirs host-side; this is the
/// UI's. It holds ONE lease while ANY of three things is true, and none while all are false:
/// </para>
/// <list type="bullet">
/// <item><see cref="IsWindowVisible"/> - the main window is shown and not minimized. Deliberately the
/// window, not one page: Home, the canvas dashboard, the sensor pickers and the staging drawer all read
/// live telemetry, and a per-page list is one new page away from a view that silently freezes.</item>
/// <item><see cref="IsFlyoutShowingSensors"/> - the tray flyout is open with pinned sensor cards in it,
/// the one live readout that exists while the main window is hidden.</item>
/// <item><see cref="HasArmedAlerts"/> - at least one sensor alert is configured. Alerts are evaluated
/// on every sample by the dashboard view model whether or not anything is on screen, and an alert
/// that stops seeing samples when the window hides is an alert that never fires - the whole point
/// of one is to catch what nobody is watching.</item>
/// </list>
/// <para>
/// UI-thread affine like the stores it mirrors; the setters are driven from property and collection
/// change events on that thread.
/// </para>
/// </remarks>
public sealed class TelemetryDemandCoordinator : IDisposable
{
    private readonly DemandHold _hold;
    private bool _isWindowVisible;
    private bool _isFlyoutShowingSensors;
    private bool _hasArmedAlerts;

    /// <param name="acquire">Takes one lease on the host sampler; normally
    /// <see cref="ITelemetryBroadcaster.AcquireDemand"/>.</param>
    public TelemetryDemandCoordinator(Func<IDisposable?> acquire)
    {
        _hold = new DemandHold(acquire);
    }

    /// <summary>Whether a lease is currently held on the host sampler.</summary>
    public bool IsHoldingDemand => _hold.IsHeld;

    /// <summary>The main window is shown and not minimized.</summary>
    public bool IsWindowVisible
    {
        get => _isWindowVisible;
        set { _isWindowVisible = value; Update(); }
    }

    /// <summary>The tray flyout is open and has at least one pinned sensor card.</summary>
    public bool IsFlyoutShowingSensors
    {
        get => _isFlyoutShowingSensors;
        set { _isFlyoutShowingSensors = value; Update(); }
    }

    /// <summary>At least one sensor alert is configured.</summary>
    public bool HasArmedAlerts
    {
        get => _hasArmedAlerts;
        set { _hasArmedAlerts = value; Update(); }
    }

    private void Update() =>
        _hold.Set(_isWindowVisible || _isFlyoutShowingSensors || _hasArmedAlerts);

    /// <summary>Releases the lease, if held, and never takes another.</summary>
    public void Dispose() => _hold.Dispose();
}
