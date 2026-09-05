using System;
using Avalonia.Media;
using Avalonia.Threading;
using Remex.Core.Guards;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// The always-alive consumer of <see cref="WindowsAccentWatcher"/>: when the profile's colour
/// source is the Windows accent, a changed accent rewrites <c>AccentColor</c>, repaints through
/// <see cref="ThemeService.ApplyCustomization"/> and saves through the debounced path.
/// </summary>
/// <remarks>
/// NOT THE CUSTOMIZATION VIEW MODEL, because that is built lazily by <c>ShellViewModel</c> the
/// first time the sheet opens — a person who never opens it would never follow the accent. The
/// view model, when it exists, hears the resulting <c>CustomizationApplied</c> and adopts the new
/// seed without saving again.
/// </remarks>
public sealed class ColorSourceCoordinator : IDisposable
{
    private readonly DashboardLayoutService _layout;
    private readonly ThemeService _theme;
    private readonly WindowsAccentWatcher _watcher;

    public ColorSourceCoordinator(DashboardLayoutService layout, ThemeService theme, WindowsAccentWatcher watcher)
    {
        _layout = Guard.NotNull(layout);
        _theme = Guard.NotNull(theme);
        _watcher = Guard.NotNull(watcher);
        _watcher.AccentChanged += OnAccentChanged;
    }

    /// <summary>Windows only; elsewhere there is no accent to follow and nothing is armed.</summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        _watcher.Start();
        // The accent may have changed while the app was closed: adopt it now if the profile follows it.
        if (_watcher.Current is { } hex) Apply(hex);
    }

    public void SetWindowVisible(bool visible) => _watcher.SetVisible(visible);

    public void PollNow() => _watcher.PollNow();

    private void OnAccentChanged(string hex) => Dispatcher.UIThread.Post(() => Apply(hex));

    /// <summary>UI thread. No-op unless the profile's source is the Windows accent.</summary>
    internal void Apply(string hex)
    {
        var profile = _layout.CurrentProfile;
        var settings = profile.Customization;
        if (!string.Equals(settings.ColorSource, ColorSources.WindowsAccent, StringComparison.Ordinal)) return;

        var shaped = ShapedBySource(settings, hex);
        if (string.Equals(shaped.AccentColor, settings.AccentColor, StringComparison.OrdinalIgnoreCase)) return;

        _theme.ApplyCustomization(shaped);
        _layout.RequestSave(profile with { Customization = shaped });
    }

    /// <summary>
    /// The seed a source colour becomes: the source's hue and tone, the profile's own vibrancy
    /// (<c>ThemeSeedChroma</c>) as chroma — so the Vibrancy slider keeps shaping a seed the person
    /// cannot type. Returns the same instance when the source is not a colour.
    /// </summary>
    internal static CustomizationSettings ShapedBySource(CustomizationSettings settings, string sourceHex)
    {
        if (!Color.TryParse(sourceHex, out var source)) return settings;

        var (hue, _, tone) = SeedHct.FromColor(source);
        var seed = SeedHct.ToHex(hue, settings.ThemeSeedChroma, tone);
        return settings with
        {
            AccentColor = seed,
            ThemeSeedChroma = SeedHct.ChromaOf(seed, settings.ThemeSeedChroma),
        };
    }

    public void Dispose() => _watcher.AccentChanged -= OnAccentChanged;
}
