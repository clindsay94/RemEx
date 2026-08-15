using Avalonia;
using Avalonia.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class TrayFlyoutWindow : Window
{
    public TrayFlyoutWindow()
    {
        InitializeComponent();

        // TRANSPARENT ONLY — NOT MICA, NOT BLUR (RemEx-zu09j). DWM composites a Mica or acrylic
        // backdrop across the WHOLE window rect, including the margin this window leaves around its
        // rounded card for the drop shadow. The result was an opaque square sitting behind rounded
        // corners: the sharp grey rectangle. The frosted look now comes from GlassBaseDarkBrush on
        // the inner Border, which clips to its own CornerRadius and is a per-theme token, so all
        // four themes keep their own surface colour.
        //
        // These are set HERE AND ONLY HERE. The same three properties used to be declared on the
        // Window element as well, with a different list, and the code-behind silently won because it
        // runs after InitializeComponent - so the markup read as though Mica were a fallback when it
        // was actually first choice.
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;

        // Not in the constructor: the platform handle does not exist until the window is opened.
        Opened += (_, _) => TrayWindowCorners.ApplyRounded(this);

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
            // ONE COPY OF THE ARITHMETIC (RemEx-q7ak). This subtracted a LOGICAL window size from a
            // PHYSICAL screen edge and scaled the result, which lands correctly at 100% and drifts
            // further off the corner the higher the scaling goes. The balloon, written later against
            // the same corner, already did it the right way round — so there were two copies and one
            // of them was wrong.
            Position = Remex.Desktop.Services.TrayPlacement.BottomRight(
                screen.WorkingArea, Width, Height, screen.Scaling, marginLogical: 12);
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
