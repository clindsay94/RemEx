using System;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Desktop.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// One card in the preset gallery, painted in its own palette.
/// </summary>
/// <remarks>
/// THE GALLERY PREVIEWS ITSELF. A swatch row hand-picked to "look like" a preset is a second source
/// of truth for that preset's colours, and it drifts — the tiles this replaced still showed
/// CyberNOC as literal #050505/#00F3FF long after the palette started coming out of the generator.
/// Every brush here is <c>DynamicColorGenerator.Generate</c> run on the preset's own inputs, so a
/// tile cannot disagree with what clicking it produces.
/// </remarks>
public sealed partial class SeedPresetTileViewModel : ObservableObject, IDisposable
{
    public SeedPresetTileViewModel(SeedPreset preset)
    {
        Preset = preset;
        // Non-null defaults so the tile is renderable before the first Refresh; the constructor
        // cannot call Refresh because it has no view of the live settings.
        _surfaceBrush = Brushes.Transparent;
        _primaryBrush = Brushes.Transparent;
        _secondaryBrush = Brushes.Transparent;
        _tertiaryBrush = Brushes.Transparent;
        _onSurfaceBrush = Brushes.Transparent;
        _outlineBrush = Brushes.Transparent;

        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    /// <summary>
    /// <see cref="DisplayName"/> resolves its text when it is GOT, so a language change alters what
    /// it would return while nothing on this object has changed and no notification fires. Every
    /// preset name is a proper noun that reads the same in all nine locales today, which is exactly
    /// what would make this defect invisible until someone localizes one of them.
    /// </summary>
    private void OnLocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(DisplayName));

    public void Dispose() => LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;

    public SeedPreset Preset { get; }

    public string Id => Preset.Id;

    public string DisplayName => LocalizationService.Instance[Preset.NameKey];

    /// <summary>
    /// The preset's own card radius, clamped to something a 36px-tall swatch can actually show.
    /// SolarFlare's 48px remote radius on a 36px preview is a circle, not a rounded rectangle.
    /// </summary>
    public CornerRadius PreviewCornerRadius => new(Math.Clamp(Preset.CornerRadius, 0, 14));

    [ObservableProperty] private IBrush _surfaceBrush;
    [ObservableProperty] private IBrush _primaryBrush;
    [ObservableProperty] private IBrush _secondaryBrush;
    [ObservableProperty] private IBrush _tertiaryBrush;
    [ObservableProperty] private IBrush _onSurfaceBrush;
    [ObservableProperty] private IBrush _outlineBrush;
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Recomputes the tile's palette. <paramref name="liveSeed"/> and friends are what the profile
    /// currently carries, and they are used only where the preset itself declines to choose — which
    /// is Dynamic, and only Dynamic.
    /// </summary>
    public void Refresh(string liveSeed, string liveVariant, bool liveIsLight, double liveContrast)
    {
        var palette = PaletteFor(Preset, liveSeed, liveVariant, liveIsLight, liveContrast);

        SurfaceBrush = new SolidColorBrush(palette.Surface);
        PrimaryBrush = new SolidColorBrush(palette.Primary);
        SecondaryBrush = new SolidColorBrush(palette.Secondary);
        TertiaryBrush = new SolidColorBrush(palette.Tertiary);
        OnSurfaceBrush = new SolidColorBrush(palette.OnSurface);
        OutlineBrush = new SolidColorBrush(palette.Outline);
    }

    /// <summary>
    /// The palette a preset renders as. Static and dependency-free so a test can assert that a tile
    /// and the settings that clicking it writes produce the same colours.
    /// </summary>
    public static DynamicColorGenerator.M3Palette PaletteFor(
        SeedPreset preset, string liveSeed, string liveVariant, bool liveIsLight, double liveContrast)
    {
        var seedHex = preset.Seed ?? liveSeed;
        // Same fallback constant the theme service uses for an unparseable seed. A tile that throws
        // or renders black because a profile carries junk is worse than a tile showing the default.
        if (!Color.TryParse(seedHex, out var seed) && !Color.TryParse(liveSeed, out seed))
        {
            seed = Color.FromRgb(0x6C, 0x4C, 0xFF);
        }

        return DynamicColorGenerator.Generate(
            seed,
            preset.SchemeVariant ?? liveVariant,
            isDark: !(preset.IsLight ?? liveIsLight),
            contrast: Math.Clamp(preset.Contrast ?? liveContrast, -1.0, 1.0));
    }
}
