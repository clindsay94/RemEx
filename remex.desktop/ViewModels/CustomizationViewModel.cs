using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;
using Remex.Desktop.Models;
using Remex.Core.Models;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Remex.Desktop.ViewModels;

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

    /// <summary>
    /// The light/dark choice carried into the next save. Null means "never chosen explicitly", which
    /// <see cref="ThemeService.ApplyCustomization"/> still answers by looking at the preset name.
    /// </summary>
    /// <remarks>
    /// SELECTING A PRESET IS CHOOSING ITS LIGHT/DARK, so SelectTheme writes this rather than leaving
    /// the name-matching fallback to infer it. The fallback exists for settings saved before the
    /// field did; once a user has picked anything, changing the seed must not drag the mode back to
    /// whatever the preset happens to be called. There is no UI for the switch yet - RemEx-5u0vy
    /// owns that surface - so this is the only writer.
    /// </remarks>
    private bool? _useLightPalette;

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

    private async Task VibrateCardCornerRadius(double target)
    {
        _isSnapping = true;
        await Dispatcher.UIThread.InvokeAsync(() => CornerRadius = Math.Clamp(target + 1.2, 0, 32));
        await Task.Delay(32);
        await Dispatcher.UIThread.InvokeAsync(() => CornerRadius = Math.Clamp(target - 1.2, 0, 32));
        await Task.Delay(32);
        await Dispatcher.UIThread.InvokeAsync(() => CornerRadius = target);
        _isSnapping = false;
        ApplyAndSave();
    }

    private async Task VibrateRemoteCornerRadius(double target)
    {
        _isSnapping = true;
        await Dispatcher.UIThread.InvokeAsync(() => RemoteCardCornerRadius = Math.Clamp(target + 1.2, 0, 48));
        await Task.Delay(32);
        await Dispatcher.UIThread.InvokeAsync(() => RemoteCardCornerRadius = Math.Clamp(target - 1.2, 0, 48));
        await Task.Delay(32);
        await Dispatcher.UIThread.InvokeAsync(() => RemoteCardCornerRadius = target);
        _isSnapping = false;
        ApplyAndSave();
    }

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
        _syncWithHardware = settings.SyncWithHardware;
        _useLightPalette = settings.UseLightPalette;
        _splashStyle = settings.SplashStyle;
        _selectedPageTitleFont = AvailableFonts.FirstOrDefault(f => f.Value == settings.PageTitleFontFamily)
                                 ?? AvailableFonts.FirstOrDefault();
        _selectedBodyFont = AvailableFonts.FirstOrDefault(f => f.Value == settings.BodyFontFamily)
                            ?? AvailableFonts.FirstOrDefault();
        _uiScale = settings.UiScale <= 0 ? 1.0 : Math.Clamp(settings.UiScale, 0.85, 1.3);

        // Load saved custom accent colours
        var profile = _layoutService.CurrentProfile;
        var colors = profile.Customization.CustomAccentColors ?? Array.Empty<string>();
        foreach (var hex in colors.Take(8))
            CustomAccentColors.Add(hex);

        // Load available background types
        RefreshBackgroundTypes();

        // Surface any persisted font that no longer resolves on this machine.
        ValidateFonts();
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

    [ObservableProperty]
    private bool _syncWithHardware;

    [ObservableProperty]
    private string _splashStyle;

    partial void OnSplashStyleChanged(string value) => ApplyAndSave();

    public ObservableCollection<string> AvailableSplashStyles { get; } = new()
    {
        "RemexCommand", "CosmicZoom", "Pong"
    };

    /// <summary>Available header fonts (bundled display fonts + installed system fonts).</summary>
    public ObservableCollection<FontOption> AvailableFonts { get; } = new(SystemFontService.GetHeaderFonts());

    /// <summary>The selected page-title font; persisted as its <see cref="FontOption.Value"/>.</summary>
    [ObservableProperty]
    private FontOption? _selectedPageTitleFont;

    partial void OnSelectedPageTitleFontChanged(FontOption? value)
    {
        ValidateFonts();
        ApplyAndSave();
    }

    /// <summary>The selected content/body font; persisted as its <see cref="FontOption.Value"/>.</summary>
    [ObservableProperty]
    private FontOption? _selectedBodyFont;

    partial void OnSelectedBodyFontChanged(FontOption? value)
    {
        ValidateFonts();
        ApplyAndSave();
    }

    /// <summary>
    /// Non-null when a chosen font can't be loaded on this machine — shown as a gentle notice in the
    /// panel. The app doesn't break (ThemeService falls back to the default), this just explains why.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFontWarning))]
    private string? _fontWarning;

    /// <summary>True when <see cref="FontWarning"/> is set — drives the notice's visibility.</summary>
    public bool HasFontWarning => !string.IsNullOrEmpty(FontWarning);

    /// <summary>Overall UI scale (85%–130%). Applied live as a layout transform over the whole shell.</summary>
    [ObservableProperty]
    private double _uiScale = 1.0;

    partial void OnUiScaleChanged(double value) => ApplyAndSave();

    /// <summary>Flags any selected font that won't materialize, so the user gets a plain-English heads-up.</summary>
    private void ValidateFonts()
    {
        var unavailable = new System.Collections.Generic.List<string>();
        if (SelectedPageTitleFont is { } title && !SystemFontService.TryResolveFont(title.Value, out _))
            unavailable.Add(title.DisplayName);
        if (SelectedBodyFont is { } body && !SystemFontService.TryResolveFont(body.Value, out _))
            unavailable.Add(body.DisplayName);

        FontWarning = unavailable.Count == 0
            ? null
            : string.Format(LocalizationService.Instance["Custom_FontUnavailable"], string.Join(", ", unavailable));
    }

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

    partial void OnSyncWithHardwareChanged(bool value) => ApplyAndSave();

    public bool IsGlassModeSelected => CanvasBackgroundType == "Glass";

    private void ApplyAndSave()
    {
        if (_isApplyingPreset) return;

        // EVERY FIELD THIS SCREEN DOES NOT OWN HAS TO BE CARRIED FORWARD BY HAND. Building a fresh
        // record and assigning only what the sliders bind to silently resets the rest to their
        // defaults on the next save — which is why ThemeContrast has been persisted, reloaded and
        // then wiped on the first customization change since it was added, and why "the contrast
        // setting does nothing" (RemEx-68ynp) was true in two independent places at once.
        // CustomizationSettingsRoundTripTests fails if a new field is added and not listed here.
        var carried = _layoutService.CurrentProfile.Customization;

        var settings = new CustomizationSettings
        {
            ThemeId = SelectedTheme.ToString(),
            ThemeContrast = carried.ThemeContrast,
            ThemeSeedChroma = carried.ThemeSeedChroma,
            UseLightPalette = _useLightPalette,
            CornerRadius = CornerRadius,
            RemoteCardCornerRadius = RemoteCardCornerRadius,
            GlassOpacity = GlassOpacity,
            AppWindowOpacity = AppWindowOpacity,
            GlowStrength = GlowStrength,
            AccentColor = AccentColor,
            SchemeVariant = SchemeVariant,
            BackgroundMaterial = CanvasBackgroundType,
            SyncWithHardware = SyncWithHardware,
            SplashStyle = SplashStyle,
            PageTitleFontFamily = SelectedPageTitleFont?.Value ?? "avares://Remex.Desktop/Assets/Fonts#Orbitron",
            CardHeaderFontFamily = carried.CardHeaderFontFamily,
            BodyFontFamily = SelectedBodyFont?.Value ?? "avares://Avalonia.Fonts.Inter/Assets#Inter",
            UiScale = UiScale,
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
                        _useLightPalette = false;
                        break;
                    case AppTheme.SolarFlare:
                        CornerRadius = 24;
                        RemoteCardCornerRadius = 48;
                        AccentColor = "#FFB800";
                        GlowStrength = 2;
                        GlassOpacity = 0.8;
                        _useLightPalette = true;
                        break;
                    case AppTheme.Monolith:
                        CornerRadius = 8;
                        RemoteCardCornerRadius = 12;
                        AccentColor = "#0A84FF";
                        GlowStrength = 0;
                        GlassOpacity = 1.0;
                        _useLightPalette = false;
                        break;
                    case AppTheme.BaseDarkGlass:
                        CornerRadius = 16;
                        RemoteCardCornerRadius = 24;
                        AccentColor = "#6C4CFF";
                        GlowStrength = 2;
                        GlassOpacity = 0.1;
                        SplashStyle = "RemexCommand";
                        _useLightPalette = false;
                        break;
                    case AppTheme.Dynamic:
                        CornerRadius = 24;
                        RemoteCardCornerRadius = 24;
                        GlowStrength = 4;
                        GlassOpacity = 0.4;
                        // Keep existing AccentColor as the seed, and the existing light/dark choice
                        // with it — Dynamic is "whatever the user has built", so it is the one preset
                        // that must not overwrite _useLightPalette.
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
