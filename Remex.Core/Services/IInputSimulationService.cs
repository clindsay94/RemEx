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
}
