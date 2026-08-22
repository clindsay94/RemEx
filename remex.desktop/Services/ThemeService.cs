using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Desktop.Models;
using Remex.Core.Models;
using System.Diagnostics;

namespace Remex.Desktop.Services;

public class ThemeService : IDisposable
{
    public event Action<CustomizationSettings>? CustomizationApplied;
    private readonly ResourceDictionary _overrideResources = new();

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
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Enum.TryParse<AppTheme>(settings.ThemeId, true, out var themeEnum))
            {
                ApplyBaseThemeInternal(themeEnum);
            }
            else
            {
                // An unrecognized theme id (typo'd/renamed preset, stale saved settings, etc.) must
                // never leave the app with no base theme applied. Log it so the gap is findable, and
                // fall back to the known-good default rather than silently doing nothing.
                Trace.TraceWarning(
                    $"ThemeService.ApplyCustomization: unknown theme preset '{settings.ThemeId}' — falling back to {nameof(AppTheme.Dynamic)}.");
                ApplyBaseThemeInternal(AppTheme.Dynamic);
            }

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
            var isLightTheme = settings.UseLightPalette
                ?? string.Equals(settings.ThemeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);

            // The base theme file resolved a variant from the preset NAME. Now that light/dark is
            // its own setting, the variant has to follow the setting, or Fluent's own control
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
            // above OUTSIDE this check. A light preset with a bad seed would therefore paint Fluent's
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

            var palette = DynamicColorGenerator.Generate(
                accentColor,
                settings.SchemeVariant,
                isDark: !isLightTheme,
                contrast: Math.Clamp(settings.ThemeContrast, -1.0, 1.0));

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

            // Override Fluent theme's SystemAccentColor so native controls (Button, Slider,
            // ToggleSwitch, ComboBox, TextBox focus ring, etc.) pick up M3 colors.
            SetResourceOverrideInternal("SystemAccentColor", palette.Primary);
            SetResourceOverrideInternal("SystemAccentColorLight1", palette.Secondary);
            SetResourceOverrideInternal("SystemAccentColorLight2", palette.OnPrimaryContainer);
            SetResourceOverrideInternal("SystemAccentColorLight3", palette.Tertiary);
            SetResourceOverrideInternal("SystemAccentColorDark1", palette.PrimaryContainer);
            SetResourceOverrideInternal("SystemAccentColorDark2", palette.SurfaceContainerHigh);
            SetResourceOverrideInternal("SystemAccentColorDark3", palette.Surface);

            // Card hover glow tinted with the current accent color.
            var p = palette.Primary;
            SetResourceOverrideInternal("CardHoverShadow", new BoxShadows(
                new BoxShadow
                {
                    Blur = 48,
                    Spread = 0,
                    OffsetY = 12,
                    Color = Color.FromArgb(0x60, 0, 0, 0)
                },
                new BoxShadow[] { new BoxShadow { Blur = 15, Spread = 2,
                                Color = Color.FromArgb(0x50, p.R, p.G, p.B) } }));

            // Base card drop shadow + a neon accent glow whose intensity follows the
            // GlowStrength slider (0 = no glow). Every Border.glass-card consumes CardShadow,
            // so the glow previews live across whatever screen is open.
            double glow = Math.Clamp(settings.GlowStrength, 0, 30);
            var baseCardShadow = new BoxShadow
            {
                Blur = 24, Spread = 0, OffsetY = 6,
                Color = Color.FromArgb(0x40, 0, 0, 0)
            };
            var cardShadow = glow > 0.5
                ? new BoxShadows(baseCardShadow, new BoxShadow[]
                    {
                        new BoxShadow
                        {
                            Blur = glow * 1.6,
                            Spread = glow * 0.15,
                            OffsetX = 0, OffsetY = 0,
                            Color = Color.FromArgb((byte)Math.Clamp(0x30 + glow * 6, 0, 255), p.R, p.G, p.B)
                        }
                    })
                : new BoxShadows(baseCardShadow);
            SetResourceOverrideInternal("CardShadow", cardShadow);

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

                // Overall UI scale — clamped to a safe, legible range so the shell can't be shrunk into
                // illegibility or blown up past the window. Consumed by the shell's layout transform.
                app.Resources["UiScale"] = settings.UiScale <= 0 ? 1.0 : Math.Clamp(settings.UiScale, 0.85, 1.3);
            }

            // Reattach the override dictionary — fires one ResourcesChanged for all updates.
            merged?.Add(_overrideResources);

            CustomizationApplied?.Invoke(settings);
        });
    }

    private void ApplyBaseThemeInternal(AppTheme theme)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources) return;

        // Clear existing theme files, but keep our override dictionary!
        var dictionaries = resources.MergedDictionaries;
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i] != _overrideResources)
            {
                dictionaries.RemoveAt(i);
            }
        }

        var themeFile = theme == AppTheme.Dynamic ? "BaseDarkGlass" : theme.ToString();
        var uri = new Uri($"avares://Remex.Desktop/Themes/{themeFile}.axaml");
        dictionaries.Insert(0, new ResourceInclude(uri) { Source = uri });

        if (Application.Current is { })
        {
            Application.Current.RequestedThemeVariant = theme == AppTheme.SolarFlare
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }

    public void SetResourceOverrideInternal(string key, object value)
    {
        _overrideResources[key] = value;
    }

    /// <summary>
    /// Injects a color from physical hardware into the current theme.
    /// Used by HardwareThemeService.
    /// </summary>
    public void ApplyHardwareAccent(Color color)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // We only override the primary accent; the rest of the palette 
            // will be regenerated based on this hardware seed if needed.
            // For now, we just push the literal color.
            SetResourceOverrideInternal("AccentPrimary", color);
            SetResourceOverrideInternal("AccentPrimaryBrush", new SolidColorBrush(color));
        });
    }
}
