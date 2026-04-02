using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Client.Models;
using Remex.Core.Models;
using System;

namespace Remex.Client.Services;

public class ThemeService
{
    public event Action<CustomizationSettings>? CustomizationApplied;

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
