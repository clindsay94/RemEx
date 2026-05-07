using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Client.Models;
using Remex.Core.Models;
using System;

namespace Remex.Client.Services;

public class ThemeService : IDisposable
{
    public event Action<CustomizationSettings>? CustomizationApplied;
    private readonly ResourceDictionary _overrideResources = new();

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

            // All themes — including static presets — run through M3.
            // Each preset's AccentColor is the seed; SolarFlare gets a light-mode palette.
            var isLightTheme = string.Equals(settings.ThemeId, "SolarFlare", StringComparison.OrdinalIgnoreCase);
            if (Color.TryParse(settings.AccentColor, out var accentColor))
            {
                var palette = DynamicColorGenerator.Generate(
                    accentColor,
                    settings.SchemeVariant,
                    isDark: !isLightTheme);

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
                SetResourceOverrideInternal("SystemError", palette.Error);
                SetResourceOverrideInternal("SystemErrorBrush", new SolidColorBrush(palette.Error));

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
            }

            SetResourceOverrideInternal("CanvasBackgroundType", settings.BackgroundMaterial);

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
        var uri = new Uri($"avares://Remex.Client/Themes/{themeFile}.axaml");
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
