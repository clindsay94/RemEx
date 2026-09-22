using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Desktop.Services;
using Remex.Desktop.Models;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.Theme;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;

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
    private readonly ILogger<CustomizationViewModel> _logger;
    private bool _isApplyingPreset;

    /// <summary>
    /// Snap-to-grid, grid size and pinned sensors (RemEx-4kv0g.4.2) — a sibling view model, not owned
    /// by this class; temporarily bound from a LAYOUT section on this sheet (drop 3 gives it a real
    /// tab). Null only in tests that construct this VM without one.
    /// </summary>
    public LayoutSettingsViewModel? Layout { get; }

    /// <summary>
    /// Which Personalize tab is showing (RemEx-4kv0g.4.3) — remembered for the run, not persisted:
    /// closing and reopening the sheet lands back on this tab, but a restart always opens Colour.
    /// Deliberately excluded from <see cref="BuildCurrentSettings"/>.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Short-circuits only the persist half of <see cref="ApplyAndSave"/> — the live repaint (and
    /// preview/tile refresh) still runs, <c>_layoutService.RequestSave</c> alone does not.
    /// </summary>
    /// <remarks>
    /// RemEx-k7891: <see cref="RefreshBackgroundTypes"/>'s platform fallback has to go through the
    /// real <c>CanvasBackgroundType</c> setter — CommunityToolkit.Mvvm's generator forbids writing an
    /// <c>[ObservableProperty]</c> backing field directly from anywhere but the constructor
    /// (MVVMTK0034) — so this is the same shape as <see cref="_isApplyingPreset"/>, guarding the one
    /// step that must not run instead of skipping the setter's side effects altogether.
    /// </remarks>
    private bool _suppressPersist;

    /// <summary>
    /// Whether <see cref="CanvasBackgroundType"/> is currently showing
    /// <see cref="RefreshBackgroundTypes"/>'s session-only platform fallback rather than the
    /// profile's real choice.
    /// </summary>
    /// <remarks>
    /// RemEx-k7891 FOLLOW-UP (Opus review, MEDIUM). <see cref="_suppressPersist"/> alone only
    /// guards the fallback's OWN call into <see cref="ApplyAndSave"/> — but the live
    /// <c>CanvasBackgroundType</c> stays "Aurora" afterwards, so the very NEXT save from ANY other
    /// property (a hardware-accent sync, an unrelated slider) would persist that displayed fallback
    /// over the real, unsupported-here material unless <see cref="ApplyAndSave"/> substitutes
    /// <see cref="_unsupportedPersistedMaterial"/> back in while this flag is set. A genuine pick
    /// through the picker clears both — see <see cref="OnCanvasBackgroundTypeChanged(string)"/>.
    /// </remarks>
    private bool _isBackgroundFallbackActive;

    /// <summary>
    /// The real, persisted <c>BackgroundMaterial</c> stashed away while
    /// <see cref="_isBackgroundFallbackActive"/> is set — including <c>null</c>, the hand-edited
    /// missing-field shape. See that field's remarks for why this exists.
    /// </summary>
    private string? _unsupportedPersistedMaterial;

    /// <summary>Held so <see cref="Dispose"/> can detach it — the theme service outlives this VM.</summary>
    private Action<CustomizationSettings>? _onCustomizationApplied;

    /// <summary>
    /// The host's latest <c>theme_sync</c> snapshot, resolved once at construction time
    /// (RemEx-sudp8) — the same tolerant, try/catch-guarded lookup
    /// <c>SystemStatusViewModel.ResolveFromHost</c> uses, so a sheet opened while the embedded host
    /// is down (or absent in a test) shows no "Match my phone" affordance rather than throwing.
    /// </summary>
    private readonly IPhoneThemeSnapshotStore? _phoneThemeStore;

    /// <summary>Held so <see cref="Dispose"/> can detach it — the store outlives this VM.</summary>
    private Action<PhoneThemeSnapshot?>? _onPhoneThemeChanged;

    /// <summary>
    /// The Home pins this sheet's Flyout section's Cards checklist is a subset of (Flyout D2 .2,
    /// RemEx-4kv0g.18.6). Optional and try/catch-free-checked like <see cref="_phoneThemeStore"/>
    /// above — every test that constructs this view model without a real <c>ShellViewModel</c> also
    /// has no reason to stand up a <c>HomeViewModel</c>, so a null here just means
    /// <see cref="FlyoutCards"/> stays empty rather than the sheet failing to open.
    /// </summary>
    private readonly HomeViewModel? _home;

    /// <summary>Held so <see cref="Dispose"/> can detach it — <see cref="_home"/> outlives this VM.</summary>
    private NotifyCollectionChangedEventHandler? _onPinnedSensorsChanged;

    /// <summary>
    /// Source for the Flyout section's Apps checklist (Flyout D2 .2, RemEx-4kv0g.18.6) — the same
    /// launcher store <c>AppLauncherViewModel</c> and <c>TrayFlyoutViewModel</c> read. Optional for
    /// the same reason <see cref="_home"/> is.
    /// </summary>
    private readonly ILauncherStorageService? _launcherStorage;

    /// <summary>
    /// The Flyout section's persisted state (Flyout D2 .2, RemEx-4kv0g.18.6 fix round 1, HIGH) — the
    /// SOURCE OF TRUTH <see cref="BuildCurrentSettings"/> writes from. <see cref="FlyoutCards"/>,
    /// <see cref="FlyoutTiles"/> and <see cref="FlyoutApps"/> are a VIEW onto these three lists, not
    /// the other way around: none of the three is guaranteed to be fully populated at the moment of
    /// an unrelated save (<see cref="_home"/>/<see cref="_launcherStorage"/> are optional, and
    /// <see cref="RefreshFlyoutAppsAsync"/> is async), so deriving a save FROM the checklist is the
    /// preset-wipe shape — a slider nudge silently writing an empty or partial list over the user's
    /// real one. Loaded once in the constructor; a checkbox toggle mutates exactly one id
    /// (<see cref="OnFlyoutCardShownChanged"/>/<see cref="OnFlyoutTileShownChanged"/>/
    /// <see cref="OnFlyoutAppShownChanged"/>); a hidden sensor id is pruned only when the user
    /// actually unpins that sensor (see the <c>PinnedSensors.CollectionChanged</c> handler in the
    /// constructor), never wholesale on a rebuild or a save.
    /// </summary>
    private readonly List<string> _flyoutHiddenSensorIds;

    /// <summary>See <see cref="_flyoutHiddenSensorIds"/>. Never pruned — a hidden tile id always
    /// resolves (the six tiles are fixed), so there is no "no longer exists" case to prune for.</summary>
    private readonly List<string> _flyoutHiddenTileIds;

    /// <summary>See <see cref="_flyoutHiddenSensorIds"/>. A shown-list, not a hidden-list: an id the
    /// launcher does not currently resolve stays here untouched (the entry "may come back" — deleting
    /// an app and re-adding an identically-configured one is not this VM's problem to guess at).</summary>
    private readonly List<Guid> _flyoutAppIds;

    // The old _useLightPalette mirror is gone with the switch it fed (RemEx-zk5bc): the null-mode
    // fallback reads settings.UseLightPalette inline at load, and the save path carries the stored
    // value verbatim - a private copy of a superseded field is exactly the two-values-that-can-
    // disagree shape the mode exists to end.

    /// <summary>
    /// The mode carried into the next save: <see cref="ThemeModes.Light"/>, <c>Dark</c>, or
    /// <c>System</c>. Written by the mode picker and by <see cref="SelectTheme"/> — selecting a
    /// preset is choosing its light/dark, so a preset pick pins the mode rather than leaving it
    /// on System (RemEx-zk5bc).
    /// </summary>
    private string? _themeMode;

    /// <summary>
    /// Whether the mode has been chosen (picker or preset) since this view model was constructed.
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
    private bool _themeModeChosenThisSession;

    /// <summary>
    /// Set while <see cref="SetThemeMode"/> pushes its value out to <see cref="ThemeModeIndex"/>,
    /// so the picker's own handler does not read that echo back as a fresh user choice.
    /// </summary>
    private bool _isSyncingThemeMode;

    /// <summary>Picker order: 0 Light, 1 Dark, 2 System — matching the ComboBox in the view.</summary>
    private static int ThemeModeToIndex(string? mode) => mode switch
    {
        ThemeModes.Light => 0,
        ThemeModes.System => 2,
        _ => 1,
    };

    private static string ThemeModeFromIndex(int index) => index switch
    {
        0 => ThemeModes.Light,
        2 => ThemeModes.System,
        _ => ThemeModes.Dark,
    };

    private void SetThemeMode(string mode)
    {
        _themeMode = mode;
        _themeModeChosenThisSession = true;

        _isSyncingThemeMode = true;
        try
        {
            ThemeModeIndex = ThemeModeToIndex(mode);
        }
        finally
        {
            _isSyncingThemeMode = false;
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

    public ObservableCollection<string> AvailableSchemeVariants { get; } = new(SchemeVariants.All);

    /// <summary>
    /// The variant row: one strip per <see cref="AvailableSchemeVariants"/> entry, each painted from
    /// the live seed in its OWN variant (RemEx-lrxyo).
    /// </summary>
    /// <remarks>
    /// Refreshed from <see cref="RefreshSchemeVariantStrips"/>, which runs at the tail of
    /// <c>ApplyAndSave</c> AND from the <c>CustomizationApplied</c> handler in the constructor. Both
    /// are required and neither implies the other: the first covers every user action, the second
    /// covers an OS light/dark flip, which repaints the app with no user action at all. An earlier
    /// draft of this comment claimed the strips were "refreshed everywhere ThemePresets is" while
    /// only the first path was wired, and that sentence is exactly what a future maintainer would
    /// have trusted instead of re-deriving it.
    /// </remarks>
    public ObservableCollection<SchemeVariantStripViewModel> SchemeVariantStrips { get; } = new();

    /// <summary>The tonal-ramp preview beneath the variant row, for the currently-selected variant.</summary>
    public TonalRampViewModel TonalRamp { get; } = new();

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
    /// The base-mode picker's selection: 0 Light, 1 Dark, 2 System (follow the OS). Writing it is
    /// what turns <see cref="CustomizationSettings.ThemeMode"/> into an explicit choice that
    /// survives changing the seed; System makes the palette track the OS setting live
    /// (RemEx-zk5bc).
    /// </summary>
    [ObservableProperty]
    private int _themeModeIndex = 1;

    partial void OnSeedHueChanged(double value) => PushSeedToAccent();

    partial void OnSeedChromaChanged(double value)
    {
        // A programmatic write (a saved palette, a preset) can hand this a value outside the wheel's
        // own range; only SeedHct.ToColor clamped it downstream, so the VM kept reporting the
        // unclamped number while the Vibrancy slider itself pinned at MaxChroma (RemEx-8twk0.8 gate
        // addendum). Re-entering the setter with the clamped value keeps this the one place that
        // clamps, rather than adding a second clamp at every call site.
        var clamped = Math.Clamp(value, 0, SeedHct.MaxChroma);
        if (clamped != value) { SeedChroma = clamped; return; }
        PushSeedToAccent();
    }

    partial void OnSeedToneChanged(double value) => PushSeedToAccent();

    partial void OnThemeContrastChanged(double value) => ApplyAndSave();

    partial void OnThemeModeIndexChanged(int value)
    {
        if (_isSyncingThemeMode) return;
        SetThemeMode(ThemeModeFromIndex(value));
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

    // ═══════════════ Share palette (RemEx-a7uzb) ═══════════════
    //
    // Copy-as-AXAML and JSON export/import for the Palette Studio. The three seams below mirror
    // SettingsViewModel's savefile picker seams (SettingsViewModel.cs:178-184) and
    // DiagnosticLogsViewModel's clipboard seam — wired by the view via TopLevel so this view model
    // stays testable without a running Avalonia.

    /// <summary>Wired by the view to the window's clipboard. Used by <see cref="CopyPaletteAsAxamlCommand"/>.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    /// <summary>Wired by the view to a native "Save File" picker. Used by <see cref="ExportPaletteJsonCommand"/>.</summary>
    public Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickSaveFileAsync { get; set; }

    /// <summary>Wired by the view to a native "Open File" picker. Used by <see cref="ImportPaletteJsonCommand"/>.</summary>
    public Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? PickOpenFileAsync { get; set; }

    /// <summary>Builds the recipe for the palette currently painted, from the live-edited fields
    /// rather than the last-saved profile — the studio always shares what is on screen.</summary>
    /// <remarks>
    /// Internal, not private (visible to Remex.Desktop.Tests via InternalsVisibleTo) — a test needs
    /// the same recipe the export button would produce, to pin the export/import seed identity
    /// without going through a real file picker for the export half (RemEx-8twk0.7 fix round).
    /// </remarks>
    internal PaletteRecipe RecipeFromCurrent()
    {
        var mode = _themeModeChosenThisSession
            ? _themeMode
            : _layoutService.CurrentProfile.Customization.ThemeMode;
        return new PaletteRecipe(
            AccentColor,
            SchemeVariant,
            mode ?? ThemeModes.Dark,
            ThemeContrast,
            SeedHct.ChromaOf(AccentColor, _layoutService.CurrentProfile.Customization.ThemeSeedChroma),
            ColorSource,
            string.IsNullOrWhiteSpace(NewPaletteName) ? null : NewPaletteName.Trim());
    }

    /// <summary>Copies the current palette to the clipboard as a compilable Avalonia
    /// <c>ResourceDictionary</c>. Silently returns when the view has not wired a clipboard.</summary>
    [RelayCommand]
    private async Task CopyPaletteAsAxamlAsync()
    {
        if (CopyToClipboardAsync is null) return;

        try
        {
            var recipe = RecipeFromCurrent();
            var isDark = !ThemeService.ResolveIsLight(
                _layoutService.CurrentProfile.Customization,
                SeedPresetCatalog.Resolve(SelectedPresetId),
                ThemeService.TryGetOsIsLight());
            var palette = DynamicColorGenerator.Generate(Color.Parse(AccentColor), SchemeVariant, isDark, ThemeContrast);

            await CopyToClipboardAsync(PaletteExchange.ToAxaml(palette, recipe));

            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Notification_PaletteCopied_Title"],
                LocalizationService.Instance["Notification_PaletteCopied_Message"]);
        }
        catch (Exception ex)
        {
            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Custom_CopyPaletteAxaml"],
                string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    /// <summary>Exports the current palette's recipe as a <c>.remexpalette</c> JSON file.</summary>
    [RelayCommand]
    private async Task ExportPaletteJsonAsync()
    {
        if (PickSaveFileAsync is null) return;

        try
        {
            var file = await PickSaveFileAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["Custom_ExportPaletteJson"],
                SuggestedFileName = $"RemEx-palette-{DateTime.Now:yyyyMMdd}",
                DefaultExtension = "remexpalette",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(LocalizationService.Instance["Custom_PaletteFileType"])
                    {
                        Patterns = new[] { "*.remexpalette" },
                    },
                },
            });

            if (file is null) return;

            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(PaletteExchange.ToJson(RecipeFromCurrent()));
            }

            // A closed native dialog with nothing else happening is indistinguishable from
            // cancel; say where the file went, the way copy and import announce themselves.
            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Custom_ExportPaletteJson"],
                file.Name);
        }
        catch (Exception ex)
        {
            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Custom_ExportPaletteJson"],
                string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    /// <summary>Imports a <c>.remexpalette</c> JSON file and applies it through the same setters the
    /// UI uses, so ApplyAndSave/ThemeService repaint exactly as a manual edit would.</summary>
    [RelayCommand]
    private async Task ImportPaletteJsonAsync()
    {
        if (PickOpenFileAsync is null) return;

        try
        {
            var files = await PickOpenFileAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["Custom_ImportPaletteJson"],
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(LocalizationService.Instance["Custom_PaletteFileType"])
                    {
                        Patterns = new[] { "*.remexpalette" },
                    },
                },
            });

            if (files.Count == 0) return;

            string json;
            await using (var stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                json = await reader.ReadToEndAsync();

            if (!PaletteExchange.TryParseJson(json, out var recipe) || recipe is null)
            {
                NotificationService.Instance.Notify(
                    NotificationImportance.Outcome,
                    LocalizationService.Instance["Custom_ImportPaletteJson"],
                    LocalizationService.Instance["Custom_PaletteImportInvalid"]);
                return;
            }

            var tile = AddSavedPalette(new SavedPalette
            {
                Name = recipe.Name ?? NextDefaultPaletteName(),
                ColorSource = recipe.ColorSource,
                Seed = recipe.Seed,
                Vibrancy = recipe.SeedChroma,
                Contrast = recipe.Contrast,
                Strategy = recipe.Variant,
            });
            SetThemeMode(recipe.Mode);
            ApplySavedPalette(tile);   // saves; the mode rides the same save

            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Notification_PaletteImported_Title"],
                string.Format(
                    LocalizationService.Instance["Notification_PaletteImported_Message"],
                    recipe.Seed, recipe.Variant));
        }
        catch (Exception ex)
        {
            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Custom_ImportPaletteJson"],
                string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message));
        }
    }

    public CustomizationViewModel(
        ShellViewModel shell, DashboardLayoutService layoutService, ThemeService themeService,
        HomeViewModel? home = null, ILauncherStorageService? launcherStorage = null,
        ILogger<CustomizationViewModel>? logger = null, LayoutSettingsViewModel? layout = null)
    {
        _shell = shell;
        _layoutService = layoutService;
        _themeService = themeService;
        _home = home;
        _launcherStorage = launcherStorage;
        _logger = logger ?? NullLogger<CustomizationViewModel>.Instance;
        Layout = layout;

        // NOT EmbeddedHostServiceLocator.Require<T>, which throws when the host is absent — the
        // same reason SystemStatusViewModel.ResolveFromHost isn't either. This sheet has to survive
        // the host being down or, in a test, never having existed at all; it just shows no "Match my
        // phone" affordance in that case.
        try
        {
            _phoneThemeStore = App.EmbeddedHostServices?.GetService(typeof(IPhoneThemeSnapshotStore))
                as IPhoneThemeSnapshotStore;
        }
        catch (ObjectDisposedException)
        {
            _phoneThemeStore = null;
        }

        if (_phoneThemeStore is not null)
        {
            // POSTED, NOT RUN INLINE: Set() is called from the agent's WebSocket receive loop, which
            // is not the UI thread this view model's bindings expect (RemEx-r8c6 is the same shape
            // for LauncherEntriesReceived et al.).
            _onPhoneThemeChanged = _ => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(HasPhoneTheme)));
            _phoneThemeStore.Changed += _onPhoneThemeChanged;
        }

        // Initialize from current profile
        var settings = _layoutService.CurrentProfile.Customization;
        _selectedPresetId = SeedPresetCatalog.Resolve(settings.ThemeId).Id;
        _cornerRadius = settings.CornerRadius;
        _remoteCardCornerRadius = settings.RemoteCardCornerRadius;
        _cardBorderThickness = settings.CardBorderThickness;
        _glassOpacity = settings.GlassOpacity;
        _appWindowOpacity = settings.AppWindowOpacity;
        _flyoutOpacity = Math.Clamp(settings.FlyoutOpacity, 0.0, 1.0);
        // AUTHORITATIVE, NOT DERIVED FROM A CHECKLIST (fix round 1, RemEx-4kv0g.18.6 review, HIGH).
        // FlyoutCards/FlyoutTiles/FlyoutApps are a VIEW onto these three lists, built from
        // HomeViewModel.PinnedSensors / the six fixed tile ids / the launcher store - none of which
        // is guaranteed to be populated (or populated with everything the record has ever recorded)
        // at the moment of a save. Deriving the persisted lists FROM the checklists is the
        // preset-wipe shape: a slider nudge before RefreshFlyoutAppsAsync finishes, or with _home /
        // _launcherStorage null (both optional), silently wrote an empty or partial list over the
        // user's real one. These three copies are what BuildCurrentSettings actually persists; a
        // checkbox toggle mutates the matching field by exactly one id (OnFlyoutCardShownChanged /
        // OnFlyoutTileShownChanged / OnFlyoutAppShownChanged) and nothing else touches them except
        // the one-id prune below when a sensor is actually unpinned.
        _flyoutHiddenSensorIds = new List<string>(settings.FlyoutHiddenSensorIds ?? []);
        _flyoutHiddenTileIds = new List<string>(settings.FlyoutHiddenTileIds ?? []);
        _flyoutAppIds = new List<Guid>(settings.FlyoutAppIds ?? []);
        _glowStrength = settings.GlowStrength;
        _accentColor = settings.AccentColor;
        _schemeVariant = SchemeVariants.Normalize(settings.SchemeVariant);
        _canvasBackgroundType = settings.BackgroundMaterial;
        _wallpaperSource = settings.WallpaperSource;
        _wallpaperBlur = Math.Clamp(settings.WallpaperBlur, 0.0, 1.0);
        _wallpaperImagePath = settings.WallpaperImagePath;

        // Desktop wallpaper only where the registry can be read (Windows); Pick an image everywhere.
        if (OperatingSystem.IsWindows()) AvailableWallpaperSources.Add(WallpaperSources.Desktop);
        AvailableWallpaperSources.Add(WallpaperSources.Image);
        if (!AvailableWallpaperSources.Contains(_wallpaperSource)) _wallpaperSource = WallpaperSources.Image;

        _themeMode = settings.ThemeMode;
        _themeContrast = Math.Clamp(settings.ThemeContrast, -1.0, 1.0);

        _wallpaperSeedIndex = Math.Max(0, settings.WallpaperSeedIndex);

        // Which sources this platform offers, then the stored choice resolved onto them without a
        // save: a Windows profile opened on Linux runs on Custom for the session (spec section 9).
        if (OperatingSystem.IsWindows())
        {
            AvailableColorSources.Add(ColorSources.WindowsAccent);
            AvailableColorSources.Add(ColorSources.Wallpaper);
        }
        AvailableColorSources.Add(ColorSources.Custom);
        _colorSource = AvailableColorSources.Contains(settings.ColorSource) ? settings.ColorSource : ColorSources.Custom;
        if (_colorSource == ColorSources.WindowsAccent) _sourceAccentHex = SystemSeedSources.TryGetWindowsAccent();

        // THE PICKER HAS TO SHOW WHAT IS ACTUALLY PAINTED. Migration stamps ThemeMode on load, so
        // it is normally present; the fallback chain below is the hand-edited-null shape and
        // mirrors ThemeService.ResolveIsLight's legacy case deliberately — a picker that reads the
        // opposite of the window behind it is worse than no picker.
        _themeModeIndex = settings.ThemeMode is { } mode
            ? ThemeModeToIndex(mode)
            : (settings.UseLightPalette
                ?? string.Equals(settings.ThemeId, "SolarFlare", StringComparison.OrdinalIgnoreCase))
                ? 0
                : 1;

        _splashStyle = settings.SplashStyle;
        _selectedPageTitleFont = AvailableFonts.FirstOrDefault(f => f.Value == settings.PageTitleFontFamily)
                                 ?? AvailableFonts.FirstOrDefault();
        // Null on disk means "follows the title font" (RemEx-n6csl); the picker shows the effective font.
        _selectedPageSubtitleFont = AvailableFonts.FirstOrDefault(f => f.Value == (settings.PageSubtitleFontFamily ?? settings.PageTitleFontFamily))
                                    ?? AvailableFonts.FirstOrDefault();
        _selectedBodyFont = AvailableFonts.FirstOrDefault(f => f.Value == settings.BodyFontFamily)
                            ?? AvailableFonts.FirstOrDefault();
        _uiScale = settings.UiScale <= 0 ? 1.0 : Math.Clamp(settings.UiScale, 0.85, 1.3);

        // Personalize → Text (RemEx-jt6w5): clamped on read, never rewritten on disk until touched.
        var typography = TypographySettings.Normalize(settings.Typography);
        _headersScale = typography.HeadersScale;
        _subtitlesScale = typography.SubtitlesScale;
        _bodyScale = typography.BodyScale;
        _smallScale = typography.SmallScale;
        _sensorScale = typography.SensorScale;
        _headersBold = typography.HeadersBold;
        _subtitlesBold = typography.SubtitlesBold;
        _bodyBold = typography.BodyBold;
        _smallBold = typography.SmallBold;
        _sensorBold = typography.SensorBold;
        _sensorTitleBackdrop = typography.SensorTitleBackdrop;
        _textShadowEnabled = typography.ShadowEnabled;
        _textShadowStrength = typography.ShadowStrength;

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

        // CHROMA COMES FROM THE PERSISTED REQUEST, NOT THE SEED'S OWN ACHIEVED VALUE (RemEx-ceu4x).
        // Reading FromColor's chroma here reopened the sheet on whatever the accent could hold last
        // time, not on what the person asked the slider for — so a Vibrancy of 90 that a Windows
        // accent hue could only reach 60 of showed the slider parked at 60, permanently, the moment
        // the sheet was closed and reopened once.
        //
        // CLAMPED HERE TOO (RemEx-aidt1 review LOW), the same range OnSeedChromaChanged clamps a
        // live edit to. This bypasses that setter (see the comment above this constructor's own
        // block for why), so a hand-edited profile carrying a request outside the wheel's own range
        // would otherwise show the slider itself pinned at MaxChroma while the field it is bound to
        // reports the unclamped number - the exact split RemEx-8twk0.8 already fixed for a live drag.
        var (hue, _, tone) = SeedHct.FromColor(initialSeed);
        _seedHue = hue;
        _seedChroma = Math.Clamp(settings.ThemeSeedChromaRequest, 0, SeedHct.MaxChroma);
        _seedTone = tone;

        // Build the preset gallery. AFTER the seed axes are set, because the tiles are painted from
        // the live settings and Dynamic's tile is the live settings.
        foreach (var preset in SeedPresetCatalog.All)
            ThemePresets.Add(new SeedPresetTileViewModel(preset));

        // Same ordering reason as the gallery above — painted from the live settings, so built
        // after the seed axes exist. AddSavedPalette calls CurrentIsLightPalette(), which reads
        // _themeModeIndex — already set above.
        foreach (var saved in settings.SavedPalettes ?? Array.Empty<SavedPalette>())
            AddSavedPalette(saved);

        // Same ordering reason as the gallery above: built after the seed axes exist, since the
        // strips and the ramp are painted from the live settings.
        foreach (var variant in AvailableSchemeVariants)
            SchemeVariantStrips.Add(new SchemeVariantStripViewModel(variant));

        RefreshPresetPreviews(onlyVarying: false);
        UpdatePresetSelection();
        RefreshSchemeVariantStrips();
        UpdateSchemeVariantSelection();

        // AN OS THEME FLIP REPAINTS THE TILES TOO (RemEx-zk5bc review). In System mode the window
        // can change light/dark with no slider touched — ApplyCustomization re-runs from the
        // ColorValuesChanged listener — and the Dynamic tile is painted from the live settings, so
        // without this it keeps the old mode's swatches until the next user action. A NAMED
        // delegate in a field, not a lambda, so Dispose can detach it — the same convention
        // ShellViewModel uses on this event, and for the same reason: the service outlives us.
        // The variant strips ride this event too (review HIGH, RemEx-lrxyo). They are previews of
        // what a click WOULD produce, so an OS light/dark flip that repaints the app without any
        // slider being touched has to repaint them as well — otherwise the row keeps rendering the
        // old mode's palette and the preview stops agreeing with the app, which is the one thing it
        // exists to guarantee. This is the same defect RemEx-zk5bc's review caught for the preset
        // tiles, which is why this line exists at all.
        // No Dispatcher.Post: CustomizationApplied is already raised inside one, so the collection
        // mutations below stay on the UI thread.
        _onCustomizationApplied = applied =>
        {
            // A seed the coordinator wrote (Windows accent changed) has to reach the wheel and the
            // hex box, or the next slider nudge writes the old seed back over it. _isApplyingPreset
            // short-circuits ApplyAndSave, so this is a sync, not a second save.
            if (!IsCustomSource && !string.Equals(applied.AccentColor, AccentColor, StringComparison.OrdinalIgnoreCase))
            {
                _isApplyingPreset = true;
                try
                {
                    // MIRRORS AdoptSourceSeed, NOT A PLAIN AccentColor ASSIGNMENT (RemEx-aidt1).
                    // Assigning AccentColor directly ran OnAccentColorChanged -> SyncSeedFromAccent
                    // UNGUARDED (this handler never sets _isSyncingSeed), which re-derives SeedChroma
                    // from the just-applied seed's ACHIEVED chroma - silently overwriting the live
                    // Vibrancy REQUEST with whatever the coordinator's hue could reach. The next
                    // unrelated slider nudge then persisted that corrupted request, reintroducing the
                    // exact ratchet RemEx-ceu4x fixed, just gated behind "sheet open during a
                    // background accent sync". Taking hue/tone only and recombining through
                    // PushSeedToAccent (which guards its OWN AccentColor write with _isSyncingSeed)
                    // keeps SeedChroma exactly what it already was.
                    if (Color.TryParse(applied.AccentColor, out var appliedSeed))
                    {
                        var (hue, _, tone) = SeedHct.FromColor(appliedSeed);
                        _isSyncingSeed = true;
                        try { SeedHue = hue; SeedTone = tone; }
                        finally { _isSyncingSeed = false; }
                        PushSeedToAccent();
                    }
                    else
                    {
                        // Unreachable in practice - ColorSourceCoordinator only ever hands this
                        // event a hex ShapedBySource itself parsed - but a direct assignment is the
                        // closest thing to the old behaviour if it ever is.
                        AccentColor = applied.AccentColor;
                    }
                }
                finally { _isApplyingPreset = false; }
            }
            if (IsWindowsAccentSource) SourceAccentHex = SystemSeedSources.TryGetWindowsAccent();

            RefreshPresetPreviews(onlyVarying: true);
            RefreshSchemeVariantStrips();
            // Same double-placement as the two lines above, and for the same reason: this covers an
            // OS light/dark flip or a hardware-accent sync, neither of which calls ApplyAndSave, so
            // its own tail call (below, in ApplyAndSave) never runs for them. The two calls DO both
            // fire for an ApplyAndSave-driven change too (ApplyCustomization posts to the UI thread,
            // so this one lands a tick later there, or synchronously wherever PostToUiThread is
            // wired to run inline) — a second cheap repaint, not a correctness issue, and removing
            // either one reopens the gap the other exists to close (RemEx-8twk0.7 review LOW 2).
            RefreshSavedPaletteTiles();
        };
        _themeService.CustomizationApplied += _onCustomizationApplied;

        // Flyout section (Flyout D2 .2, RemEx-4kv0g.18.6). Tiles first - it needs no collaborator and
        // has to be non-empty before the section can render anything. Cards mirrors the pins that
        // exist right now and re-derives whenever HomeViewModel's collection changes; Apps reloads
        // from the launcher store asynchronously (RefreshFlyoutApps), the same "reload on open/
        // Refresh" contract TrayFlyoutViewModel already gives its own copy of this list.
        RebuildFlyoutTiles();
        RebuildFlyoutCards();
        if (_home is not null)
        {
            // PRUNE ON REMOVE ONLY, AND ONLY THE ID THAT WAS ACTUALLY UNPINNED (fix round 1,
            // RemEx-4kv0g.18.6 review, HIGH). _flyoutHiddenSensorIds is deliberately never
            // resynced wholesale against the live pin set - a sensor that is hidden but not
            // currently pinned (offline this session, or just not staged on the canvas yet) has to
            // keep its hidden id for when it comes back. The one legitimate way an id stops meaning
            // anything is the user unpinning that exact sensor, so that is the one case this handler
            // acts on; every other change (Add, Reset) only rebuilds the view.
            _onPinnedSensorsChanged = (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
                {
                    var pruned = false;
                    foreach (SensorViewModel removed in e.OldItems)
                        pruned |= _flyoutHiddenSensorIds.RemoveAll(
                            id => string.Equals(id, removed.Name, StringComparison.OrdinalIgnoreCase)) > 0;
                    if (pruned) ApplyAndSave();
                }
                RebuildFlyoutCards();
            };
            _home.PinnedSensors.CollectionChanged += _onPinnedSensorsChanged;
        }
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        RefreshFlyoutApps();

        // Load available background types
        RefreshBackgroundTypes();

        // Surface any persisted font that no longer resolves on this machine.
        ValidateFonts();

        // Repopulate the wallpaper candidates so the swatches show on open. AdoptSourceSeed saves
        // only if the seed actually changes (PushSeedToAccent assigns AccentColor; the generated
        // setter is a no-op for an equal string).
        if (IsWallpaperSource) _ = RefreshWallpaperSeedsAsync();
    }

    // ═══ Flyout section (Flyout D2 .2, RemEx-4kv0g.18.6) ═══

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => RebuildFlyoutTiles();

    /// <summary>
    /// A card checkbox toggled — mutates <see cref="_flyoutHiddenSensorIds"/> by exactly the one id
    /// the sender carries and saves. NEVER rebuilds <see cref="FlyoutCards"/> from here (fix round 1,
    /// RemEx-4kv0g.18.6 review, HIGH): the field is authoritative and the checklist item's own
    /// <c>IsShown</c> already reflects what the user just clicked, so a rebuild would be redundant at
    /// best and, if it read from anywhere other than this same field, a second source of truth.
    /// </summary>
    private void OnFlyoutCardShownChanged(object? sender, bool value)
    {
        if (sender is not FlyoutCardChoice choice) return;

        if (value)
            _flyoutHiddenSensorIds.RemoveAll(id => string.Equals(id, choice.SensorId, StringComparison.OrdinalIgnoreCase));
        else if (!_flyoutHiddenSensorIds.Contains(choice.SensorId, StringComparer.OrdinalIgnoreCase))
            _flyoutHiddenSensorIds.Add(choice.SensorId);

        ApplyAndSave();
    }

    /// <summary>A tile checkbox toggled. See <see cref="OnFlyoutCardShownChanged"/>'s remarks — same
    /// shape, against <see cref="_flyoutHiddenTileIds"/>.</summary>
    private void OnFlyoutTileShownChanged(object? sender, bool value)
    {
        if (sender is not FlyoutTileChoice choice) return;

        if (value)
            _flyoutHiddenTileIds.RemoveAll(id => string.Equals(id, choice.Id, StringComparison.OrdinalIgnoreCase));
        else if (!_flyoutHiddenTileIds.Contains(choice.Id, StringComparer.OrdinalIgnoreCase))
            _flyoutHiddenTileIds.Add(choice.Id);

        ApplyAndSave();
    }

    /// <summary>An app checkbox toggled. See <see cref="OnFlyoutCardShownChanged"/>'s remarks — same
    /// shape, against <see cref="_flyoutAppIds"/>, SHOWN-list polarity inverted (ticking ADDS the id
    /// rather than removing it).</summary>
    private void OnFlyoutAppShownChanged(object? sender, bool value)
    {
        if (sender is not FlyoutAppChoice choice) return;

        if (value)
        {
            if (!_flyoutAppIds.Contains(choice.Id)) _flyoutAppIds.Add(choice.Id);
        }
        else
        {
            _flyoutAppIds.Remove(choice.Id);
        }

        ApplyAndSave();
    }

    /// <summary>
    /// The six tray toolbar buttons, stable id + localized label, in <c>TrayFlyoutViewModel
    /// .RebuildToolbar</c>'s own order. <c>IsShown</c> is read straight from
    /// <see cref="_flyoutHiddenTileIds"/> every time (fix round 1, RemEx-4kv0g.18.6 review, HIGH) —
    /// that field is the only source of truth, so there is nothing left in the old checklist worth
    /// preserving across a rebuild.
    /// </summary>
    private void RebuildFlyoutTiles()
    {
        foreach (var tile in FlyoutTiles) tile.ShownChanged -= OnFlyoutTileShownChanged;
        FlyoutTiles.Clear();

        foreach (var (id, labelKey) in FlyoutTileDefinitions)
        {
            var isShown = !_flyoutHiddenTileIds.Contains(id, StringComparer.OrdinalIgnoreCase);

            var choice = new FlyoutTileChoice(id, LocalizationService.Instance[labelKey], isShown);
            choice.ShownChanged += OnFlyoutTileShownChanged;
            FlyoutTiles.Add(choice);
        }
    }

    /// <summary>Stable id + the localization key <c>TrayFlyoutViewModel.RebuildToolbar</c> reads for
    /// that same tile's label - nothing new to translate, only reused.</summary>
    private static readonly (string Id, string LabelKey)[] FlyoutTileDefinitions =
    [
        ("lock", "Tray_Tile_Lock"),
        ("sleep", "Tray_Tile_Sleep"),
        ("remote", "Tray_Tile_RemoteDesktop"),
        ("send", "Tray_Tile_SendFile"),
        ("pair", "Btn_Pair"),
        ("power", "PaletteCategory_Power"),
    ];

    /// <summary>
    /// One entry per <see cref="HomeViewModel.PinnedSensors"/> entry, checked = shown in the tray
    /// flyout. A no-op when <see cref="_home"/> is null (no collaborator in this test/host).
    /// <c>IsShown</c> is read straight from <see cref="_flyoutHiddenSensorIds"/> every time (fix
    /// round 1, RemEx-4kv0g.18.6 review, HIGH) — see <see cref="RebuildFlyoutTiles"/>'s remarks for
    /// why there is nothing in the old checklist worth preserving separately.
    /// </summary>
    private void RebuildFlyoutCards()
    {
        if (_home is null) return;

        foreach (var card in FlyoutCards) card.ShownChanged -= OnFlyoutCardShownChanged;
        FlyoutCards.Clear();

        foreach (var sensor in _home.PinnedSensors)
        {
            var isShown = !_flyoutHiddenSensorIds.Contains(sensor.Name, StringComparer.OrdinalIgnoreCase);

            var choice = new FlyoutCardChoice(sensor.Name, sensor.DisplayName, isShown);
            choice.ShownChanged += OnFlyoutCardShownChanged;
            FlyoutCards.Add(choice);
        }
    }

    /// <summary>Reloads <see cref="FlyoutApps"/> from the launcher store — the view calls this on
    /// attach (<c>PersonalizationPanelView.ConfigureViewModel</c>, fix round 1 RemEx-4kv0g.18.6
    /// review HIGH 2 — the call was missing; this comment used to claim it happened and did not) so a
    /// launcher entry added or removed elsewhere shows up the next time this sheet opens, the same
    /// "reload on open" contract <c>TrayFlyoutViewModel.Refresh</c> gives its own copy of the
    /// launcher list.</summary>
    public void RefreshFlyoutApps() => _ = RefreshFlyoutAppsAsync();

    private async Task RefreshFlyoutAppsAsync()
    {
        if (_launcherStorage is null) return;

        List<AppEntry> entries;
        try
        {
            entries = await _launcherStorage.LoadEntriesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CustomizationViewModel: failed to load launcher entries for the Flyout section's Apps checklist");
            entries = [];
        }

        foreach (var app in FlyoutApps) app.ShownChanged -= OnFlyoutAppShownChanged;
        FlyoutApps.Clear();

        // isShown reads _flyoutAppIds (fix round 1, RemEx-4kv0g.18.6 review, HIGH) - an entry the
        // launcher failed to return this call (a transient LoadEntriesAsync failure, or one just not
        // loaded yet) never shows a row here, but its id is untouched in _flyoutAppIds and survives
        // any save made in the meantime; it reappears, correctly ticked, the next time this succeeds.
        foreach (var entry in entries.OrderBy(e => e.Order))
        {
            var isShown = _flyoutAppIds.Contains(entry.Id);

            var choice = new FlyoutAppChoice(entry.Id, entry.DisplayName, entry.IconBase64, isShown);
            choice.ShownChanged += OnFlyoutAppShownChanged;
            FlyoutApps.Add(choice);
        }
    }

    private void RefreshBackgroundTypes()
    {
        AvailableBackgroundTypes.Clear();
        AvailableBackgroundTypes.Add("Aurora");
        AvailableBackgroundTypes.Add("Wallpaper");
        // Mica (RemEx-rq0xl): only offered where MicaBackdrop can actually ask DWM for the
        // backdrop (Windows 11 22H2+). A persisted "Mica" anywhere else falls through the same
        // unsupported-material fallback below as any other unavailable choice.
        if (OperatingSystem.IsWindows() && MicaBackdrop.IsSupported)
        {
            AvailableBackgroundTypes.Add("Mica");
        }
        if (OperatingSystem.IsWindows())
        {
            AvailableBackgroundTypes.Add("Acrylic");
        }
        else if (OperatingSystem.IsLinux())
        {
            AvailableBackgroundTypes.Add("Glass"); // Linux Mica-like alternative
        }
        AvailableBackgroundTypes.Add("Gradient");
        AvailableBackgroundTypes.Add("Solid");

        // A mode this platform cannot offer (or a null/absent one off disk) falls back to Aurora
        // for THIS SESSION ONLY (RemEx-k7891). Going through the CanvasBackgroundType property here
        // used to persist the fallback: the generated setter's OnCanvasBackgroundTypeChanged partial
        // ends in ApplyAndSave, so opening a profile saved with Acrylic once on a platform that
        // doesn't offer it — or a hand-edited profile with BackgroundMaterial missing or null —
        // silently and permanently rewrote it to Aurora on disk. _suppressPersist keeps the setter's
        // repaint (picker and dashboard background must never disagree about what's on screen)
        // while skipping only ApplyAndSave's RequestSave call — the profile's real choice survives
        // until the user actually picks something themselves. (The backing field can't be written
        // directly outside the constructor: CommunityToolkit.Mvvm's generator forbids it, MVVMTK0034.)
        if (!AvailableBackgroundTypes.Contains(CanvasBackgroundType))
        {
            // STASHED BEFORE THE ASSIGNMENT BELOW OVERWRITES IT (RemEx-k7891 follow-up, MEDIUM). The
            // live CanvasBackgroundType reads "Aurora" from here on, but the profile's real choice —
            // possibly null — has to keep reaching ApplyAndSave for every OTHER save this session,
            // not just this one suppressed call. See _isBackgroundFallbackActive's remarks.
            _isBackgroundFallbackActive = true;
            _unsupportedPersistedMaterial = CanvasBackgroundType;

            _suppressPersist = true;
            try { CanvasBackgroundType = "Aurora"; }
            finally { _suppressPersist = false; }
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

    // ─── Saved palettes (RemEx-ddynd, spec section 7) ────────────────────────────────────────────

    /// <summary>The person's palettes, in saved order, after the built-in presets on the sheet.</summary>
    public ObservableCollection<SavedPaletteTileViewModel> SavedPalettes { get; } = new();

    /// <summary>The name "Save current" writes. Blank means "Palette N".</summary>
    [ObservableProperty]
    private string _newPaletteName = string.Empty;

    private SavedPalette CurrentAsSavedPalette(string name) => new()
    {
        Name = name,
        ColorSource = ColorSource,
        Seed = AccentColor,
        // THE SEED'S OWN CHROMA, not the slider's requested one — same reasoning and the same call
        // as RecipeFromCurrent's export path. Most hue/tone pairs cannot reach high chroma in sRGB,
        // so a request of 120 can land on a colour of 60; storing the request would mean applying
        // this tile later re-pushes 120 through HCT and reconstructs the swatch instead of
        // reproducing it exactly. ApplySavedPalette only short-circuits to the stored seed when the
        // stored vibrancy already equals the seed's own chroma — the two paths must agree.
        Vibrancy = SeedHct.ChromaOf(AccentColor, SeedChroma),
        Contrast = Math.Clamp(ThemeContrast, -1.0, 1.0),
        Strategy = SchemeVariant,
    };

    /// <summary>
    /// The smallest "Palette N" (N &gt;= 1) not already in use, not <c>Count + 1</c> — deleting a
    /// middle tile and saving again must fill the gap it left, not collide with a tile whose number
    /// still exists (e.g. delete "Palette 2" of three, blank-save should land "Palette 2" again,
    /// not a second "Palette 3").
    /// </summary>
    private string NextDefaultPaletteName()
    {
        var used = SavedPalettes.Select(t => t.Record.Name).ToHashSet(StringComparer.Ordinal);
        var n = 1;
        while (used.Contains(SavedPalette.DefaultNamePrefix + n)) n++;
        return SavedPalette.DefaultNamePrefix + n;
    }

    [RelayCommand]
    private void SaveCurrentPalette()
    {
        var name = string.IsNullOrWhiteSpace(NewPaletteName) ? NextDefaultPaletteName() : NewPaletteName.Trim();
        AddSavedPalette(CurrentAsSavedPalette(name));
        NewPaletteName = string.Empty;
        ApplyAndSave();
    }

    private SavedPaletteTileViewModel AddSavedPalette(SavedPalette palette)
    {
        var tile = new SavedPaletteTileViewModel(palette);
        tile.Renamed += _ => ApplyAndSave();
        tile.Refresh(CurrentIsLightPalette());
        SavedPalettes.Add(tile);
        return tile;
    }

    /// <summary>
    /// Applying a palette sets the same fields a preset sets. A Custom palette becomes the Custom
    /// source with that seed; a Windows-accent or Wallpaper palette re-selects that source, whose
    /// handler adopts the live system colour shaped by the palette's vibrancy (spec section 7).
    /// </summary>
    [RelayCommand]
    private void ApplySavedPalette(SavedPaletteTileViewModel tile)
    {
        var p = tile.Record;
        _isApplyingPreset = true;
        try
        {
            SchemeVariant = SchemeVariants.Normalize(p.Strategy);
            ThemeContrast = Math.Clamp(p.Contrast, -1.0, 1.0);
            AccentColor = p.Seed;      // syncs hue/chroma/tone from the seed
            SeedChroma = p.Vibrancy;   // then the saved vibrancy re-shapes it (PushSeedToAccent)
            ColorSource = p.ColorSource switch
            {
                ColorSources.WindowsAccent when AvailableColorSources.Contains(ColorSources.WindowsAccent) => ColorSources.WindowsAccent,
                ColorSources.Wallpaper when AvailableColorSources.Contains(ColorSources.Wallpaper) => ColorSources.Wallpaper,
                _ => ColorSources.Custom,
            };
        }
        finally
        {
            _isApplyingPreset = false;
        }

        // ColorSource's own handler already adopted the system seed (or, for Wallpaper, started
        // the async extraction, which saves when it lands); this is the one save for everything else.
        ApplyAndSave();
        CommitSeedToRecents();
    }

    [RelayCommand]
    private void DeleteSavedPalette(SavedPaletteTileViewModel tile)
    {
        if (!SavedPalettes.Remove(tile)) return;
        ApplyAndSave();
    }

    private void RefreshSavedPaletteTiles()
    {
        var liveIsLight = CurrentIsLightPalette();
        foreach (var tile in SavedPalettes) tile.Refresh(liveIsLight);
    }

    [ObservableProperty]
    private double _cornerRadius;

    /// <summary>The sample card's corners: the live slider value, capped like the preset tiles are.</summary>
    public CornerRadius SampleCardCornerRadius => new(Math.Clamp(CornerRadius, 0, 24));

    [ObservableProperty]
    private double _remoteCardCornerRadius;

    /// <summary>
    /// Card border thickness, in pixels. NOT a slider — no personalization control writes this
    /// directly. The only writer is <see cref="SelectTheme"/>, which copies the chosen preset's
    /// <see cref="SeedPreset.CardBorderThickness"/> the same way it copies <see cref="CornerRadius"/>.
    /// Persisted here so a per-preset theme dictionary no longer has to (RemEx-bnz2x).
    /// </summary>
    [ObservableProperty]
    private double _cardBorderThickness;

    [ObservableProperty]
    private double _glassOpacity;

    [ObservableProperty]
    private double _appWindowOpacity;

    /// <summary>
    /// Opacity of the tray flyout popup's own glass (Flyout D2 .2, RemEx-4kv0g.18.6) — independent of
    /// <see cref="GlassOpacity"/>, which only ever shapes the canvas cards. See
    /// <c>CustomizationSettings.FlyoutOpacity</c>'s own doc for the default.
    /// </summary>
    [ObservableProperty]
    private double _flyoutOpacity;

    /// <summary>
    /// The Flyout section's Cards checklist: one entry per <see cref="HomeViewModel.PinnedSensors"/>
    /// entry, rebuilt whenever that collection changes. Checked = shown in the tray flyout; toggling
    /// an entry writes <c>CustomizationSettings.FlyoutHiddenSensorIds</c> (Flyout D2 .2,
    /// RemEx-4kv0g.18.6).
    /// </summary>
    public ObservableCollection<FlyoutCardChoice> FlyoutCards { get; } = new();

    /// <summary>
    /// The Flyout section's Buttons checklist — the six tray toolbar actions, stable ids and
    /// localized labels. Toggling an entry writes <c>CustomizationSettings.FlyoutHiddenTileIds</c>
    /// (Flyout D2 .2, RemEx-4kv0g.18.6).
    /// </summary>
    public ObservableCollection<FlyoutTileChoice> FlyoutTiles { get; } = new();

    /// <summary>
    /// The Flyout section's Apps checklist — one entry per launcher entry, launcher order. Ticking an
    /// entry writes <c>CustomizationSettings.FlyoutAppIds</c>, a shown-list (Flyout D2 .2,
    /// RemEx-4kv0g.18.6).
    /// </summary>
    public ObservableCollection<FlyoutAppChoice> FlyoutApps { get; } = new();

    [ObservableProperty]
    private double _glowStrength;

    [ObservableProperty]
    private string _accentColor;

    [ObservableProperty]
    private string _schemeVariant;

    [ObservableProperty]
    private string _canvasBackgroundType;

    /// <summary>A <see cref="WallpaperSources"/> value. Persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageWallpaperSource))]
    private string _wallpaperSource = WallpaperSources.Desktop;

    /// <summary>Desktop wallpaper only where the registry can be read (Windows); Pick an image everywhere.</summary>
    public ObservableCollection<string> AvailableWallpaperSources { get; } = new();

    public bool IsImageWallpaperSource => WallpaperSource == WallpaperSources.Image;

    /// <summary>0 to 1; the shell maps it to a blur radius. Persisted.</summary>
    [ObservableProperty]
    private double _wallpaperBlur = 0.6;

    /// <summary>The app-owned copy's path, carried and replaced by <see cref="PickWallpaperImageAsync"/>.</summary>
    private string? _wallpaperImagePath;

    public bool IsWallpaperBackgroundSelected => CanvasBackgroundType == "Wallpaper";

    /// <summary>Glass and Wallpaper are the two modes the window-opacity slider shapes.</summary>
    public bool IsWindowOpacityRelevant => CanvasBackgroundType is "Glass" or "Wallpaper";

    partial void OnWallpaperSourceChanged(string value) => ApplyAndSave();

    partial void OnWallpaperBlurChanged(double value) => ApplyAndSave();

    /// <summary>Pick an image: copy it under the per-user directory, downscaled; on failure keep the
    /// previous image, say so, and write nothing (spec section 9).</summary>
    [RelayCommand]
    private async Task PickWallpaperImageAsync()
    {
        if (PickOpenFileAsync is null) return;

        var files = await PickOpenFileAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Custom_ChooseWallpaperImage"],
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        if (files.Count == 0) return;

        var source = files[0].TryGetLocalPath();
        if (source is null) return;

        var directory = WallpaperImageStore.DirectoryFor(RemexDataPaths.PerUserDirectory);
        var (ok, copy) = await Task.Run(() =>
        {
            var success = WallpaperImageStore.TryCopyDownscaled(source, directory, out var path);
            return (success, path);
        });

        if (!ok || copy is null)
        {
            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Custom_ChooseWallpaperImage"],
                LocalizationService.Instance["Custom_WallpaperImageCopyFailed"]);
            return;
        }

        var wasImage = WallpaperSource == WallpaperSources.Image;
        var previous = _wallpaperImagePath;
        _wallpaperImagePath = copy;
        WallpaperSource = WallpaperSources.Image;   // saves through OnWallpaperSourceChanged when the source actually changes
        if (wasImage && previous != copy) ApplyAndSave(); // already Image: the setter is a no-op, so save explicitly here
        WallpaperImageStore.TryDeleteCopy(previous, directory);
    }

    [ObservableProperty]
    private string _splashStyle;

    partial void OnSplashStyleChanged(string value) => ApplyAndSave();

    public ObservableCollection<string> AvailableSplashStyles { get; } = new()
    {
        "RemexCommand", "CosmicZoom", "Pong"
    };

    /// <summary>Plays the selected splash over the shell (spec section 8).</summary>
    [RelayCommand]
    private void PreviewSplash() => _shell.ReplayWelcomeSplash();

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

    /// <summary>
    /// The selected page-subtitle font (RemEx-n6csl); persisted as its <see cref="FontOption.Value"/>.
    /// Loaded as the effective font (the title font when the profile has none), so the picker never
    /// shows an empty selection for an upgraded profile.
    /// </summary>
    [ObservableProperty]
    private FontOption? _selectedPageSubtitleFont;

    partial void OnSelectedPageSubtitleFontChanged(FontOption? value)
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

    // ─── Personalize → Text (RemEx-jt6w5) ─────────────────────────────────────────────────────────
    // Each generated setter applies live through ApplyAndSave, exactly like UiScale above; the values
    // reach ThemeService.ApplyCustomizationCore → TypographyService with the rest of the record. The
    // reset command raises _isResettingTypography so its thirteen assignments produce ONE apply-and-save.

    /// <summary>Headers size multiplier (0.80–1.60): Headline5/6, Subtitle1, page-title, card-title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadersSizeLabel))]
    private double _headersScale = 1.0;

    partial void OnHeadersScaleChanged(double value) => ApplyTypographyChange();

    /// <summary>Subtitles size multiplier: the page-subtitle line under each page title (RemEx-n6csl).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitlesSizeLabel))]
    private double _subtitlesScale = 1.0;

    partial void OnSubtitlesScaleChanged(double value) => ApplyTypographyChange();

    /// <summary>Body size multiplier: Body2 and untagged text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodySizeLabel))]
    private double _bodyScale = 1.0;

    partial void OnBodyScaleChanged(double value) => ApplyTypographyChange();

    /// <summary>Small text size multiplier: Caption and Overline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmallSizeLabel))]
    private double _smallScale = 1.0;

    partial void OnSmallScaleChanged(double value) => ApplyTypographyChange();

    /// <summary>Sensor text size multiplier: card titles and dual-metric names.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorSizeLabel))]
    private double _sensorScale = 1.0;

    partial void OnSensorScaleChanged(double value) => ApplyTypographyChange();

    [ObservableProperty]
    private bool _headersBold;

    partial void OnHeadersBoldChanged(bool value) => ApplyTypographyChange();

    [ObservableProperty]
    private bool _subtitlesBold;

    partial void OnSubtitlesBoldChanged(bool value) => ApplyTypographyChange();

    [ObservableProperty]
    private bool _bodyBold;

    partial void OnBodyBoldChanged(bool value) => ApplyTypographyChange();

    [ObservableProperty]
    private bool _smallBold;

    partial void OnSmallBoldChanged(bool value) => ApplyTypographyChange();

    [ObservableProperty]
    private bool _sensorBold;

    partial void OnSensorBoldChanged(bool value) => ApplyTypographyChange();

    /// <summary>The value-pill treatment behind sensor-card titles.</summary>
    [ObservableProperty]
    private bool _sensorTitleBackdrop;

    partial void OnSensorTitleBackdropChanged(bool value) => ApplyTypographyChange();

    /// <summary>The legibility halo switch; governs every section.</summary>
    [ObservableProperty]
    private bool _textShadowEnabled = true;

    partial void OnTextShadowEnabledChanged(bool value) => ApplyTypographyChange();

    /// <summary>Halo strength 0–100. A double because Slider.Value is one; rounded to an int on save.</summary>
    [ObservableProperty]
    private double _textShadowStrength = 40;

    partial void OnTextShadowStrengthChanged(double value) => ApplyTypographyChange();

    /// <summary>"default → effective" for the section's reference member (Headline6 = 20 pt).</summary>
    public string HeadersSizeLabel => SizeLabel(TypographySection.Headers, HeadersScale);

    /// <summary>Reference member PageSubtitle = 12 pt.</summary>
    public string SubtitlesSizeLabel => SizeLabel(TypographySection.Subtitles, SubtitlesScale);

    /// <summary>Reference member Body2 = 14 pt.</summary>
    public string BodySizeLabel => SizeLabel(TypographySection.Body, BodyScale);

    /// <summary>Reference member Caption = 12 pt.</summary>
    public string SmallSizeLabel => SizeLabel(TypographySection.Small, SmallScale);

    /// <summary>Reference member SensorTitle = 12 pt.</summary>
    public string SensorSizeLabel => SizeLabel(TypographySection.Sensor, SensorScale);

    private static string SizeLabel(TypographySection section, double scale) =>
        string.Format(
            LocalizationService.Instance["Custom_TextSizeLabelFormat"],
            TypographyResolver.ReferencePoints(section),
            TypographyResolver.EffectivePoints(section, scale));

    private bool _isResettingTypography;

    private void ApplyTypographyChange()
    {
        if (!_isResettingTypography) ApplyAndSave();
    }

    /// <summary>The Text section as the sheet currently shows it, for <see cref="BuildCurrentSettings"/>.</summary>
    private TypographySettings BuildTypographySettings() => TypographySettings.Normalize(new TypographySettings
    {
        HeadersScale = HeadersScale,
        SubtitlesScale = SubtitlesScale,
        BodyScale = BodyScale,
        SmallScale = SmallScale,
        SensorScale = SensorScale,
        HeadersBold = HeadersBold,
        SubtitlesBold = SubtitlesBold,
        BodyBold = BodyBold,
        SmallBold = SmallBold,
        SensorBold = SensorBold,
        SensorTitleBackdrop = SensorTitleBackdrop,
        ShadowEnabled = TextShadowEnabled,
        ShadowStrength = (int)Math.Round(TextShadowStrength, MidpointRounding.AwayFromZero),
    });

    /// <summary>One click, no confirmation: every field of the Text section back to the spec table, one save.</summary>
    [RelayCommand]
    private void ResetTextToDefaults()
    {
        var defaults = TypographySettings.Default;
        _isResettingTypography = true;
        try
        {
            HeadersScale = defaults.HeadersScale;
            SubtitlesScale = defaults.SubtitlesScale;
            BodyScale = defaults.BodyScale;
            SmallScale = defaults.SmallScale;
            SensorScale = defaults.SensorScale;
            HeadersBold = defaults.HeadersBold;
            SubtitlesBold = defaults.SubtitlesBold;
            BodyBold = defaults.BodyBold;
            SmallBold = defaults.SmallBold;
            SensorBold = defaults.SensorBold;
            SensorTitleBackdrop = defaults.SensorTitleBackdrop;
            TextShadowEnabled = defaults.ShadowEnabled;
            TextShadowStrength = defaults.ShadowStrength;
        }
        finally
        {
            _isResettingTypography = false;
        }

        ApplyAndSave();
    }

    /// <summary>Flags any selected font that won't materialize, so the user gets a plain-English heads-up.</summary>
    private void ValidateFonts()
    {
        var unavailable = new System.Collections.Generic.List<string>();
        if (SelectedPageTitleFont is { } title && !SystemFontService.TryResolveFont(title.Value, out _))
            unavailable.Add(title.DisplayName);
        if (SelectedPageSubtitleFont is { } subtitle && !SystemFontService.TryResolveFont(subtitle.Value, out _))
            unavailable.Add(subtitle.DisplayName);
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
        OnPropertyChanged(nameof(SampleCardCornerRadius));
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
    /// <summary>Clamped on set (fix round 1, RemEx-4kv0g.18.6 review, LOW), not only on load/save -
    /// re-assigns through the generated setter with the clamped value when out of range, which
    /// re-enters this same handler once (now in range) and saves exactly once.</summary>
    partial void OnFlyoutOpacityChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        if (clamped != value)
        {
            FlyoutOpacity = clamped;
            return;
        }

        ApplyAndSave();
    }
    partial void OnGlowStrengthChanged(double value) => ApplyAndSave();
    partial void OnAccentColorChanged(string value)
    {
        // Order matters: the sliders have to be showing the new seed before the repaint, or a swatch
        // click leaves the wheel's thumb sitting on the colour the user just replaced.
        SyncSeedFromAccent();
        ApplyAndSave();
    }
    partial void OnSchemeVariantChanged(string value)
    {
        UpdateSchemeVariantSelection();
        ApplyAndSave();
    }
    partial void OnCanvasBackgroundTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGlassModeSelected));
        OnPropertyChanged(nameof(IsWallpaperBackgroundSelected));
        OnPropertyChanged(nameof(IsWindowOpacityRelevant));

        // A GENUINE PICK CLEARS THE STASHED FALLBACK (RemEx-k7891 follow-up). _suppressPersist is
        // only ever true for RefreshBackgroundTypes' own platform-fallback assignment; every other
        // write to this property — including the user's own pick in the ComboBox — means they chose
        // something, so whatever platform-unsupported (or null) material this session started with
        // no longer needs protecting from the next save.
        if (!_suppressPersist)
        {
            _isBackgroundFallbackActive = false;
            _unsupportedPersistedMaterial = null;
        }

        ApplyAndSave();
    }

    /// <summary>
    /// The reduced-motion preference, so it can sit with the other personalisation toggles.
    /// </summary>
    /// <remarks>
    /// A view of <see cref="ShellViewModel.IsReducedMotion"/> rather than a second copy of it. The
    /// preference lives on the profile itself, not in <c>Customization</c>, and the shell already
    /// persists it when it changes — so this deliberately does not go through
    /// <see cref="ApplyAndSave"/>, which would write the customization record for a value that is
    /// not part of it. Until this bead it had no control at all: the value was read from the profile
    /// at startup and there was no way for anyone to set it (RemEx-yzu5m).
    /// </remarks>
    public bool IsReducedMotion
    {
        get => _shell.IsReducedMotion;
        set
        {
            if (_shell.IsReducedMotion == value)
            {
                return;
            }

            _shell.IsReducedMotion = value;
            OnPropertyChanged();
        }
    }

    public bool IsGlassModeSelected => CanvasBackgroundType == "Glass";

    /// <summary>
    /// A view of <see cref="ShellViewModel.IsPaletteDragging"/>, set by the seed wheel's own
    /// <c>IsDragging</c> (bound <c>Mode=OneWayToSource</c> in PersonalizationPanelView.axaml). Every
    /// frame of a drag calls <see cref="ApplyAndSave"/> through <see cref="PushSeedToAccent"/>, and
    /// this is what tells the crossfade in App.axaml / DashboardBackgroundControl to sit out those
    /// frames rather than restart on each one and fall behind the pointer (RemEx-zgtn1). Same
    /// shell-proxy shape as <see cref="IsReducedMotion"/> above — the flag lives on the shell because
    /// that is what the window and the backdrop are bound to, not this screen.
    /// </summary>
    /// <remarks>
    /// THE GETTER RAISES NO CHANGE NOTIFICATION OF ITS OWN, and that is sound only while the wheel's
    /// <c>OneWayToSource</c> binding is the single writer of <c>_shell.IsPaletteDragging</c> — the
    /// binding pushes into this setter, so nothing needs to be told about a value it just wrote
    /// (review LOW, RemEx-zgtn1). If a second writer ever appears — a keyboard seed nudge, a
    /// hardware injection marking itself as a drag — this property has to subscribe to the shell,
    /// or it will silently report a stale value to anything that binds it.
    /// </remarks>
    public bool IsSeedDragging
    {
        get => _shell.IsPaletteDragging;
        set
        {
            if (_shell.IsPaletteDragging == value) return;
            _shell.IsPaletteDragging = value;
            OnPropertyChanged();
        }
    }

    private void ApplyAndSave()
    {
        if (_isApplyingPreset) return;

        var settings = BuildCurrentSettings();

        // Use the internal setter if possible, or request a save — the LIVE paint always uses
        // `settings` verbatim (the fallback, while one is showing), never the substitution below.
        _themeService.ApplyCustomization(settings);

        // Every live change refreshes the splash's last-seed sidecar (RemEx-alwfa.1, decision (b)),
        // not only a persisted one — `settings` is the seed actually painting the shell right now,
        // which is what the NEXT launch's splash should match. Fire-and-forget: a failed sidecar
        // write must never block or fail this method, and WriteAsync already logs its own failures.
        LastSeedSidecar.WriteAsync(settings, _logger).FireAndForget("write the last-seed splash sidecar", _logger);

        // _suppressPersist SKIPS THIS WHOLE BLOCK (RemEx-k7891). RefreshBackgroundTypes' platform
        // fallback needs the repaint above — and the preset/tile refresh below — to run exactly as a
        // real pick's would, so the picker and the dashboard background never disagree about what's
        // on screen. What it must never do is write the fallback over the profile's real, persisted
        // BackgroundMaterial, which is exactly what RequestSave does.
        if (!_suppressPersist)
        {
            // RemEx-k7891 FOLLOW-UP (Opus review, MEDIUM). _suppressPersist alone only covers the
            // fallback's OWN suppressed call above — but CanvasBackgroundType stays "Aurora" after
            // it, so THIS save (triggered by something else entirely — a hardware-accent sync, an
            // unrelated slider) would otherwise persist that displayed fallback over the real,
            // unsupported-here material. While _isBackgroundFallbackActive is set, substitute the
            // stashed original back in for the persisted copy only; the live `settings` above (and
            // therefore the on-screen paint) is untouched.
            var toPersist = _isBackgroundFallbackActive
                ? settings with { BackgroundMaterial = _unsupportedPersistedMaterial! }
                : settings;
            var profile = _layoutService.CurrentProfile with { Customization = toPersist };
            _layoutService.RequestSave(profile);
        }

        // The gallery is downstream of the same settings the shell is, so it repaints here rather
        // than from four separate change handlers that would each have to remember to.
        RefreshPresetPreviews(onlyVarying: true);
        RefreshSchemeVariantStrips();
        RefreshSavedPaletteTiles();
    }

    /// <summary>
    /// Builds the <see cref="CustomizationSettings"/> record from the view model's current live
    /// values, for <see cref="ApplyAndSave"/> to apply and (unless <see cref="_suppressPersist"/>) persist.
    /// </summary>
    /// <remarks>
    /// EVERY FIELD THIS SCREEN DOES NOT OWN HAS TO BE CARRIED FORWARD BY HAND. Building a fresh
    /// record and assigning only what the sliders bind to silently resets the rest to their
    /// defaults on the next save — which is why ThemeContrast has been persisted, reloaded and
    /// then wiped on the first customization change since it was added, and why "the contrast
    /// setting does nothing" (RemEx-68ynp) was true in two independent places at once.
    /// CustomizationSettingsRoundTripTests fails if a new field is added and not listed here.
    /// </remarks>
    private CustomizationSettings BuildCurrentSettings()
    {
        var carried = _layoutService.CurrentProfile.Customization;

        return new CustomizationSettings
        {
            // WITHOUT THIS LINE THE MIGRATION RE-RUNS ON EVERY LAUNCH. The record's default is 0,
            // which means "written before the seed engine", so a save that forgets to stamp the
            // version hands the next startup a profile that looks legacy - and the legacy arm
            // adopts the preset's variant and contrast over the user's, silently, forever
            // (RemEx-dbkzy).
            SchemaVersion = CustomizationMigration.CurrentSchemaVersion,

            ThemeId = SelectedPresetId,
            ThemeContrast = Math.Clamp(ThemeContrast, -1.0, 1.0),

            // THE SEED'S OWN CHROMA, not the slider's requested one. Most hue/tone pairs cannot
            // reach high chroma in sRGB, so a request of 120 can land on a colour of 60 — writing
            // the request would persist a number the seed does not have. Writing what it achieved
            // means Hct.From(hue, ThemeSeedChroma, tone) reproduces this exact seed, which is the
            // property the Android side needs for the two platforms to agree (RemEx-ndhlv).
            ThemeSeedChroma = SeedHct.ChromaOf(AccentColor, carried.ThemeSeedChroma),
            // THE RAW ASK, ALONGSIDE THE ACHIEVED VALUE ABOVE (RemEx-ceu4x). Without this,
            // ColorSourceCoordinator.ShapedBySource had nothing to request but the achieved chroma
            // above, and achieved <= requested means every accent change whose hue could not hold
            // it ratcheted Vibrancy down permanently. SeedChroma is the slider's own live value, so
            // this is exactly what the studio is showing right now, independent of what the current
            // seed could reach.
            ThemeSeedChromaRequest = SeedChroma,
            // SUPERSEDED FIELD CARRIED VERBATIM, MODE WRITTEN INSTEAD (RemEx-zk5bc). UseLightPalette
            // is a migration input now; writing new values to it would recreate the two-fields-that-
            // can-disagree trap the mode exists to end.
            UseLightPalette = carried.UseLightPalette,
            ThemeMode = _themeModeChosenThisSession ? _themeMode : carried.ThemeMode,
            CornerRadius = CornerRadius,
            RemoteCardCornerRadius = RemoteCardCornerRadius,
            CardBorderThickness = CardBorderThickness,
            GlassOpacity = GlassOpacity,
            AppWindowOpacity = AppWindowOpacity,
            GlowStrength = GlowStrength,
            AccentColor = AccentColor,
            SchemeVariant = SchemeVariant,
            BackgroundMaterial = CanvasBackgroundType,
            // Carried, not surfaced: the toggle and its view-model property went in RemEx-dbjfy, but
            // ApplyAndSave builds a fresh record, so an unassigned field would silently reset a stored
            // true to false on the next save. Preserve what the profile already holds (the round-trip
            // guard in CustomizationSettingsRoundTripTests insists on it); see
            // DashboardProfile.SyncWithHardware for why the field itself stays.
            SyncWithHardware = carried.SyncWithHardware,
            SplashStyle = SplashStyle,
            PageTitleFontFamily = SelectedPageTitleFont?.Value ?? "avares://Remex.Desktop/Assets/Fonts#Orbitron",
            // No literal fallback: null stays null and keeps following the title font (RemEx-n6csl).
            PageSubtitleFontFamily = SelectedPageSubtitleFont?.Value,
            CardHeaderFontFamily = carried.CardHeaderFontFamily,
            BodyFontFamily = SelectedBodyFont?.Value ?? "avares://Avalonia.Fonts.Inter/Assets#Inter",
            UiScale = UiScale,
            // Carried from the shell, not owned by this screen: the sheet's resize grip lives on
            // ShellView and ShellViewModel commits it on drag end (RemEx-vkkcq). Listed here so a
            // Personalize save does not reset it to null (the round-trip guard insists on it).
            // The shell is optional here (tests build this view-model without one), in which case
            // the stored value passes through untouched.
            PersonalizeSheetWidth = _shell?.PersonalizeSheetWidth ?? carried.PersonalizeSheetWidth,
            CustomAccentColors = CustomAccentColors.Take(MaxRecentSeeds).ToList(),
            // Task 1 carries these forward verbatim; Tasks 3, 5 and 7 replace each `carried.X`
            // with the view model's own live value as the sheet gains the control for it.
            ColorSource = ColorSource,
            WallpaperSeedIndex = WallpaperSeedIndex,
            WallpaperSource = WallpaperSource,
            WallpaperImagePath = _wallpaperImagePath,
            WallpaperBlur = Math.Clamp(WallpaperBlur, 0.0, 1.0),
            SavedPalettes = SavedPalettes.Select(t => t.Record).ToList(),
            Typography = BuildTypographySettings(),
            // Flyout D2 .2 (RemEx-4kv0g.18.6). WRITTEN FROM THE FIELDS, NEVER FROM THE CHECKLISTS
            // (fix round 1, review HIGH) - FlyoutCards/FlyoutTiles/FlyoutApps are a VIEW that may be
            // empty or partial at the moment of an unrelated save (_home/_launcherStorage are
            // optional, RefreshFlyoutAppsAsync is async); deriving a save from them is the
            // preset-wipe shape. _flyoutHiddenSensorIds/_flyoutHiddenTileIds/_flyoutAppIds are the
            // authoritative copies - see their own doc comments.
            FlyoutOpacity = Math.Clamp(FlyoutOpacity, 0.0, 1.0),
            FlyoutHiddenSensorIds = new List<string>(_flyoutHiddenSensorIds),
            FlyoutHiddenTileIds = new List<string>(_flyoutHiddenTileIds),
            FlyoutAppIds = new List<Guid>(_flyoutAppIds),
        };
    }

    /// <summary>Clicking a variant strip is choosing a variant — the same live-apply path the old ComboBox used.</summary>
    [RelayCommand]
    private void SelectSchemeVariant(string variant)
    {
        if (string.IsNullOrEmpty(variant)) return;
        SchemeVariant = variant;
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
            CardBorderThickness = preset.CardBorderThickness;
            GlowStrength = preset.GlowStrength;
            GlassOpacity = preset.GlassOpacity;

            // THE NULLS ARE THE POINT, not an oversight. Dynamic declines to choose a seed, a
            // variant, a mode and a contrast, and "declines" has to mean the existing value survives
            // - a preset that means "my own colour" cannot be the one that overwrites it.
            if (preset.Seed is { } seed) { ColorSource = ColorSources.Custom; AccentColor = seed; }
            if (preset.SchemeVariant is { } variant) SchemeVariant = variant;
            if (preset.IsLight is { } light) SetThemeMode(light ? ThemeModes.Light : ThemeModes.Dark);
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
    /// Rebuilds every variant strip's palette (each in its OWN variant, always — unlike the preset
    /// gallery there is no "pinned" strip to skip) and the ramp preview for the currently-selected
    /// variant. Seven strips plus one ramp is cheap enough to run on every seed/mode/contrast tick.
    /// </summary>
    private void RefreshSchemeVariantStrips()
    {
        var liveIsLight = CurrentIsLightPalette();
        foreach (var strip in SchemeVariantStrips)
            strip.Refresh(AccentColor, liveIsLight, ThemeContrast);

        TonalRamp.Refresh(AccentColor, SchemeVariant, liveIsLight, ThemeContrast);
    }

    private void UpdateSchemeVariantSelection()
    {
        var matched = false;
        foreach (var strip in SchemeVariantStrips)
        {
            strip.IsSelected = string.Equals(strip.Variant, SchemeVariant, StringComparison.Ordinal);
            matched |= strip.IsSelected;
        }

        // An unrecognised variant string assigned at runtime must still light up a strip (review
        // LOW, RemEx-lrxyo). A persisted string is normalised at construction now
        // (SchemeVariants.Normalize), so this fallback only fires for a value assigned at runtime
        // rather than for anything that came off disk.
        if (!matched)
        {
            var fallback = SchemeVariantStrips.FirstOrDefault(
                s => string.Equals(s.Variant, SchemeVariants.TonalSpot, StringComparison.Ordinal));
            if (fallback is not null) fallback.IsSelected = true;
        }
    }

    /// <summary>
    /// What light/dark the profile is actually painting right now — the session's mode choice if
    /// there is one, otherwise the stored mode, resolving System against the OS the same way
    /// <see cref="ThemeService.ResolveIsLight"/> does. The tiles have to agree with the window
    /// behind them.
    /// </summary>
    private bool CurrentIsLightPalette()
    {
        var mode = _themeModeChosenThisSession
            ? _themeMode
            : _layoutService.CurrentProfile.Customization.ThemeMode;
        return mode switch
        {
            ThemeModes.Light => true,
            ThemeModes.Dark => false,
            ThemeModes.System => ThemeService.TryGetOsIsLight() ?? false,
            // Hand-edited null mode: the picker index was initialised from the legacy chain, so it
            // is the same answer ResolveIsLight's fallback would give.
            _ => ThemeModeIndex == 0,
        };
    }

    private void UpdatePresetSelection()
    {
        foreach (var tile in ThemePresets)
        {
            tile.IsSelected = string.Equals(tile.Id, SelectedPresetId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Clicking a recently-used seed under the Custom source. The only seed setter left
    /// on the sheet, and it lives inside the Custom source on purpose (spec section 1).</summary>
    [RelayCommand]
    private void SetAccent(string hex) => AccentColor = hex;

    // ─── Colour source (RemEx-ddynd) ─────────────────────────────────────────────────────────────
    //
    // ONE SEED, ONE PATH. AccentColor stays the seed for every source; the source only decides who
    // writes it. Windows accent and Wallpaper hand over a hue and a tone; the Vibrancy slider
    // (SeedChroma) stays the chroma; PushSeedToAccent recombines the three exactly as a wheel drag
    // does. Custom lets the person write all three.

    /// <summary>A <see cref="ColorSources"/> value. Persisted; the picker binds it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowsAccentSource))]
    [NotifyPropertyChangedFor(nameof(IsWallpaperSource))]
    [NotifyPropertyChangedFor(nameof(IsCustomSource))]
    private string _colorSource = ColorSources.Custom;

    /// <summary>The sources this platform can offer, in picker order. Windows: all three; Linux: Custom only —
    /// the seed extraction in SystemSeedSources is Windows-only (spec section 9).</summary>
    public ObservableCollection<string> AvailableColorSources { get; } = new();

    public bool IsWindowsAccentSource => ColorSource == ColorSources.WindowsAccent;
    public bool IsWallpaperSource => ColorSource == ColorSources.Wallpaper;
    public bool IsCustomSource => ColorSource == ColorSources.Custom;

    /// <summary>The raw Windows accent as read from the registry, for the swatch. Null when unavailable.</summary>
    [ObservableProperty]
    private string? _sourceAccentHex;

    /// <summary>Which extracted wallpaper candidate is in use. Persisted.</summary>
    [ObservableProperty]
    private int _wallpaperSeedIndex;

    /// <summary>Whether the "from this PC" sources exist on this platform.</summary>
    public bool IsSystemSeedAvailable => OperatingSystem.IsWindows();

    /// <summary>The wallpaper's top seed candidates, best first, as hex strings for the swatch template.</summary>
    public ObservableCollection<string> WallpaperSeedCandidates { get; } = new();

    public bool HasWallpaperSeedCandidates => WallpaperSeedCandidates.Count > 0;

    partial void OnColorSourceChanged(string value)
    {
        switch (value)
        {
            case ColorSources.WindowsAccent:
                SourceAccentHex = SystemSeedSources.TryGetWindowsAccent();
                // A missing accent (a stripped-down Windows can lack the key) leaves the seed alone
                // and still records the choice, so the coordinator picks it up if the key appears.
                if (SourceAccentHex is { } hex) AdoptSourceSeed(hex);
                else ApplyAndSave();
                break;
            case ColorSources.Wallpaper:
                _ = RefreshWallpaperSeedsAsync();
                break;
            default:
                ApplyAndSave();
                break;
        }
    }

    /// <summary>The stored index, or 0 when the candidate list no longer reaches it.</summary>
    internal static int ResolveWallpaperSeedIndex(int stored, int count) =>
        stored >= 0 && stored < count ? stored : 0;

    /// <summary>
    /// Takes hue and tone from a source colour, keeps the Vibrancy slider's chroma, and recombines
    /// them into <see cref="AccentColor"/> through the same path a wheel drag uses.
    /// </summary>
    private void AdoptSourceSeed(string hex)
    {
        if (!Color.TryParse(hex, out var source)) return;

        var (hue, _, tone) = SeedHct.FromColor(source);
        _isSyncingSeed = true;
        try
        {
            SeedHue = hue;
            SeedTone = tone;
        }
        finally
        {
            _isSyncingSeed = false;
        }

        PushSeedToAccent();
    }

    /// <summary>
    /// Whether a phone has synced a palette over the link (RemEx-sudp8). Read manually rather than
    /// via <c>[NotifyPropertyChangedFor]</c> because the underlying value lives in
    /// <see cref="IPhoneThemeSnapshotStore"/>, not an <c>[ObservableProperty]</c> on this view model
    /// — the <see cref="_onPhoneThemeChanged"/> handler raises the change manually, the same shape
    /// <see cref="HasWallpaperSeedCandidates"/> uses for a collection's <c>Count</c>.
    /// </summary>
    public bool HasPhoneTheme => _phoneThemeStore?.Latest is not null;

    /// <summary>
    /// Applies the phone's last-synced palette through the normal seed pipeline, exactly like
    /// <see cref="SelectTheme"/> and <see cref="ApplySavedPalette"/>: batched under
    /// <see cref="_isApplyingPreset"/> so each field write's own <c>ApplyAndSave</c> is a no-op,
    /// then one explicit <see cref="ApplyAndSave"/> and <see cref="CommitSeedToRecents"/> once the
    /// batch is over.
    /// </summary>
    [RelayCommand]
    private void MatchPhoneTheme()
    {
        var snapshot = _phoneThemeStore?.Latest;
        if (snapshot is null) return;

        var (seedHex, schemeVariant, mode, contrast) = TryMapPhoneTheme(snapshot);

        _isApplyingPreset = true;
        try
        {
            // SAME SHAPE AS SelectTheme: a null means "leave this axis alone", so ColorSource only
            // moves to Custom paired with an actual seed to show — a malformed hex changes nothing,
            // including the source picker, rather than switching to Custom with no new colour to show.
            if (seedHex is not null) { ColorSource = ColorSources.Custom; AccentColor = seedHex; }
            if (schemeVariant is not null) SchemeVariant = schemeVariant;
            if (mode is not null) SetThemeMode(mode);
            if (contrast is not null) ThemeContrast = contrast.Value;
        }
        finally
        {
            _isApplyingPreset = false;
        }

        ApplyAndSave();
        CommitSeedToRecents();
    }

    /// <summary>The nine Android style names, exactly as <c>SettingsManager</c> stores them.</summary>
    private static readonly string[] KnownPhoneStyles =
    [
        SchemeVariants.TonalSpot, SchemeVariants.Vibrant, SchemeVariants.Expressive,
        SchemeVariants.Rainbow, SchemeVariants.FruitSalad, SchemeVariants.Neutral, SchemeVariants.Monochrome,
        SchemeVariants.Fidelity, SchemeVariants.Content,
    ];

    /// <summary>
    /// Maps a <see cref="PhoneThemeSnapshot"/> onto the four axes <see cref="MatchPhoneTheme"/>
    /// writes. Internal and static so <c>PhoneThemeMappingTests</c> can pin the table without a
    /// constructed view model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// STYLE NEVER RETURNS NULL. Android's style name arrives snake_case
    /// (<c>tonal_spot</c>) against the PC's PascalCase constants (<c>TonalSpot</c>), so the
    /// comparison strips underscores and ignores case rather than requiring an exact match; anything
    /// that still doesn't land on one of the nine — <c>spritz</c> is still Android-internal, not a
    /// user-facing style choice — maps to
    /// <see cref="SchemeVariants.TonalSpot"/>, per the wire contract. This is a DIFFERENT rule from
    /// <see cref="SchemeVariants.Normalize"/>, which maps a legacy PERSISTED PC string's own
    /// "Spritz" to <see cref="SchemeVariants.Neutral"/> — that rule is about this app's old on-disk
    /// names, not the phone's wire vocabulary, and reusing it here would send Android's actual
    /// <c>neutral</c> down the same path as its very different <c>spritz</c> edge case.
    /// </para>
    /// <para>
    /// CONTRAST IS RANGE-GUARDED, NOT FLOORED. Both ends run MCU's ContrastCurves over -1.0..1.0
    /// (RemEx-4kv0g), so the phone's signed value is applied as-is; only values outside the range are
    /// clamped to it, matching the load-time clamp at line 652.
    /// </para>
    /// <para>
    /// SEED AND MODE CAN BOTH BE NULL: an unparseable hex leaves <see cref="AccentColor"/> untouched
    /// (mirrors <see cref="SyncSeedFromAccent"/>'s own refusal to collapse a bad string to black),
    /// and a mode outside <c>light</c>/<c>dark</c>/<c>system</c> leaves the mode picker untouched —
    /// the wire contract does not define a fallback for either.
    /// </para>
    /// </remarks>
    internal static (string? SeedHex, string? SchemeVariant, string? Mode, double? Contrast) TryMapPhoneTheme(
        PhoneThemeSnapshot snapshot)
    {
        // NULL-SAFE READS, NOT JUST NULLABLE ANNOTATIONS (RemEx-sudp8 fix round). PhoneThemeSnapshot's
        // wire properties are no longer `required`, and System.Text.Json fills an absent record
        // property with a genuine runtime null - bypassing the property's own `= string.Empty`
        // initializer, which only applies to a plain C# construction. Color.TryParse(null, ...) and
        // string.Replace on a null reference both throw, and this method has no host-side validator
        // in front of it the way PingPongHandler.IsValidThemeSync does on the write side.
        string? seedHex = snapshot.SeedHex is { } seedIn && Color.TryParse(seedIn, out _) ? seedIn : null;

        var normalizedStyle = (snapshot.Style ?? string.Empty).Replace("_", "", StringComparison.Ordinal);
        var schemeVariant = SchemeVariants.TonalSpot;
        foreach (var known in KnownPhoneStyles)
        {
            if (!string.Equals(known, normalizedStyle, StringComparison.OrdinalIgnoreCase)) continue;
            schemeVariant = known;
            break;
        }

        string? mode = snapshot.Mode switch
        {
            "light" => ThemeModes.Light,
            "dark" => ThemeModes.Dark,
            "system" => ThemeModes.System,
            _ => null,
        };

        double? contrast = double.IsNaN(snapshot.Contrast) ? null : Math.Clamp(snapshot.Contrast, -1.0, 1.0);

        return (seedHex, schemeVariant, mode, contrast);
    }

    /// <summary>A candidate swatch click: remember which one, and adopt it.</summary>
    [RelayCommand]
    private void SelectWallpaperSeed(string hex)
    {
        var index = WallpaperSeedCandidates.IndexOf(hex);
        if (index < 0) return;
        WallpaperSeedIndex = index;
        AdoptSourceSeed(hex);
    }

    /// <summary>Re-extracts the candidates (the Refresh action, and every switch to the Wallpaper source).</summary>
    [RelayCommand]
    private Task RefreshWallpaperSeeds() => RefreshWallpaperSeedsAsync();

    private async Task RefreshWallpaperSeedsAsync()
    {
        // Decode + quantize + score is CPU-bound and a 4K wallpaper is a real file — off the UI
        // thread, the same rule the palette solve follows.
        var seeds = await Task.Run(SystemSeedSources.ExtractWallpaperSeeds);

        WallpaperSeedCandidates.Clear();
        foreach (var seed in seeds) WallpaperSeedCandidates.Add(seed);
        OnPropertyChanged(nameof(HasWallpaperSeedCandidates));

        // The list may be shorter than it was when the index was stored (the wallpaper changed):
        // the first candidate is used and the index reset (spec section 5).
        WallpaperSeedIndex = ResolveWallpaperSeedIndex(WallpaperSeedIndex, seeds.Count);
        if (seeds.Count > 0 && IsWallpaperSource) AdoptSourceSeed(seeds[WallpaperSeedIndex]);
        else if (IsWallpaperSource) ApplyAndSave();
    }

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
        foreach (var strip in SchemeVariantStrips) strip.Dispose();
        SchemeVariantStrips.Clear();
        // Tiles hold no subscriptions to singletons; clearing just drops the Renamed handlers that
        // close over this view model.
        SavedPalettes.Clear();
        _themeService.CustomizationApplied -= _onCustomizationApplied;
        if (_phoneThemeStore is not null) _phoneThemeStore.Changed -= _onPhoneThemeChanged;
        if (_home is not null && _onPinnedSensorsChanged is not null) _home.PinnedSensors.CollectionChanged -= _onPinnedSensorsChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }
}

/// <summary>One row in the Flyout section's Cards checklist (Flyout D2 .2, RemEx-4kv0g.18.6) — a
/// subset of <see cref="HomeViewModel.PinnedSensors"/>, checked = shown in the tray flyout.</summary>
public partial class FlyoutCardChoice : ObservableObject
{
    /// <summary><see cref="Remex.Desktop.ViewModels.SensorViewModel.Name"/> — the same id
    /// <c>CustomizationSettings.FlyoutHiddenSensorIds</c> and <c>DashboardProfile.PinnedSensorIds</c>
    /// use.</summary>
    public string SensorId { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isShown;

    public event EventHandler<bool>? ShownChanged;

    public FlyoutCardChoice(string sensorId, string displayName, bool isShown)
    {
        SensorId = sensorId;
        DisplayName = displayName;
        _isShown = isShown;
    }

    partial void OnIsShownChanged(bool value) => ShownChanged?.Invoke(this, value);
}

/// <summary>One row in the Flyout section's Buttons checklist (Flyout D2 .2, RemEx-4kv0g.18.6) — one
/// of the six tray toolbar actions.</summary>
public partial class FlyoutTileChoice : ObservableObject
{
    /// <summary>Stable id — <c>lock</c>/<c>sleep</c>/<c>remote</c>/<c>send</c>/<c>pair</c>/<c>power</c>
    /// — matching <c>TrayTile.Id</c> and <c>CustomizationSettings.FlyoutHiddenTileIds</c>.</summary>
    public string Id { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isShown;

    public event EventHandler<bool>? ShownChanged;

    public FlyoutTileChoice(string id, string label, bool isShown)
    {
        Id = id;
        Label = label;
        _isShown = isShown;
    }

    partial void OnIsShownChanged(bool value) => ShownChanged?.Invoke(this, value);
}

/// <summary>One row in the Flyout section's Apps checklist (Flyout D2 .2, RemEx-4kv0g.18.6) — one
/// launcher entry. Unlike the other two lists this is opt-IN: unchecked is the default.</summary>
public partial class FlyoutAppChoice : ObservableObject
{
    /// <summary><see cref="AppEntry.Id"/> — matches <c>CustomizationSettings.FlyoutAppIds</c>.</summary>
    public Guid Id { get; }

    public string DisplayName { get; }

    public string? IconBase64 { get; }

    [ObservableProperty]
    private bool _isShown;

    public event EventHandler<bool>? ShownChanged;

    public FlyoutAppChoice(Guid id, string displayName, string? iconBase64, bool isShown)
    {
        Id = id;
        DisplayName = displayName;
        IconBase64 = iconBase64;
        _isShown = isShown;
    }

    partial void OnIsShownChanged(bool value) => ShownChanged?.Invoke(this, value);
}
