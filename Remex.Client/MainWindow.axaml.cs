using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Remex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;

namespace Remex.Client;

public partial class MainWindow : Window
{
    private ThemeService? _themeService;

    public MainWindow()
    {
        InitializeComponent();

        _themeService = App.Services.GetService<ThemeService>(); // optional service
        if (_themeService is not null)
        {
            _themeService.CustomizationApplied += OnCustomizationApplied;

            if (App.Services.GetService<DashboardLayoutService>() is { CurrentProfile.Customization: { } settings }) // optional service
            {
                OnCustomizationApplied(settings);
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (App.IsShuttingDown)
        {
            base.OnClosing(e);
            return;
        }

        // Honor the user's chosen close behavior (Settings → General).
        var closeToTray = true;
        if (App.Services?.GetService<DashboardLayoutService>() is { CurrentProfile: { } profile }) // optional service
            closeToTray = profile.CloseToTray;

        if (closeToTray)
        {
            // Keep the app alive in the tray; the window just hides.
            // The user can exit via the tray menu or the in-app Exit button.
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
            return;
        }

        // User wants the X button to fully exit: end the process.
        e.Cancel = true;
        base.OnClosing(e);
        App.RequestApplicationShutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_themeService is not null)
            _themeService.CustomizationApplied -= OnCustomizationApplied;
        base.OnClosed(e);
    }

    private void OnCustomizationApplied(CustomizationSettings settings)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (OperatingSystem.IsWindows() && settings.BackgroundMaterial == "Mica")
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
                Background = Brushes.Transparent;
                Opacity = 1.0;
            }
            else if (OperatingSystem.IsWindows() && settings.BackgroundMaterial == "Acrylic")
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
                Background = Brushes.Transparent;
                Opacity = 1.0;
            }
            else if (settings.BackgroundMaterial == "Glass")
            {
                // Glass mode: request compositor-level transparency so the desktop is visible
                // behind the window. AppWindowOpacity further controls how much shows through.
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.Blur };
                Background = Brushes.Transparent;
                Opacity = Math.Clamp(settings.AppWindowOpacity, 0.1, 1.0);
            }
            else
            {
                // Gradient, Wallpaper, Solid, and all non-transparent modes.
                TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x10));
                Opacity = 1.0;
            }
        });
    }
}
