using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Material.Styles.Themes;
using Remex.Desktop.Models;
using Remex.Core.Models;
using System.Diagnostics;

namespace Remex.Desktop.Services;

public class ThemeService : IDisposable
{
    public event Action<CustomizationSettings>? CustomizationApplied;
    private readonly ResourceDictionary _overrideResources = new();

    /// <summary>
    /// The base-theme <see cref="ResourceInclude"/> this service last merged into
    /// <c>Application.Resources</c>, so the next switch can remove EXACTLY that one.
    /// </summary>
    /// <remarks>
    /// Null until the first switch, at which point <see cref="SwapBaseTheme"/> adopts the include
    /// <c>App.axaml</c> declared instead — see the comment there for why anything else merged into
    /// <c>Application.Resources</c> has to survive.
    /// </remarks>
    private IResourceProvider? _baseThemeResources;

    /// <summary>
    /// The seed used when the saved accent will not parse. Kept equal to
    /// <c>CustomizationSettings.AccentColor</c>'s own default on purpose: a user with a broken accent
    /// should land where a user with no accent lands, not somewhere third.
    /// </summary>
    internal const string FallbackAccentSeed = "#6C4CFF";

    /// <summary>
    /// <see cref="FallbackAccentSeed"/> as a colour, for callers that need the seed rather than the
    /// string — the Palette Studio initialises its HCT axes from this when the saved accent will not
    /// parse, so the sliders and the painted window agree about what a broken seed means.
    /// </summary>
    /// <remarks>
    /// TryParse ON A CONSTANT, for the same reason the apply path uses it: <c>Parse</c> throws, and a
    /// throw from a static initialiser is a type that can never be loaded. The literal second branch
    /// makes that structurally impossible rather than merely unlikely.
    /// </remarks>
    internal static readonly Color FallbackAccentColor =
        Color.TryParse(FallbackAccentSeed, out var fallback) ? fallback : Color.FromRgb(0x6C, 0x4C, 0xFF);

    /// <summary>
    /// Minimum alpha (as a 0..1 fraction) for popup surfaces — ComboBox dropdowns, ContextMenu,
    /// MenuFlyout — regardless of how low the Card Opacity slider (<see cref="CustomizationSettings.GlassOpacity"/>)
    /// is set. Cards themselves have no floor and can go fully transparent; a popup that goes with
    /// them is unreadable rather than stylish, since its only content is text over whatever is
    /// behind the window (Connor, 2026-08-31). Below this floor popups clamp here; above it they
    /// keep tracking the slider like a card does. One named constant so the number is easy to retune.
    /// </summary>
    internal const double PopupOpacityFloor = 0.40;

