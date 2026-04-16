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
}
