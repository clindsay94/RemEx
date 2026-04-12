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

    public void Dispose()
    {
        // Clear event subscribers to prevent memory leaks
        CustomizationApplied = null;
    }

    public void SetBaseTheme(AppTheme theme)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyBaseThemeInternal(theme));
    }

    public void ApplyCustomization(CustomizationSettings settings)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Enum.TryParse<AppTheme>(settings.ThemeId, true, out var themeEnum))
            {
                // Internal call to SetBaseTheme without nested Post
                ApplyBaseThemeInternal(themeEnum);
            }

            SetResourceOverrideInternal("CardCornerRadius", new CornerRadius(settings.CornerRadius));
            SetResourceOverrideInternal("RemoteCardCornerRadius", new CornerRadius(settings.RemoteCardCornerRadius));
            SetResourceOverrideInternal("GlassOpacity", settings.GlassOpacity);
            SetResourceOverrideInternal("GlowStrength", settings.GlowStrength);

            // Scale card background alpha with GlassOpacity so the slider has a visible effect
            // across all background modes (not just Acrylic). Max base alpha is 0x26 (≈15%).
            var baseAlpha = (byte)Math.Clamp(settings.GlassOpacity * 0x26, 1, 0x40);
            var hoverAlpha = (byte)Math.Clamp(settings.GlassOpacity * 0x33, 1, 0x55);
            SetResourceOverrideInternal("CardBackground", Color.FromArgb(baseAlpha, 255, 255, 255));
            SetResourceOverrideInternal("CardBackgroundHover", Color.FromArgb(hoverAlpha, 255, 255, 255));
            SetResourceOverrideInternal("CardBackgroundBrush", new SolidColorBrush(Color.FromArgb(baseAlpha, 255, 255, 255)));
            SetResourceOverrideInternal("CardBackgroundHoverBrush", new SolidColorBrush(Color.FromArgb(hoverAlpha, 255, 255, 255)));

            if (Color.TryParse(settings.AccentColor, out var accentColor))
            {
                SetResourceOverrideInternal("AccentPrimary", accentColor);
                SetResourceOverrideInternal("AccentPrimaryBrush", new SolidColorBrush(accentColor));
            }

            SetResourceOverrideInternal("CanvasBackgroundType", settings.BackgroundMaterial);

            CustomizationApplied?.Invoke(settings);
        });
    }

    private void ApplyBaseThemeInternal(AppTheme theme)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources) return;

        resources.MergedDictionaries.Clear();

        var uri = new Uri($"avares://Remex.Client/Themes/{theme}.axaml");
        resources.MergedDictionaries.Add(new ResourceInclude(uri)
        {
            Source = uri
        });

        // SolarFlare is a light theme → switch the Fluent variant
        if (Application.Current is { })
        {
            Application.Current.RequestedThemeVariant = theme == AppTheme.SolarFlare
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }

    public void SetResourceOverride(string key, object value)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SetResourceOverrideInternal(key, value));
    }

    private void SetResourceOverrideInternal(string key, object value)
    {
        if (Application.Current == null) return;
        Application.Current.Resources[key] = value;
    }
}
