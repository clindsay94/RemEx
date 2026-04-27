using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Client.Models;
using Remex.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Customization page.
/// Provides theming, layout presets, and visual customization options.
/// </summary>
public partial class CustomizationViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly DashboardLayoutService _layoutService;
    private readonly ThemeService _themeService;
    private bool _isApplyingPreset;

    // ═══ Slider snap ═══
    private static readonly double[] CardSnapPoints = [0, 2, 8, 16, 24, 32];
    private static readonly double[] RemoteSnapPoints = [0, 4, 12, 24, 48];
    private const double SnapThreshold = 1.5;
    private bool _isSnapping;
    private double? _lastCardSnap;
    private double? _lastRemoteSnap;

    private static double? FindSnap(double value, double[] points)
    {
        foreach (var p in points)
            if (Math.Abs(value - p) <= SnapThreshold) return p;
        return null;
    }

    // TODO(human): Implement VibrateCardCornerRadius(double target) and VibrateRemoteCornerRadius(double target)
    // Each should briefly jitter the appropriate property around 'target' to give a tactile snap feel,
    // then settle at 'target' and call ApplyAndSave(). Use _isSnapping to guard against recursion.

    public ObservableCollection<string> AvailableBackgroundTypes { get; } = new();

    public ObservableCollection<string> AvailableSchemeVariants { get; } = new()
    {
        "TonalSpot", "Vibrant", "Expressive", "Rainbow", "FruitSalad", "Content", "Spritz"
    };

    /// <summary>User-saved custom accent colours shown after the built-in swatches.</summary>
    public ObservableCollection<string> CustomAccentColors { get; } = new();

    /// <summary>Controls the visibility of the hex input flyout for custom accent entry.</summary>
    [ObservableProperty]
    private bool _isCustomAccentPickerOpen;

    /// <summary>Hex value being typed by the user in the custom accent flyout.</summary>
    [ObservableProperty]
    private string _customAccentHex = string.Empty;

    [RelayCommand]
    private void OpenCustomAccentPicker() => IsCustomAccentPickerOpen = true;

    [RelayCommand]
    private void CloseCustomAccentPicker()
    {
        IsCustomAccentPickerOpen = false;
        CustomAccentHex = string.Empty;
    }

    [RelayCommand]
    private void ConfirmCustomAccent()
    {
        var hex = CustomAccentHex.Trim();
        if (!hex.StartsWith('#')) hex = '#' + hex;
        if (hex.Length is not (7 or 9)) return; // must be #RRGGBB or #AARRGGBB

        AccentColor = hex;

        if (!CustomAccentColors.Contains(hex))
        {
            CustomAccentColors.Add(hex);

            // Persist custom colours in the profile (up to 8)
            var saved = CustomAccentColors.Take(8).ToList();
            var profile = _layoutService.CurrentProfile;
            var updated = profile with
            {
                Customization = profile.Customization with { CustomAccentColors = saved }
            };
            _layoutService.RequestSave(updated);
        }

        IsCustomAccentPickerOpen = false;
        CustomAccentHex = string.Empty;
    }

    public CustomizationViewModel(ShellViewModel shell, DashboardLayoutService layoutService, ThemeService themeService)
    {
        _shell = shell;
        _layoutService = layoutService;
        _themeService = themeService;

        // Initialize from current profile
        var settings = _layoutService.CurrentProfile.Customization;
        _selectedTheme = Enum.TryParse<AppTheme>(settings.ThemeId, true, out var theme) ? theme : AppTheme.BaseDarkGlass;
        _cornerRadius = settings.CornerRadius;
        _remoteCardCornerRadius = settings.RemoteCardCornerRadius;
        _glassOpacity = settings.GlassOpacity;
        _appWindowOpacity = settings.AppWindowOpacity;
        _glowStrength = settings.GlowStrength;
        _accentColor = settings.AccentColor;
        _schemeVariant = settings.SchemeVariant;
        _canvasBackgroundType = settings.BackgroundMaterial;

        // Load saved custom accent colours
        var profile = _layoutService.CurrentProfile;
        var colors = profile.Customization.CustomAccentColors ?? Array.Empty<string>();
        foreach (var hex in colors.Take(8))
            CustomAccentColors.Add(hex);

        // Load available background types
        RefreshBackgroundTypes();
    }

    private void RefreshBackgroundTypes()
    {
        AvailableBackgroundTypes.Clear();
        if (OperatingSystem.IsWindows())
        {
            AvailableBackgroundTypes.Add("Mica");
            AvailableBackgroundTypes.Add("Acrylic");
        }
        else if (OperatingSystem.IsLinux())
        {
            AvailableBackgroundTypes.Add("Glass"); // Linux Mica-like alternative
        }
        AvailableBackgroundTypes.Add("Gradient");
        AvailableBackgroundTypes.Add("Wallpaper");
        AvailableBackgroundTypes.Add("Solid");

        // Fallback if current type is not available (e.g. switching from Windows to Linux)
        if (!AvailableBackgroundTypes.Contains(CanvasBackgroundType))
        {
            CanvasBackgroundType = OperatingSystem.IsLinux() ? "Glass" : "Gradient";
        }
    }

    [ObservableProperty]
    private AppTheme _selectedTheme;

    /// <summary>String representation of <see cref="SelectedTheme"/> used for Classes.selected bindings in AXAML.</summary>
    public string SelectedThemePreset => SelectedTheme.ToString();

    [ObservableProperty]
    private double _cornerRadius;

    [ObservableProperty]
    private double _remoteCardCornerRadius;

    [ObservableProperty]
    private double _glassOpacity;

    [ObservableProperty]
    private double _appWindowOpacity;

    [ObservableProperty]
    private double _glowStrength;

    [ObservableProperty]
    private string _accentColor;

    [ObservableProperty]
    private string _schemeVariant;

    [ObservableProperty]
    private string _canvasBackgroundType;

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        OnPropertyChanged(nameof(SelectedThemePreset));
        ApplyAndSave();
    }
    partial void OnCornerRadiusChanged(double value)
    {
        if (_isSnapping || _isApplyingPreset) { ApplyAndSave(); return; }
        var snap = FindSnap(value, CardSnapPoints);
        if (snap.HasValue)
        {
            bool isNewSnap = snap != _lastCardSnap;
            _lastCardSnap = snap;
            if (Math.Abs(value - snap.Value) > 0.01)
            {
                if (isNewSnap) _ = VibrateCardCornerRadius(snap.Value);
                else { _isSnapping = true; CornerRadius = snap.Value; _isSnapping = false; ApplyAndSave(); }
                return;
            }
        }
        else { _lastCardSnap = null; }
        ApplyAndSave();
    }

    partial void OnRemoteCardCornerRadiusChanged(double value)
    {
        if (_isSnapping || _isApplyingPreset) { ApplyAndSave(); return; }
        var snap = FindSnap(value, RemoteSnapPoints);
        if (snap.HasValue)
        {
            bool isNewSnap = snap != _lastRemoteSnap;
            _lastRemoteSnap = snap;
            if (Math.Abs(value - snap.Value) > 0.01)
            {
                if (isNewSnap) _ = VibrateRemoteCornerRadius(snap.Value);
                else { _isSnapping = true; RemoteCardCornerRadius = snap.Value; _isSnapping = false; ApplyAndSave(); }
                return;
            }
        }
        else { _lastRemoteSnap = null; }
        ApplyAndSave();
    }
    partial void OnGlassOpacityChanged(double value) => ApplyAndSave();
    partial void OnAppWindowOpacityChanged(double value) => ApplyAndSave();
    partial void OnGlowStrengthChanged(double value) => ApplyAndSave();
    partial void OnAccentColorChanged(string value) => ApplyAndSave();
    partial void OnSchemeVariantChanged(string value) => ApplyAndSave();
    partial void OnCanvasBackgroundTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGlassModeSelected));
        ApplyAndSave();
    }

    public bool IsGlassModeSelected => CanvasBackgroundType == "Glass";

    private void ApplyAndSave()
    {
        if (_isApplyingPreset) return;

        var settings = new CustomizationSettings
        {
            ThemeId = SelectedTheme.ToString(),
            CornerRadius = CornerRadius,
            RemoteCardCornerRadius = RemoteCardCornerRadius,
            GlassOpacity = GlassOpacity,
            AppWindowOpacity = AppWindowOpacity,
            GlowStrength = GlowStrength,
            AccentColor = AccentColor,
            SchemeVariant = SchemeVariant,
            BackgroundMaterial = CanvasBackgroundType,
            CustomAccentColors = CustomAccentColors.Take(8).ToList()
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
            _isApplyingPreset = true;
            try
            {
                SelectedTheme = theme;

                // Apply preset defaults based on PRD
                switch (theme)
                {
                    case AppTheme.CyberNOC:
                        CornerRadius = 2;
                        RemoteCardCornerRadius = 4;
                        AccentColor = "#00F3FF";
                        GlowStrength = 10;
                        GlassOpacity = 0.05;
                        break;
                    case AppTheme.SolarFlare:
                        CornerRadius = 24;
                        RemoteCardCornerRadius = 48;
                        AccentColor = "#FFB800";
                        GlowStrength = 2;
                        GlassOpacity = 0.8;
                        break;
                    case AppTheme.Monolith:
                        CornerRadius = 8;
                        RemoteCardCornerRadius = 12;
                        AccentColor = "#0A84FF";
                        GlowStrength = 0;
                        GlassOpacity = 1.0;
                        break;
                    case AppTheme.BaseDarkGlass:
                        CornerRadius = 16;
                        RemoteCardCornerRadius = 24;
                        AccentColor = "#6C4CFF";
                        GlowStrength = 2;
                        GlassOpacity = 0.1;
                        break;
                    case AppTheme.Dynamic:
                        CornerRadius = 24;
                        RemoteCardCornerRadius = 24;
                        GlowStrength = 4;
                        GlassOpacity = 0.4;
                        // Keep existing AccentColor as the seed
                        break;
                }
            }
            finally
            {
                _isApplyingPreset = false;
            }

            ApplyAndSave();
        }
    }

    [RelayCommand]
    private void SetAccent(string hex) => AccentColor = hex;

    [RelayCommand]
    private void ResetToDefault() => SelectTheme("BaseDarkGlass");

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();

    public void Dispose()
    {
        // No resources to dispose currently, but implementing IDisposable for consistency
        // in the ViewModel disposal hierarchy
    }
}
