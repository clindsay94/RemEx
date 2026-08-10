namespace Remex.Core.Services;

public interface IInputSimulationService
{
    void MoveMouse(int x, int y);
    void MouseMoveRelative(int dx, int dy);
    void MouseDown(int button);
    void MouseUp(int button);
    void MouseClick(int button);
    void MouseScroll(int deltaX, int deltaY);
    void KeyDown(int keyCode);
    void KeyUp(int keyCode);
    void TypeText(string text);

    /// <summary>
    /// Gets the current cursor position.
    /// Returns (0, 0) if unavailable.
    /// </summary>
    (int X, int Y) GetCursorPosition();

    /// <summary>
    /// Confines the OS cursor to the given rectangle in virtual-desktop coordinates, so the pointer
    /// cannot leave the streamed display. The caller should re-apply this periodically because the OS
    /// releases the clip on display/desktop/foreground changes. No-op on platforms without a
    /// cursor-clipping concept.
    /// </summary>
    void ConfineCursorToRegion(int left, int top, int width, int height) { }

    /// <summary>
    /// Releases any cursor confinement previously established by <see cref="ConfineCursorToRegion"/>.
    /// </summary>
    void ReleaseCursorConfinement() { }

    /// <summary>
    /// Human-readable name of the active input backend for diagnostics and DesktopMeta.
    /// Returns null when not available.
    /// </summary>
    string? BackendName => null;

    /// <summary>
    /// Non-null once this service is accepting input events and DISCARDING them, with the reason
    /// (RemEx-iaxc). Null while input is being delivered, or before anything has been attempted.
    /// </summary>
    /// <remarks>
    /// **A RUNTIME SIGNAL BECAUSE NO STARTUP FLAG CAN CARRY IT.** Backends whose availability is
    /// decided before any input arrives report it through <c>SupportsInputSimulation</c> instead, and
    /// should leave this null. This exists for the case where the answer changes AFTER the host has
    /// already advertised the capability truthfully — on Wayland the injector is started lazily on the
    /// first event so the permission dialog appears only when a remote session really begins sending
    /// input, and a declined dialog cannot be predicted at startup.
    ///
    /// Read after dispatching an event, not before: the whole point is that the failure is only
    /// discoverable by having tried. Defaults to null so a backend that cannot fail this way needs no
    /// implementation.
    /// </remarks>
    string? InputSilentlyDroppedReason => null;
}