    public ThemeService()
    {
        // Add our override dictionary to the application resources.
        // We ensure it's added after theme files so it takes precedence.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current != null)
            {
                Application.Current.Resources.MergedDictionaries.Add(_overrideResources);
            }
        });
    }

    public void Dispose()
    {
        CustomizationApplied = null;
        DetachOsThemeListener();
    }

    // ─── System mode: follow the OS light/dark setting, live (RemEx-zk5bc) ──────────────────────

    /// <summary>The last settings applied, so an OS theme flip can re-run the same apply.</summary>
    private CustomizationSettings? _lastApplied;

    // ─── Hardware accent injection: the seed is a live override, never a preference (RemEx-w6c4s) ─

    /// <summary>
    /// The user's own most-recently-requested customization — set on every call to
    /// <see cref="ApplyCustomization"/> from the caller's own settings, and never from a
    /// hardware-overridden clone. This is what "the user's chosen seed" means when a hardware
    /// accent override is cleared: whatever was last actually requested, unmodified.
    /// </summary>
    /// <remarks>
    /// THE INVARIANT HAS EXACTLY ONE OTHER WAY IN, AND IT IS CLOSED DELIBERATELY. An earlier
    /// revision of this comment asserted the "never" and was wrong: <see cref="_lastApplied"/>
    /// holds the EFFECTIVE settings (hardware clone included), and
    /// <see cref="OnOsColorValuesChanged"/> used to re-apply from it, which routed the override
    /// colour back through <see cref="ApplyCustomization"/> and overwrote this field. That handler
    /// now prefers this field. If a third caller of <see cref="ApplyCustomization"/> ever appears,
    /// check what it is passing before trusting the word "never" above.
    /// </remarks>
    private CustomizationSettings? _userSettings;

    /// <summary>
    /// How this service gets onto the UI thread. Production leaves it alone; a test replaces it
    /// with <c>a =&gt; a()</c> to run the hop inline (RemEx-w6c4s).
    /// </summary>
    /// <remarks>
    /// THIS EXISTS SO A TEST CAN OBSERVE A RESTORE IT DID NOT PERFORM ITSELF. There is no
    /// Avalonia.Headless reference in the test assembly, so nothing pumps the dispatcher and a
    /// posted callback never runs. The first version of the restore test worked around that by
    /// calling the apply itself — which meant it passed with <see cref="ClearHardwareAccent"/>'s
    /// restore deleted, proving only that the generator is deterministic. Measured by injection,
    /// not guessed. With this seam the test asserts on the palette before and after a clear and
    /// touches nothing in between, so deleting the restore fails it.
    /// </remarks>
    internal Action<Action> PostToUiThread { get; set; } =
        static action => Avalonia.Threading.Dispatcher.UIThread.Post(() => action());

    /// <summary>
    /// The live hardware accent, when hardware sync is on and has reported a colour. Layered onto
    /// <see cref="_userSettings"/>'s <c>AccentColor</c> for palette generation only — it is never
    /// written into <see cref="_userSettings"/> itself, so it never reaches a saved profile.
    /// </summary>
    private Color? _hardwareAccentOverride;

    /// <summary>The platform-settings instance the listener is attached to, null when detached.</summary>
    /// <remarks>
    /// The INSTANCE is kept, not a bool: unsubscribing requires the same object that was
    /// subscribed to, and asking Avalonia again at detach time could hand back a different one.
    /// Attached only while the applied mode is <see cref="ThemeModes.System"/> — Light and Dark
    /// pin regardless of the OS, so holding a handler for them would be a leak with no effect.
    /// </remarks>
    private Avalonia.Platform.IPlatformSettings? _osListenerTarget;

    /// <summary>
    /// Resolves whether the palette is light. Pure and internal so the precedence — explicit mode
    /// first, then the legacy <c>UseLightPalette</c>-then-preset chain for profiles that predate
    /// the mode — is pinned by tests rather than re-derived by readers.
    /// </summary>
    /// <param name="osIsLight">
    /// The OS answer, or <c>null</c> when the platform cannot say — System mode then falls back to
    /// dark, matching what every profile painted before the mode existed.
    /// </param>
    internal static bool ResolveIsLight(CustomizationSettings settings, SeedPreset preset, bool? osIsLight) =>
        settings.ThemeMode switch
        {
            ThemeModes.Light => true,
            ThemeModes.Dark => false,
            ThemeModes.System => osIsLight ?? false,
            // null, or a value written by a newer build this one does not know: the legacy chain.
            _ => settings.UseLightPalette ?? preset.IsLight ?? false,
        };

    /// <summary>What the OS says right now, or <c>null</c> where it cannot be asked.</summary>
    internal static bool? TryGetOsIsLight()
    {
        var platform = Application.Current?.PlatformSettings;
        if (platform is null) return null;
        return platform.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light;
    }

    private void AttachOsThemeListener()
    {
        if (_osListenerTarget is not null) return;
        var platform = Application.Current?.PlatformSettings;
        if (platform is null) return;
        platform.ColorValuesChanged += OnOsColorValuesChanged;
        _osListenerTarget = platform;
    }

    private void DetachOsThemeListener()
    {
        if (_osListenerTarget is null) return;
        _osListenerTarget.ColorValuesChanged -= OnOsColorValuesChanged;
        _osListenerTarget = null;
    }

    /// <summary>
    /// Writes the seed into MaterialTheme's live palette so Material-templated controls follow the
    /// same colours every RemEx surface does (RemEx-prkot).
    /// </summary>
    /// <remarks>
    /// UI thread only — called from inside <see cref="ApplyCustomization"/>'s posted lambda.
    /// Null-tolerant on purpose: unit tests construct this service without a Material application
    /// (or any application), and a theme push with nowhere to land is a no-op, not a crash.
    /// </remarks>
    private static void PushSeedIntoMaterialTheme(Color primary, Color secondary)
    {
        try
        {
            if (Application.Current is not { } app) return;
            var materialTheme = app.LocateMaterialTheme<Material.Styles.Themes.MaterialThemeBase>();
            if (materialTheme is null) return;

            var theme = materialTheme.CurrentTheme.ToMutable();
            theme.SetPrimaryColor(primary);
            theme.SetSecondaryColor(secondary);
            materialTheme.CurrentTheme = theme;
        }
        catch (Exception ex)
        {
            // A palette that fails to reach Material's swatches leaves its controls on the last
            // colours rather than taking the theme system down mid-apply.
            Trace.TraceWarning($"ThemeService: could not push the seed into MaterialTheme — {ex.Message}");
        }
    }

    private void OnOsColorValuesChanged(object? sender, Avalonia.Platform.PlatformColorValues e)
    {
        // Re-run the whole apply rather than flipping the variant in place: the palette solve, the
        // theme variant, and every override key have to move together or the window paints a light
        // chrome under dark text. The settings guard means a listener that outlives a mode change
        // (the detach races the event) re-applies the pinned mode, which is a no-op repaint, not a
        // wrong one.
        // _userSettings FIRST, _lastApplied ONLY AS A FALLBACK (review HIGH, RemEx-w6c4s).
        // _lastApplied is what was PAINTED, which under an active hardware override is the
        // hardware-seeded clone, not what the user asked for. Re-applying that here fed the
        // override colour straight back through ApplyCustomization, whose first act is
        // `_userSettings = settings` — so an OS light/dark flip while sync was on quietly promoted
        // the hardware colour to "the user's seed", and turning sync off afterwards restored it
        // instead of the real one. The picker kept showing the real seed, so the swatch and the
        // window disagreed with nothing logged.
        // Re-applying _userSettings is not a behaviour change for the ordinary case: it rewrites
        // _userSettings with itself, and ApplyCustomization re-layers any live override on top,
        // so RemEx-zk5bc's OS-flip repaint is unchanged.
        var settings = _userSettings ?? _lastApplied;
        if (settings?.ThemeMode is not ThemeModes.System) return;
        ApplyCustomization(settings);
    }

    public void SetBaseTheme(AppTheme theme)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyBaseThemeInternal(theme));
    }

    /// <summary>
    /// Applies the base theme synchronously on the current (UI) thread.
    /// Safe to call from <c>OnFrameworkInitializationCompleted</c> before the
    /// event loop starts, so the correct theme is loaded before any window is shown.
    /// </summary>
    public void ApplyThemeSync(AppTheme theme)
    {
        ApplyBaseThemeInternal(theme);
    }

    public void ApplyCustomization(CustomizationSettings settings)
    {
        // Recorded BEFORE the post, synchronously: this is what a hardware override restores to,
        // and it must be the caller's own settings, never something this class computed.
        _userSettings = settings;
        PostToUiThread(() => ApplyCustomizationCore(WithHardwareAccentOverride(settings)));
    }

    /// <summary>
    /// The one apply path — the ~56 resources the palette owns are generated and pushed here,
    /// nowhere else. Reached from <see cref="ApplyCustomization"/>, <see cref="ApplyHardwareAccent"/>
    /// and <see cref="ClearHardwareAccent"/>, all of which resolve to one EFFECTIVE
    /// <see cref="CustomizationSettings"/> — real settings, or real settings with the accent
    /// swapped for a hardware colour — before landing here (RemEx-w6c4s).
    /// </summary>
    /// <remarks>
    /// UI thread only in production, always reached through a posted lambda. Internal (not
    /// private) so a test can call it synchronously — this assembly has no Avalonia.Headless
    /// reference, so nothing pumps <c>Dispatcher.UIThread</c> and a posted callback never runs in
    /// a unit test. Every branch below is already null-tolerant on <c>Application.Current</c> for
    /// exactly that reason (see <see cref="PushSeedIntoMaterialTheme"/>'s remarks).
    /// </remarks>
    internal void ApplyCustomizationCore(CustomizationSettings settings)
    {
        // THE CATALOG RESOLVES THE ID, NOT THE ENUM. AppTheme is the list of structural theme
        // FILES, which is four; the preset gallery is longer than that and gets longer. Parsing
        // ThemeId through the enum meant every preset outside the four failed to parse, landed
        // in the branch below, and logged a warning about a preset that is perfectly valid.
        if (!SeedPresetCatalog.TryGet(settings.ThemeId, out var preset))
        {
            // An unrecognized theme id (typo'd/renamed preset, stale saved settings, etc.) must
            // never leave the app with no base theme applied. Log it so the gap is findable, and
            // fall back to the known-good default rather than silently doing nothing.
            Trace.TraceWarning(
                $"ThemeService.ApplyCustomization: unknown theme preset '{settings.ThemeId}' — falling back to {SeedPresetCatalog.DefaultId}.");
        }

        ApplyBaseThemeInternal(preset.BaseTheme);

        // ── Batch resource updates ──────────────────────────────────────
        // Build all new values first, then detach the override dictionary,
        // repopulate it (no change-notifications fire while detached), and
        // reattach — triggering exactly ONE ResourcesChanged notification
        // instead of one per key (~25 previously).
        var merged = Application.Current?.Resources.MergedDictionaries;
        merged?.Remove(_overrideResources);
        _overrideResources.Clear();

        SetResourceOverrideInternal("CardCornerRadius", new CornerRadius(settings.CornerRadius));
        SetResourceOverrideInternal("RemoteCardCornerRadius", new CornerRadius(settings.RemoteCardCornerRadius));
        SetResourceOverrideInternal("GlassOpacity", settings.GlassOpacity);
        SetResourceOverrideInternal("AppWindowOpacity", settings.AppWindowOpacity);
        SetResourceOverrideInternal("GlowStrength", settings.GlowStrength);

        // EVERY THEME IS A SEED NOW. The four presets are not four palettes any more, they are
        // four saved seeds; whichever one is selected, its AccentColor goes through M3 and the
        // result overrides the whole colour surface. Light/dark is a setting rather than a
        // property of the preset's name — see CustomizationSettings.UseLightPalette for why the
        // null case still answers the old question.
        // The preset's own mode is the pre-key answer: every id that could appear in a profile
        // written before UseLightPalette existed is one of the four homages, and only SolarFlare
        // carries IsLight = true. Reading it off the catalog rather than string-comparing one
        // name means a new light preset does not need this line edited to be light.
        // ThemeMode outranks the legacy chain; System asks the OS and re-applies on the
        // OS flipping, which is what the listener below arms (RemEx-zk5bc).
        var isLightTheme = ResolveIsLight(settings, preset, TryGetOsIsLight());

        _lastApplied = settings;
        if (settings.ThemeMode is ThemeModes.System) AttachOsThemeListener();
        else DetachOsThemeListener();

        // The base theme file resolved a variant from the preset NAME. Now that light/dark is
        // its own setting, the variant has to follow the setting, or Material's own control
        // templates paint dark chrome underneath a light M3 palette.
        if (Application.Current is { } themedApp)
        {
            themedApp.RequestedThemeVariant = isLightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        // AN UNPARSEABLE SEED MUST NOT SKIP THE PALETTE, and until RemEx-07jij it quietly did.
        // The block below used to be the body of this TryParse with no else, so a bad accent left
        // every colour key on whatever the theme file happened to carry. That was survivable while
        // each preset carried its own complete palette; it stopped being survivable the moment the
        // four presets started sharing one DARK fallback, because RequestedThemeVariant is set
        // above OUTSIDE this check. A light preset with a bad seed would therefore paint Material's
        // light chrome under near-white RemEx text — unreadable, with no exception and no log.
        //
        // Falling back to the record's own default seed keeps the palette internally consistent
        // whatever the setting says, and the warning is what makes the bad value findable at all.
        if (!Color.TryParse(settings.AccentColor, out var accentColor))
        {
            Trace.TraceWarning(
                $"ThemeService.ApplyCustomization: unparseable accent '{settings.AccentColor}' — "
                + $"falling back to {FallbackAccentSeed}.");
            // TryParse RATHER THAN Parse, on a constant. Parse throws, and this runs inside a
            // Dispatcher.Post where a throw is an unhandled dispatcher exception - so the code
            // path that exists to stop a bad colour breaking the window would itself break the
            // window if the constant were ever edited to something unparseable. The literal
            // second branch makes that structurally impossible instead of merely unlikely.
            accentColor = Color.TryParse(FallbackAccentSeed, out var fallbackSeed)
                ? fallbackSeed
                : Color.FromRgb(0x6C, 0x4C, 0xFF);
        }

        // Normalised at the render boundary too, not only where the picker reads it: a profile at
        // the current schema can still carry a retired name (hand-edited, or written by a path
        // that skipped the 2→3 arm), and the engine's own fallback for "Spritz" is TonalSpot while
        // the picker would highlight Neutral. One function decides what a persisted string means.
        var palette = DynamicColorGenerator.Generate(
            accentColor,
            SchemeVariants.Normalize(settings.SchemeVariant),
            isDark: !isLightTheme,
            contrast: Math.Clamp(settings.ThemeContrast, -1.0, 1.0));

        // THE SEED REACHES MATERIAL'S OWN PALETTE TOO (RemEx-prkot). MaterialTheme dresses
        // every control template from its Primary/Secondary swatches; leaving those on the
        // App.axaml placeholders would make this a two-palette app — RemEx surfaces following
        // the seed while every Material control stays brand-purple, the exact failure the
        // component-library evaluation warned about. The seed itself as Primary and the
        // scheme's own secondary, through the supported SetPrimaryColor/SetSecondaryColor API.
        // (BaseTheme="Inherit" follows RequestedThemeVariant set above, so light/dark and the
        // System mode's live OS-follow carry over without a second wire.)
        PushSeedIntoMaterialTheme(accentColor, palette.Secondary);

        SetResourceOverrideInternal("AccentPrimary", palette.Primary);
        SetResourceOverrideInternal("AccentPrimaryBrush", new SolidColorBrush(palette.Primary));
        SetResourceOverrideInternal("AccentHover", palette.Secondary);
        SetResourceOverrideInternal("AccentHoverBrush", new SolidColorBrush(palette.Secondary));
        SetResourceOverrideInternal("AccentPressed", palette.Tertiary);
        SetResourceOverrideInternal("AccentPressedBrush", new SolidColorBrush(palette.Tertiary));
        SetResourceOverrideInternal("GlassBaseDark", palette.Surface);
        SetResourceOverrideInternal("GlassBaseDarkBrush", new SolidColorBrush(palette.Surface));
        SetResourceOverrideInternal("GlassBaseMedium", palette.SurfaceVariant);
        SetResourceOverrideInternal("GlassBaseMediumBrush", new SolidColorBrush(palette.SurfaceVariant));
        SetResourceOverrideInternal("TextPrimary", palette.OnSurface);
        SetResourceOverrideInternal("TextPrimaryBrush", new SolidColorBrush(palette.OnSurface));
        SetResourceOverrideInternal("TextSecondary", palette.OnSurfaceVariant);
        SetResourceOverrideInternal("TextSecondaryBrush", new SolidColorBrush(palette.OnSurfaceVariant));
        SetResourceOverrideInternal("CardBackground", palette.SurfaceContainer);
        SetResourceOverrideInternal("CardBackgroundHover", palette.SurfaceContainerHigh);

        // Apply card opacity: GlassOpacity controls how transparent the card surfaces are.
        byte cardAlpha = (byte)Math.Round(Math.Clamp(settings.GlassOpacity, 0.05, 1.0) * 255);
        var cardColor = Color.FromArgb(cardAlpha, palette.SurfaceContainer.R, palette.SurfaceContainer.G, palette.SurfaceContainer.B);
        var cardHoverColor = Color.FromArgb((byte)Math.Min(cardAlpha + 30, 255), palette.SurfaceContainerHigh.R, palette.SurfaceContainerHigh.G, palette.SurfaceContainerHigh.B);
        SetResourceOverrideInternal("CardBackgroundBrush", new SolidColorBrush(cardColor));
        SetResourceOverrideInternal("CardBackgroundHoverBrush", new SolidColorBrush(cardHoverColor));
        SetResourceOverrideInternal("CardBorder", palette.Outline);
        SetResourceOverrideInternal("CardBorderBrush", new SolidColorBrush(palette.Outline));

        // Popup surfaces (RemEx-mmrgc's neighbour, no bead — Connor reported this live).
        // Material.Avalonia's ComboBox.axaml wraps its dropdown in an un-Themed controls:Card,
        // which resolves App.axaml's app-wide {x:Type material:Card} override and so paints
        // with CardBackgroundBrush; ContextMenu.axaml and MenuFlyoutPresenter.axaml both set
        // their own Background from MaterialCardBackgroundBrush, which App.axaml points at this
        // same brush for the same reason. A Card Opacity of 0 therefore made every dropdown as
        // transparent — and as unreadable — as the cards. Popups get their own alpha here,
        // floored at PopupOpacityFloor so they never go below readable no matter how low the
        // slider is, while still tracking it (like a card does) above that floor.
        byte popupAlpha = Math.Max(cardAlpha, (byte)Math.Round(PopupOpacityFloor * 255));
        var popupColor = Color.FromArgb(popupAlpha, palette.SurfaceContainer.R, palette.SurfaceContainer.G, palette.SurfaceContainer.B);
        SetResourceOverrideInternal("PopupSurfaceBrush", new SolidColorBrush(popupColor));

        // De-emphasised text. M3 has no "muted" role; Outline is the role it has for
        // exactly this job, and it lands where the hand-authored greys already were.
        SetResourceOverrideInternal("TextMuted", palette.Outline);
        SetResourceOverrideInternal("TextMutedBrush", new SolidColorBrush(palette.Outline));

        // ── Semantic fills and the text that sits ON them ────────────────────────────
        // THESE FOUR FOREGROUNDS USED TO BE HAND-MEASURED HEX, one per theme, each with a
        // comment recording the ratio it was chosen for. They are M3 "on" roles now, which
        // is the same guarantee arrived at by construction instead of by hand: the
        // generator walks each one along its own tonal palette until it clears its target
        // against the exact fill it will be drawn on. Retuning a fill can no longer strand
        // its foreground, because the foreground is derived from the fill.
        SetResourceOverrideInternal("SystemError", palette.Error);
        SetResourceOverrideInternal("SystemErrorBrush", new SolidColorBrush(palette.Error));
        SetResourceOverrideInternal("ErrorForegroundBrush", new SolidColorBrush(palette.OnError));
        SetResourceOverrideInternal("SystemErrorBackgroundBrush", new SolidColorBrush(palette.Error) { Opacity = 0.15 });
        SetResourceOverrideInternal("SystemErrorBackgroundHoverBrush", new SolidColorBrush(palette.Error) { Opacity = 0.22 });

        SetResourceOverrideInternal("SystemSuccess", palette.Success);
        SetResourceOverrideInternal("SystemSuccessBrush", new SolidColorBrush(palette.Success));
        SetResourceOverrideInternal("SuccessForegroundBrush", new SolidColorBrush(palette.OnSuccess));
        SetResourceOverrideInternal("SystemSuccessBackgroundBrush", new SolidColorBrush(palette.Success) { Opacity = 0.15 });
        SetResourceOverrideInternal("SystemSuccessBackgroundHoverBrush", new SolidColorBrush(palette.Success) { Opacity = 0.22 });

        SetResourceOverrideInternal("SystemWarning", palette.Warning);
        SetResourceOverrideInternal("SystemWarningBrush", new SolidColorBrush(palette.Warning));
        SetResourceOverrideInternal("SystemWarningBackgroundBrush", new SolidColorBrush(palette.Warning) { Opacity = 0.15 });

        // Text on an accent-filled surface, and text on a 15%/22% TINT. They are different
        // answers and always have been: the tint is mostly surface, so what reads on it is
        // whatever reads on the surface — OnSurface — not what reads on the solid fill.
        SetResourceOverrideInternal("AccentForegroundBrush", new SolidColorBrush(palette.OnPrimary));
        SetResourceOverrideInternal("ErrorTintForegroundBrush", new SolidColorBrush(palette.OnSurface));

        // ── The shell backdrop ───────────────────────────────────────────────────────
        // The gradient BRUSH has to be replaced, not just its three Color keys: the theme
        // dictionaries bind their gradient stops with StaticResource, which resolves once
        // and never hears about an override.
        SetResourceOverrideInternal("BackgroundGradientStart", palette.BackgroundStart);
        SetResourceOverrideInternal("BackgroundGradientMid", palette.BackgroundMid);
        SetResourceOverrideInternal("BackgroundGradientEnd", palette.BackgroundEnd);
        SetResourceOverrideInternal("BackgroundGradientBrush", new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
                {
                    new GradientStop(palette.BackgroundStart, 0),
                    new GradientStop(palette.BackgroundMid, 0.5),
                    new GradientStop(palette.BackgroundEnd, 1),
                },
        });

        // Scrims. Both are "darken what is behind me", so both take the darkest neutral the
        // palette has rather than a literal black — on a light palette a pure-black scrim
        // reads as a hole, and BackgroundEnd is already white there.
        var scrim = isLightTheme ? palette.OnSurface : palette.BackgroundEnd;
        SetResourceOverrideInternal("GlassOverlayBrush",
            new SolidColorBrush(Color.FromArgb(0x33, scrim.R, scrim.G, scrim.B)));
        SetResourceOverrideInternal("OverlayBackdropBrush",
            new SolidColorBrush(Color.FromArgb(0xE6, palette.Surface.R, palette.Surface.G, palette.Surface.B)));

        // WinUI-name alias. It is CardBackground under another key, and it has to be
        // overridden explicitly for the same StaticResource reason as the gradient above.
        SetResourceOverrideInternal("SystemControlBackgroundListLowBrush", new SolidColorBrush(cardColor));

        // Override Material theme's SystemAccentColor so native controls (Button, Slider,
        // ToggleSwitch, ComboBox, TextBox focus ring, etc.) pick up M3 colors.
        SetResourceOverrideInternal("SystemAccentColor", palette.Primary);
        SetResourceOverrideInternal("SystemAccentColorLight1", palette.Secondary);
        SetResourceOverrideInternal("SystemAccentColorLight2", palette.OnPrimaryContainer);
        SetResourceOverrideInternal("SystemAccentColorLight3", palette.Tertiary);
        SetResourceOverrideInternal("SystemAccentColorDark1", palette.PrimaryContainer);
        SetResourceOverrideInternal("SystemAccentColorDark2", palette.SurfaceContainerHigh);
        SetResourceOverrideInternal("SystemAccentColorDark3", palette.Surface);

        // ELEVATION RAMP, tinted with the current accent colour (RemEx-qbzl1).
        // Three ordered levels replace the old CardShadow/CardHoverShadow pair, which named
        // two steps after the one control that consumed them. Every raised surface — Card,
        // dialog, app bar — now asks for a LEVEL, so the depth language stays consistent when
        // a later phase raises something new.
        //
        // The accent glow rides on top of the black drop shadow and its intensity follows the
        // GlowStrength slider (0 = drop shadow only). That is why RemEx cannot use Material's
        // ShadowAssist.ShadowDepth for cards: ShadowProvider hands back a FIXED black shadow
        // and writes it as a LOCAL value on the template border, which outranks any style —
        // there is no seam to push a themed, user-scaled glow through. RemEx owns the Card
        // ControlTheme (App.axaml) precisely so these keys reach the surface instead.
        var p = palette.Primary;
        double glow = Math.Clamp(settings.GlowStrength, 0, 30);

        // Blur/offset/alpha grow monotonically with the level; the glow scales with them so a
        // level-3 lift reads as further off the page than a level-1 card at the same slider
        // setting. Ratios follow Material's Depth1..Depth3 ordering, softened for glass.
        BoxShadows Elevation(double blur, double offsetY, byte dropAlpha, double glowScale)
        {
            var drop = new BoxShadow
            {
                Blur = blur,
                Spread = 0,
                OffsetY = offsetY,
                Color = Color.FromArgb(dropAlpha, 0, 0, 0)
            };

            if (glow <= 0.5)
            {
                return new BoxShadows(drop);
            }

            return new BoxShadows(drop, new[]
            {
                    new BoxShadow
                    {
                        Blur = glow * 1.6 * glowScale,
                        Spread = glow * 0.15 * glowScale,
                        OffsetX = 0,
                        OffsetY = 0,
                        Color = Color.FromArgb(
                            (byte)Math.Clamp(0x30 + glow * 6, 0, 255), p.R, p.G, p.B)
                    }
                });
        }

        SetResourceOverrideInternal("Elevation1Shadow", Elevation(24, 6, 0x40, 1.0));
        SetResourceOverrideInternal("Elevation2Shadow", Elevation(34, 9, 0x50, 1.15));
        SetResourceOverrideInternal("Elevation3Shadow", Elevation(48, 12, 0x60, 1.3));

        SetResourceOverrideInternal("CanvasBackgroundType", settings.BackgroundMaterial);

        // Live-themeable typography (font picker). Set directly on the application's root
        // resources — NOT the merged override dictionary — because App.axaml defines
        // PageTitleFontFamily as an OWN key, and a dictionary's own keys take precedence over its
        // merged dictionaries (a merged override would be shadowed and never apply). Writing the
        // own key in place still raises ResourcesChanged, so DynamicResource re-resolves live.
        // Guarded so a malformed/unresolvable persisted or system font string can never crash.
        if (Application.Current is { } app)
        {
            // Resolve each font through a guard that force-loads the glyph typeface and falls back to
            // the App.axaml default if it can't be materialized. A bad avares URI or a font the
            // platform can't load otherwise throws at RENDER time (outside any try/catch here), which
            // freezes the UI thread — so validation must happen now, before the resource is assigned.
            app.Resources["PageTitleFontFamily"] = SystemFontService.ResolveFontOrDefault(
                settings.PageTitleFontFamily, "avares://Remex.Desktop/Assets/Fonts#Orbitron");

            app.Resources["BodyFontFamily"] = SystemFontService.ResolveFontOrDefault(
                settings.BodyFontFamily, "avares://Avalonia.Fonts.Inter/Assets#Inter");

            // MIRRORED INTO MATERIAL'S KEY (RemEx-prkot): MaterialTheme sets FontFamily on
            // Window AND on popup roots through MaterialDesignFonts, so the font picker has to
            // land there too or every tooltip, flyout and context menu stays on the previous
            // body font while the windows change — the popup half of the audit's §3 trap.
            app.Resources["MaterialDesignFonts"] = app.Resources["BodyFontFamily"]!;

            // Overall UI scale — clamped to a safe, legible range so the shell can't be shrunk into
            // illegibility or blown up past the window. Consumed by the shell's layout transform.
            app.Resources["UiScale"] = settings.UiScale <= 0 ? 1.0 : Math.Clamp(settings.UiScale, 0.85, 1.3);
        }

        // Reattach the override dictionary — fires one ResourcesChanged for all updates.
        merged?.Add(_overrideResources);

        CustomizationApplied?.Invoke(settings);
    }

    /// <summary>
    /// Layers the live hardware override onto <paramref name="settings"/>'s <c>AccentColor</c>,
    /// when one is active. Returns <paramref name="settings"/> unchanged otherwise. Never mutates
    /// its argument — <c>CustomizationSettings</c> is a record, so this returns a NEW instance via
    /// <c>with</c>; the caller's own copy (and anything built from it, including what gets saved)
    /// is untouched either way.
    /// </summary>
    /// <remarks>Internal so a test can compute the same effective settings production would post,
    /// without needing a pumped dispatcher to observe it.</remarks>
    internal CustomizationSettings WithHardwareAccentOverride(CustomizationSettings settings) =>
        _hardwareAccentOverride is { } hw ? settings with { AccentColor = ToHexSeed(hw) } : settings;

    /// <summary>6-digit hex, matching the shape every other seed in this file is written as
    /// (<see cref="FallbackAccentSeed"/>, <c>CustomizationSettings.AccentColor</c>'s default). The
    /// hardware colour's own alpha is discarded — a seed is opaque by definition here.</summary>
    private static string ToHexSeed(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void ApplyBaseThemeInternal(AppTheme theme)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources) return;

        _baseThemeResources = SwapBaseTheme(resources.MergedDictionaries, _baseThemeResources, BaseThemeUri(theme));

        if (Application.Current is { })
        {
            Application.Current.RequestedThemeVariant = theme == AppTheme.SolarFlare
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }

    /// <summary>The folder every selectable base theme file lives in.</summary>
    private const string ThemeDictionaryPrefix = "avares://Remex.Desktop/Themes/";

    /// <summary>The theme file a preset resolves to. <c>Dynamic</c> has no file of its own and
    /// paints from the seed, so it rides on BaseDarkGlass's geometry.</summary>
    internal static Uri BaseThemeUri(AppTheme theme) =>
        new($"{ThemeDictionaryPrefix}{(theme == AppTheme.Dynamic ? "BaseDarkGlass" : theme.ToString())}.axaml");

    /// <summary>
    /// The base theme files, by source string — the ONLY dictionaries a theme switch is allowed to
    /// remove. Four entries for five presets, because <see cref="AppTheme.Dynamic"/> rides on
    /// BaseDarkGlass. Ordinal on purpose: these paths are written by this file and by
    /// <c>App.axaml</c>, and an avares URI that differed by case would not have resolved at all.
    /// </summary>
    private static readonly HashSet<string> BaseThemeSources =
        Enum.GetValues<AppTheme>()
            .Select(theme => BaseThemeUri(theme).OriginalString)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Replaces the base theme merged into <paramref name="dictionaries"/> with the one at
    /// <paramref name="uri"/>, and returns the include that was inserted so the caller can hand it
    /// back as <paramref name="current"/> next time.
    /// </summary>
    /// <remarks>
    /// REMOVES BASE THEMES ONLY, NOT EVERY DICTIONARY (RemEx-gcqw5). This used to clear everything
    /// that was not the override dictionary, which did replace the previous theme but also deleted
    /// any other <c>ResourceInclude</c> <c>App.axaml</c> merges — on the FIRST theme switch, never
    /// at startup, with no exception and no log line. RemEx-qbzl1 had to move the Card
    /// <c>ControlTheme</c> out of a merged dictionary to escape it; the next one added would have
    /// been caught the same way.
    ///
    /// MATCHED AGAINST <see cref="BaseThemeSources"/> — the exact four files, NOT the
    /// <c>Themes/</c> folder prefix. That folder also holds <c>Chrome/WindowChrome.axaml</c>
    /// (merged by <c>MainWindow.axaml</c>) and <c>Shared/FallbackPalette.axaml</c>. A prefix test
    /// would eat either of those the moment it was hoisted to app scope AND leave the real theme
    /// sitting behind the new one, where it keeps painting — the same silent failure this method
    /// exists to remove, in a narrower disguise.
    ///
    /// <paramref name="current"/> is null on the first call, because the theme in place then is the
    /// one <c>App.axaml</c> declared rather than one this service inserted. It still has to go, or
    /// it would outrank every later theme: the override dictionary is appended LAST to take
    /// precedence, so position in this list is priority, and a stale theme left behind the new one
    /// at index 0 would keep painting.
    ///
    /// EVERY match goes, not just the first. A duplicate base theme is precisely the state that
    /// paints the wrong palette forever, so sweeping the whole list is what keeps this self-healing
    /// the way the clear-everything loop it replaced was.
    ///
    /// Insert at 0 rather than append, for that same reason: the theme is the floor everything else
    /// overrides.
    /// </remarks>
    internal static IResourceProvider SwapBaseTheme(
        IList<IResourceProvider> dictionaries,
        IResourceProvider? current,
        Uri uri)
    {
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(dictionaries[i], current) || IsBaseTheme(dictionaries[i]))
            {
                dictionaries.RemoveAt(i);
            }
        }

        var include = new ResourceInclude(uri) { Source = uri };
        dictionaries.Insert(0, include);
        return include;
    }

    /// <summary>Whether a merged dictionary is one of the base theme files — that is, something a
    /// theme switch owns and may replace. Anything else merged into <c>Application.Resources</c>
    /// is never a candidate.</summary>
    private static bool IsBaseTheme(IResourceProvider dictionary) =>
        dictionary is ResourceInclude { Source: { } source }
        && BaseThemeSources.Contains(source.OriginalString);

    public void SetResourceOverrideInternal(string key, object value)
    {
        _overrideResources[key] = value;
    }

    /// <summary>
    /// Injects a colour from physical hardware (RGB peripherals, via <see cref="HardwareThemeService"/>)
    /// as the palette SEED — not a single overwritten brush. Runs through the same
    /// generate-and-apply path as any other palette change, so all ~56 derived resources move
    /// together and the change crossfades exactly like a manual seed change does (RemEx-w6c4s).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user's own seed (<see cref="_userSettings"/>) is untouched by this — it is what
    /// <see cref="ClearHardwareAccent"/> restores. If nothing has been applied yet (no profile
    /// loaded, no <see cref="ApplyCustomization"/> call), there is nothing to layer the colour
    /// onto and this is a no-op beyond recording the override for whenever there is.
    /// </para>
    /// <para>
    /// CALLABLE FROM ANY THREAD. This is driven by a polling loop, so the whole body runs inside
    /// the post rather than reading and writing <see cref="_hardwareAccentOverride"/> on whatever
    /// thread the poller happens to be on. Doing the check-then-act off the dispatcher was a real
    /// hazard, not a theoretical one (review MEDIUM): a poll-thread apply interleaving with a
    /// UI-thread <see cref="ClearHardwareAccent"/> could land its repaint after the clear and leave
    /// the override field null while the hardware colour was still painted — after which every
    /// subsequent clear short-circuits and the palette can never be restored, silently.
    /// </para>
    /// </remarks>
    public void ApplyHardwareAccent(Color color) => PostToUiThread(() =>
    {
        // DEDUPE, mirroring ClearHardwareAccent's own guard and for the same stated reason. The
        // poll reports the same colour every tick for a rig sitting on a static profile; without
        // this, each tick regenerates the palette, swaps all ~56 resources, and restarts
        // RemEx-zgtn1's crossfade — a UI that pulses every 5 seconds forever to show no change.
        // Color is a struct with value equality, so this compares the colour, not a reference.
        if (_hardwareAccentOverride == color) return;

        _hardwareAccentOverride = color;
        if (_userSettings is not { } baseline) return;
        ApplyCustomizationCore(WithHardwareAccentOverride(baseline));
    });

    /// <summary>
    /// Clears a hardware accent override and restores the user's own seed. Called when hardware
    /// sync turns off (<see cref="HardwareThemeService.SetEnabled"/>) or the override otherwise
    /// needs to end (hardware disconnected, polling stopped).
    /// </summary>
    /// <remarks>
    /// A no-op when no override is active — disabling sync twice, or disabling it when it was
    /// never really injecting anything, must not re-apply the palette and crossfade for nothing.
    /// Callable from any thread, for the same reason and in the same shape as
    /// <see cref="ApplyHardwareAccent"/>: the check-then-act on the override field happens inside
    /// the post, so the two cannot interleave across threads.
    /// </remarks>
    public void ClearHardwareAccent() => PostToUiThread(() =>
    {
        if (_hardwareAccentOverride is null) return;
        _hardwareAccentOverride = null;
        if (_userSettings is not { } baseline) return;
        ApplyCustomizationCore(baseline);
    });

    // ─── Test seams (RemEx-w6c4s) — internal, visible to Remex.Desktop.Tests via InternalsVisibleTo.
    // The injection path is meant to be verifiable without hardware; these exist for exactly that
    // and production code has no reason to call them.

    /// <summary>The user's own most-recently-requested settings — see <see cref="_userSettings"/>.</summary>
    internal CustomizationSettings? UserSettings => _userSettings;

    /// <summary>The live hardware override colour, or null when sync is off / has reported nothing.</summary>
    internal Color? HardwareAccentOverride => _hardwareAccentOverride;

    /// <summary>Reads back a key from the override dictionary <see cref="ApplyCustomizationCore"/>
    /// writes to, for asserting what an apply actually produced.</summary>
    internal object? GetOverrideResource(string key) => _overrideResources.TryGetValue(key, out var value) ? value : null;
}
