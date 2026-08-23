using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;
using Remex.Desktop.Models;
using Remex.Core.Models;
using System.Collections.ObjectModel;
using Avalonia.Media;
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

    /// <summary>
    /// Whether <see cref="SelectTheme"/> has written <see cref="_useLightPalette"/> since this view
    /// model was constructed.
    /// </summary>
    /// <remarks>
    /// THE FIELD IS A SNAPSHOT AND THE VALUE IT REPLACED WAS LIVE. ApplyAndSave used to read the mode
    /// off <c>CurrentProfile.Customization</c> every time; a field read once in the constructor is not
    /// the same thing, and ShellViewModel caches this view model with <c>??=</c> and never rebuilds it.
    /// So importing a savefile from a light setup — which replaces the profile and repaints the app —
    /// and then nudging any slider would have written the stale constructor-time value back and
    /// silently reverted the import.
    /// <para>
    /// The narrow fix: the field wins only once SelectTheme has actually chosen, and until then the
    /// profile stays the source of truth exactly as before. The same staleness affects CornerRadius
    /// and the other sliders and is older than this field (RemEx-07jij filed a follow-up).
    /// </para>
    /// </remarks>
    private bool _lightPaletteChosenThisSession;

    /// <summary>
    /// Set while <see cref="SetLightPalette"/> pushes its value out to <see cref="UseLightPaletteSwitch"/>,
    /// so the switch's own handler does not read that echo back as a fresh user choice.
    /// </summary>
    private bool _isSyncingLightPalette;

    private void SetLightPalette(bool useLight)
    {
        _useLightPalette = useLight;
        _lightPaletteChosenThisSession = true;

        _isSyncingLightPalette = true;
        try
        {
            UseLightPaletteSwitch = useLight;
        }
        finally
        {
            _isSyncingLightPalette = false;
        }
    }

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

    /// <summary>
    /// User-saved custom accent colours shown after the built-in swatches, most recent first. Also
    /// the Palette Studio's "recently used seeds" row — one list, two views of it, because a seed
    /// the user liked enough to keep and a seed they just landed on are the same thing.
    /// </summary>
    public ObservableCollection<string> CustomAccentColors { get; } = new();

    /// <summary>
    /// Whether the recently-used row has anything in it. A plain <c>Count</c> binding would need an
    /// int-to-bool converter this project does not have, and a "Recent" label over nothing reads as
    /// a broken row rather than an empty one.
    /// </summary>
    public bool HasRecentSeeds => CustomAccentColors.Count > 0;

    // ═══════════════ Palette Studio ═══════════════
    //
    // THE SEED HEX STAYS THE SINGLE SOURCE OF TRUTH and HCT is a view of it. AccentColor is what is
    // persisted and what ThemeService reads; SeedHue/SeedChroma/SeedTone are derived from it on the
    // way in and recombined into it on the way out. Holding HCT as a second stored copy would mean
    // two values that can disagree, and the one the app paints from would not be the one the sliders
    // show.

    /// <summary>
    /// Set while one representation of the seed is writing the other, so the change notification
    /// that lands on the far side is not mistaken for a new edit and bounced straight back.
    /// </summary>
    private bool _isSyncingSeed;

    /// <summary>The seed's hue in degrees (0–360). Editing it rewrites <see cref="AccentColor"/>.</summary>
    [ObservableProperty]
    private double _seedHue;

    /// <summary>The seed's chroma (0–<see cref="SeedHct.MaxChroma"/>). Editing it rewrites <see cref="AccentColor"/>.</summary>
    [ObservableProperty]
    private double _seedChroma;

    /// <summary>The seed's tone (0–100). Editing it rewrites <see cref="AccentColor"/>.</summary>
    [ObservableProperty]
    private double _seedTone;

    /// <summary>
    /// Contrast target for the generated palette, -1.0 (softer) to 1.0 (WCAG AAA). Persisted as
    /// <see cref="CustomizationSettings.ThemeContrast"/>, which until this screen existed could only
    /// be set by hand-editing the profile JSON.
    /// </summary>
    [ObservableProperty]
    private double _themeContrast;

    /// <summary>
    /// Whether the generated palette is a light one. Bound to the studio's switch; writing it is
    /// what turns <see cref="CustomizationSettings.UseLightPalette"/> from "derive it from the preset
    /// name" into an explicit choice that survives changing the seed.
    /// </summary>
    [ObservableProperty]
    private bool _useLightPaletteSwitch;

    partial void OnSeedHueChanged(double value) => PushSeedToAccent();

    partial void OnSeedChromaChanged(double value) => PushSeedToAccent();

    partial void OnSeedToneChanged(double value) => PushSeedToAccent();

    partial void OnThemeContrastChanged(double value) => ApplyAndSave();

    partial void OnUseLightPaletteSwitchChanged(bool value)
    {
        if (_isSyncingLightPalette) return;
        SetLightPalette(value);
        ApplyAndSave();
    }

    /// <summary>Rebuilds <see cref="AccentColor"/> from the three HCT axes, which repaints the shell.</summary>
    private void PushSeedToAccent()
    {
        if (_isSyncingSeed) return;

        _isSyncingSeed = true;
        try
        {
            // ApplyAndSave runs from AccentColor's own handler, so the live preview is this
            // assignment. The write to disk behind it is debounced by DashboardLayoutService, which
            // is what keeps a drag from being one file write per frame.
            AccentColor = SeedHct.ToHex(SeedHue, SeedChroma, SeedTone);
        }
        finally
        {
            _isSyncingSeed = false;
        }
    }

    /// <summary>
    /// Re-derives the three HCT axes from <see cref="AccentColor"/> — for a swatch click, a preset,
    /// or the hex box, none of which go through the wheel.
    /// </summary>
    /// <remarks>
    /// AN UNPARSEABLE ACCENT LEAVES THE SLIDERS ALONE rather than collapsing them to black. The
    /// accent can be a bad string for exactly as long as it takes the user to finish typing one, and
    /// ThemeService already has the fallback that keeps the window readable meanwhile (RemEx-07jij).
    /// </remarks>
    private void SyncSeedFromAccent()
    {
        if (_isSyncingSeed) return;
        if (!Color.TryParse(AccentColor, out var seed)) return;

        var (hue, chroma, tone) = SeedHct.FromColor(seed);

        _isSyncingSeed = true;
        try
        {
            SeedHue = hue;
            SeedChroma = chroma;
            SeedTone = tone;
        }
        finally
        {
            _isSyncingSeed = false;
        }
    }

    /// <summary>
    /// Records the seed the user has just settled on in the recently-used row. Called when a wheel
    /// drag or a key repeat ENDS, not while it runs — every intermediate colour a drag passes
    /// through is not a colour anyone chose.
    /// </summary>
    public void CommitSeedToRecents()
    {
        var hex = AccentColor;
        if (!Color.TryParse(hex, out _)) return;

        // CASE-INSENSITIVE, because the two writers disagree on case and always will. SeedHct.ToHex
        // emits upper case; the hex box saves whatever the user typed. An ordinal compare therefore
        // treats "#00f3ff" typed by hand and "#00F3FF" landed on with the wheel as two colours, and
        // they occupy two of the eight slots while looking identical.
        var existing = -1;
        for (var i = 0; i < CustomAccentColors.Count; i++)
        {
            if (!string.Equals(CustomAccentColors[i], hex, StringComparison.OrdinalIgnoreCase)) continue;
            existing = i;
            break;
        }

        if (existing == 0) return;

        if (existing > 0) CustomAccentColors.Move(existing, 0);
        else CustomAccentColors.Insert(0, hex);

        while (CustomAccentColors.Count > MaxRecentSeeds)
            CustomAccentColors.RemoveAt(CustomAccentColors.Count - 1);

        ApplyAndSave();
    }

    /// <summary>How many recently-used seeds are kept. Matches what ApplyAndSave persists.</summary>
    private const int MaxRecentSeeds = 8;

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

        // PARSED, NOT COUNTED. This checked only the LENGTH — "must be #RRGGBB or #AARRGGBB" — so
        // "#FF0O00", a capital O for a zero, was seven characters and got saved as the accent. It
        // then reached ThemeService, failed Color.TryParse there, and was persisted as a permanent
        // swatch in CustomAccentColors, so it survived a restart. Asking the parser costs the same
        // and answers the question actually being asked. (RemEx-07jij)
        if (hex.Length is not (7 or 9) || !Color.TryParse(hex, out _)) return;

        AccentColor = hex;

        // ONE WRITER FOR THE LIST. This used to build its own profile record and save it, which is a
        // second place that has to remember every field ApplyAndSave carries forward — the exact
        // shape of the deletion-by-omission bug CustomizationSettingsRoundTripTests exists to catch.
        CommitSeedToRecents();

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
        _selectedPresetId = SeedPresetCatalog.Resolve(settings.ThemeId).Id;
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
        _themeContrast = Math.Clamp(settings.ThemeContrast, -1.0, 1.0);

        // THE SWITCH HAS TO SHOW WHAT IS ACTUALLY PAINTED, and for a profile written before
        // UseLightPalette existed that is the preset-name answer, not "dark". This mirrors
        // ThemeService.ApplyCustomization's null case deliberately — a switch that reads the
        // opposite of the window behind it is worse than no switch.
        _useLightPaletteSwitch = settings.UseLightPalette
            ?? string.Equals(settings.ThemeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);

        _splashStyle = settings.SplashStyle;
        _selectedPageTitleFont = AvailableFonts.FirstOrDefault(f => f.Value == settings.PageTitleFontFamily)
                                 ?? AvailableFonts.FirstOrDefault();
        _selectedBodyFont = AvailableFonts.FirstOrDefault(f => f.Value == settings.BodyFontFamily)
                            ?? AvailableFonts.FirstOrDefault();
        _uiScale = settings.UiScale <= 0 ? 1.0 : Math.Clamp(settings.UiScale, 0.85, 1.3);

        // Load saved custom accent colours
        var profile = _layoutService.CurrentProfile;
        var colors = profile.Customization.CustomAccentColors ?? Array.Empty<string>();
        foreach (var hex in colors.Take(MaxRecentSeeds))
            CustomAccentColors.Add(hex);

        CustomAccentColors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentSeeds));

        // Seed the studio's HCT axes from the accent the profile actually carries. Done directly
        // rather than through SyncSeedFromAccent because the generated property setters would fire
        // ApplyAndSave, i.e. a save on construction before the user has touched anything.
        //
        // AN UNPARSEABLE ACCENT NEEDS THE FALLBACK SEED, NOT "LEAVE THEM ALONE". Mid-edit, leaving
        // the axes untouched is right (SyncSeedFromAccent does exactly that) — but at construction
        // there is nothing to leave alone, and the fields' default of 0/0/0 is not neutral, it is
        // BLACK. A profile written before RemEx-07jij's validation fix can still carry something like
        // "#FF0O00" (a capital O for a zero) and survive a restart, so this is reachable today: the
        // shell would paint with ThemeService's fallback while the sliders read 0/0/0 and the disc
        // rendered solid black, and the first arrow key would push #000000 over the whole app.
        // Matching ThemeService's own fallback keeps the two agreeing on what a bad seed means.
        var initialSeed = Color.TryParse(_accentColor, out var parsedSeed)
            ? parsedSeed
            : ThemeService.FallbackAccentColor;

        (_seedHue, _seedChroma, _seedTone) = SeedHct.FromColor(initialSeed);

        // Build the preset gallery. AFTER the seed axes are set, because the tiles are painted from
        // the live settings and Dynamic's tile is the live settings.
        foreach (var preset in SeedPresetCatalog.All)
            ThemePresets.Add(new SeedPresetTileViewModel(preset));

        RefreshPresetPreviews(onlyVarying: false);
        UpdatePresetSelection();

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

    /// <summary>
    /// The selected preset's <see cref="SeedPreset.Id"/>, persisted verbatim as
    /// <c>CustomizationSettings.ThemeId</c>.
    /// </summary>
    /// <remarks>
    /// A STRING RATHER THAN <see cref="AppTheme"/>, because the enum stopped being the list of
    /// presets. It is now only the list of structural theme FILES — four of them — while the gallery
    /// ships more presets than that and will ship more again. Parsing ThemeId through the enum meant
    /// every preset that was not one of the four resolved to Dynamic and logged a warning on the way.
    /// </remarks>
    [ObservableProperty]
    private string _selectedPresetId;

    /// <summary>Alias kept for the Classes.selected bindings in AXAML.</summary>
    public string SelectedThemePreset => SelectedPresetId;

    /// <summary>
    /// The preset gallery, each tile painted in its own generated palette.
    /// </summary>
    public ObservableCollection<SeedPresetTileViewModel> ThemePresets { get; } = new();

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

    partial void OnSelectedPresetIdChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedThemePreset));
        UpdatePresetSelection();
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
    partial void OnAccentColorChanged(string value)
    {
        // Order matters: the sliders have to be showing the new seed before the repaint, or a swatch
        // click leaves the wheel's thumb sitting on the colour the user just replaced.
        SyncSeedFromAccent();
        ApplyAndSave();
    }
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
            ThemeId = SelectedPresetId,
            ThemeContrast = Math.Clamp(ThemeContrast, -1.0, 1.0),

            // THE SEED'S OWN CHROMA, not the slider's requested one. Most hue/tone pairs cannot
            // reach high chroma in sRGB, so a request of 120 can land on a colour of 60 — writing
            // the request would persist a number the seed does not have. Writing what it achieved
            // means Hct.From(hue, ThemeSeedChroma, tone) reproduces this exact seed, which is the
            // property the Android side needs for the two platforms to agree (RemEx-ndhlv).
            ThemeSeedChroma = SeedHct.ChromaOf(AccentColor, carried.ThemeSeedChroma),
            UseLightPalette = _lightPaletteChosenThisSession ? _useLightPalette : carried.UseLightPalette,
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
            CustomAccentColors = CustomAccentColors.Take(MaxRecentSeeds).ToList()
        };

        // Update the current profile object
        var profile = _layoutService.CurrentProfile with { Customization = settings };

        // Use the internal setter if possible, or request a save
        _themeService.ApplyCustomization(settings);
        _layoutService.RequestSave(profile);

        // The gallery is downstream of the same settings the shell is, so it repaints here rather
        // than from four separate change handlers that would each have to remember to.
        RefreshPresetPreviews(onlyVarying: true);
    }

    [RelayCommand]
    private void SelectTheme(string themeName)
    {
        // NO ENUM PARSE AND NO SWITCH. Both used to be here, and both were a second place a preset
        // had to be declared - the switch arms were even scraped by the tests, because there was
        // nowhere else to read a preset's seed from. The catalog is that place now, so an unknown
        // id is a lookup miss rather than a silently-skipped command.
        if (!SeedPresetCatalog.TryGet(themeName, out var preset)) return;

        _isApplyingPreset = true;
        try
        {
            CornerRadius = preset.CornerRadius;
            RemoteCardCornerRadius = preset.RemoteCardCornerRadius;
            GlowStrength = preset.GlowStrength;
            GlassOpacity = preset.GlassOpacity;

            // THE NULLS ARE THE POINT, not an oversight. Dynamic declines to choose a seed, a
            // variant, a mode and a contrast, and "declines" has to mean the existing value survives
            // - a preset that means "my own colour" cannot be the one that overwrites it.
            if (preset.Seed is { } seed) AccentColor = seed;
            if (preset.SchemeVariant is { } variant) SchemeVariant = variant;
            if (preset.IsLight is { } light) SetLightPalette(light);
            if (preset.Contrast is { } contrast) ThemeContrast = contrast;
            if (preset.SplashStyle is { } splash) SplashStyle = splash;

            // LAST, because it is the one that triggers the save. Everything above writes through a
            // generated setter whose partial handler is short-circuited by _isApplyingPreset, so
            // until this line the profile still holds the outgoing preset's numbers.
            SelectedPresetId = preset.Id;
        }
        finally
        {
            _isApplyingPreset = false;
        }

        ApplyAndSave();
    }

    /// <summary>
    /// Rebuilds every tile's palette. Cheap enough to run on any seed change: one
    /// <c>Generate</c> per tile, and only the Dynamic tile's result actually varies.
    /// </summary>
    private void RefreshPresetPreviews(bool onlyVarying)
    {
        var liveIsLight = CurrentIsLightPalette();
        foreach (var tile in ThemePresets)
        {
            // A preset that pins all four inputs renders the same colours forever, so re-running the
            // generator for it on every slider tick is pure waste - and these tick continuously.
            if (onlyVarying && !HasLiveInput(tile.Preset)) continue;
            tile.Refresh(AccentColor, SchemeVariant, liveIsLight, ThemeContrast);
        }
    }

    private static bool HasLiveInput(SeedPreset preset) =>
        preset.Seed is null || preset.SchemeVariant is null
        || preset.IsLight is null || preset.Contrast is null;

    /// <summary>
    /// What light/dark the profile is actually painting right now - the explicit choice if there is
    /// one, and otherwise the same preset-name answer <see cref="ThemeService.ApplyCustomization"/>
    /// falls back to. The tiles have to agree with the window behind them.
    /// </summary>
    private bool CurrentIsLightPalette() =>
        _lightPaletteChosenThisSession
            ? _useLightPalette ?? UseLightPaletteSwitch
            : UseLightPaletteSwitch;

    private void UpdatePresetSelection()
    {
        foreach (var tile in ThemePresets)
        {
            tile.IsSelected = string.Equals(tile.Id, SelectedPresetId, StringComparison.OrdinalIgnoreCase);
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
        // The tiles each hold a LocalizationService.Instance.PropertyChanged subscription, and that
        // singleton outlives this view model - an undisposed tile is pinned for the process lifetime.
        foreach (var tile in ThemePresets) tile.Dispose();
        ThemePresets.Clear();
    }
}
