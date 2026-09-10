namespace Remex.Desktop.Themes.Chrome;

/// <summary>
/// Platform gate for the title-bar fullscreen button (RemEx-5253o). On Windows and Linux a
/// title-bar fullscreen affordance is not a platform convention, and Avalonia 12's only path out of
/// FullScreen — <c>WindowDrawnDecorationsContent.FullscreenPopover</c>'s hover-the-top-edge bar — is
/// the macOS convention, not a Windows/Linux one, so Connor found the button but no discoverable way
/// back. macOS keeps the button because the popover affordance IS the native convention there.
/// </summary>
/// <remarks>
/// Avalonia 12.1.1 has no public, settable way to suppress the button itself: the
/// <c>:has-fullscreen</c> pseudo-class WindowChrome.axaml already keys off
/// (<c>^:not(:has-fullscreen)</c>) is driven by <c>Window.AllowedWindowActions</c>
/// (<c>internal</c>, read-only, sourced from the platform impl — see
/// <c>Avalonia.Controls.Window</c> in AvaloniaUI/Avalonia release/12.1). There is no
/// <c>CanFullScreen</c>-shaped property to bind against, so this is a plain OS check consumed via
/// <c>x:Static</c> in the template rather than something threaded through MainWindow.
/// </remarks>
public static class WindowChromePlatform
{
    /// <summary>
    /// Whether the title-bar fullscreen button (and its FullscreenPopover twin) should be shown.
    /// WindowChrome.axaml declares this pair's Style BEFORE the existing
    /// <c>:not(:has-fullscreen)</c> style — Avalonia has no CSS specificity, so at equal Style
    /// priority the LAST-declared matching style wins, and the capability hide has to be able to
    /// win if the platform genuinely disallows fullscreen (this only narrows the platforms where a
    /// supported fullscreen is *also offered from the chrome*).
    /// </summary>
    public static bool ShowFullScreenButton { get; } = OperatingSystem.IsMacOS();
}
