using Avalonia;
using Avalonia.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class TrayFlyoutWindow : Window
{
    public TrayFlyoutWindow()
    {
        InitializeComponent();
        
        Background = null;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent };
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        
        // Don't auto-hide on Deactivated yet, as it might conflict with the Tray menu showing.
        // We will rely on explicit Toggle from the TrayIcon.
    }

    // No hand-written InitializeComponent — see the note in ConfirmationDialog. This markup
    // names nothing, so the declaration was inert here; it is removed so the pattern cannot be
    // primed by a later edit that adds a named control (RemEx-wdqx).

    public void ShowAtTray()
    {
        var screen = Screens.Primary;
        if (screen != null)
        {
            // Position above the taskbar in the corner
            double x = screen.WorkingArea.Right - Width - 12;
            double y = screen.WorkingArea.Bottom - Height - 12;
            Position = new PixelPoint((int)(x * screen.Scaling), (int)(y * screen.Scaling));
        }
        
        // Show without taking focus
        Show();
    }

    private void OnOpenMainApp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
        App.BringMainWindowToFront();
    }

    private void OnCloseFlyout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
    }
}
