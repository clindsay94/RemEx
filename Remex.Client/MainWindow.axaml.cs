using System;
using Avalonia;
using Avalonia.Controls;
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

        _themeService = App.Services.GetService<ThemeService>();
        if (_themeService is not null)
        {
            _themeService.CustomizationApplied += OnCustomizationApplied;

            if (App.Services.GetService<DashboardLayoutService>() is { CurrentProfile.Customization: { } settings })
            {
                OnCustomizationApplied(settings);
            }
        }
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
            if (settings.BackgroundMaterial == "Mica")
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
            }
            else if (settings.BackgroundMaterial == "Acrylic")
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
            }
            else
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            }
        });
    }
}