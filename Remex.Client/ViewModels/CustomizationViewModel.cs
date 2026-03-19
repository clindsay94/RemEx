using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Client.Models;
using Remex.Core.Models;
using System;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Customization page.
/// Provides theming, layout presets, and visual customization options.
/// </summary>
public partial class CustomizationViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DashboardLayoutService _layoutService;
    private readonly ThemeService _themeService;

    public CustomizationViewModel(ShellViewModel shell, DashboardLayoutService layoutService, ThemeService themeService)
    {
        _shell = shell;
        _layoutService = layoutService;
        _themeService = themeService;

        // Initialize from current profile
        var settings = _layoutService.CurrentProfile.Customization;
        _selectedTheme = Enum.TryParse<AppTheme>(settings.BaseTheme, true, out var theme) ? theme : AppTheme.BaseDarkGlass;
        _cornerRadius = settings.CornerRadius;
        _glassOpacity = settings.GlassOpacity;
        _glowStrength = settings.GlowStrength;
        _accentColor = settings.AccentColor;
        _canvasBackgroundType = settings.CanvasBackgroundType;
    }

    [ObservableProperty]
    private AppTheme _selectedTheme;

    [ObservableProperty]
    private double _cornerRadius;

    [ObservableProperty]
    private double _glassOpacity;

    [ObservableProperty]
    private double _glowStrength;

    [ObservableProperty]
    private string _accentColor;

    [ObservableProperty]
    private string _canvasBackgroundType;

    partial void OnSelectedThemeChanged(AppTheme value) => ApplyAndSave();
    partial void OnCornerRadiusChanged(double value) => ApplyAndSave();
    partial void OnGlassOpacityChanged(double value) => ApplyAndSave();
    partial void OnGlowStrengthChanged(double value) => ApplyAndSave();
    partial void OnAccentColorChanged(string value) => ApplyAndSave();
    partial void OnCanvasBackgroundTypeChanged(string value) => ApplyAndSave();

    private void ApplyAndSave()
    {
        var settings = new CustomizationSettings
        {
            BaseTheme = SelectedTheme.ToString(),
            CornerRadius = CornerRadius,
            GlassOpacity = GlassOpacity,
            GlowStrength = GlowStrength,
            AccentColor = AccentColor,
            CanvasBackgroundType = CanvasBackgroundType
        };

        // Update the current profile object
        var profile = _layoutService.CurrentProfile with { Customization = settings };
        
        // Use the internal setter if possible, or request a save
        _themeService.ApplyCustomization(settings);
        _layoutService.RequestSave(profile);
    }

    [RelayCommand]
    private void SelectTheme(string themeName)
    {
        if (Enum.TryParse<AppTheme>(themeName, true, out var theme))
        {
            SelectedTheme = theme;
            
            // Apply preset defaults based on PRD
            switch (theme)
            {
                case AppTheme.CyberNOC:
                    CornerRadius = 2;
                    AccentColor = "#00F3FF";
                    GlowStrength = 10;
                    GlassOpacity = 0.05;
                    break;
                case AppTheme.SolarFlare:
                    CornerRadius = 24;
                    AccentColor = "#FFB800";
                    GlowStrength = 2;
                    GlassOpacity = 0.8;
                    break;
                case AppTheme.Monolith:
                    CornerRadius = 8;
                    AccentColor = "#0A84FF";
                    GlowStrength = 0;
                    GlassOpacity = 1.0;
                    break;
                case AppTheme.BaseDarkGlass:
                    CornerRadius = 16;
                    AccentColor = "#6C4CFF";
                    GlowStrength = 2;
                    GlassOpacity = 0.1;
                    break;
            }
        }
    }

    [RelayCommand]
    private void SetAccent(string hex) => AccentColor = hex;

    [RelayCommand]
    private void ResetToDefault() => SelectTheme("BaseDarkGlass");

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();
}
