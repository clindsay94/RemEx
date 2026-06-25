namespace Remex.Desktop.Services;

/// <summary>
/// Platform service that toggles immersive/fullscreen mode
/// (hides system chrome such as status bar and navigation bar on mobile).
/// </summary>
public interface IImmersiveModeService
{
    void EnterImmersiveMode();
    void ExitImmersiveMode();
}
