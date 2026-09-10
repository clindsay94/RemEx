using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Desktop.Services;
using System;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// One swatch strip in the Scheme Variant row, painted from the CURRENT seed in ITS OWN variant.
/// </summary>
/// <remarks>
/// Same shape as <see cref="SeedPresetTileViewModel"/> for the same reason: a strip that showed a
/// hand-picked "looks like Vibrant" colour would be a second source of truth for what Vibrant is,
/// and it would drift the moment <see cref="DynamicColorGenerator"/> changed. Every brush here comes
/// out of <c>DynamicColorGenerator.Generate</c> run with this strip's own variant against the live
/// seed/mode/contrast, so a strip cannot disagree with what clicking it produces.
/// </remarks>
public sealed partial class SchemeVariantStripViewModel : ObservableObject, IDisposable
{
    public SchemeVariantStripViewModel(string variant)
    {
        Variant = variant;
        // Non-null defaults so the strip is renderable before the first Refresh; the constructor
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
    /// it would return while nothing on this object has changed and no notification fires without
    /// this handler forcing one. Mirrors <see cref="SeedPresetTileViewModel.OnLocaleChanged"/>.
    /// </summary>
    private void OnLocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(DisplayName));

    public void Dispose() => LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;

    /// <summary>The raw variant name passed to <c>DynamicColorGenerator.Generate</c> — e.g. "Vibrant".</summary>
    public string Variant { get; }

    /// <summary>The localized label. Reuses the <c>Custom_Scheme_*</c> keys the old ComboBox already read.</summary>
    public string DisplayName => LocalizationService.Instance[$"Custom_Scheme_{Variant}"];

    [ObservableProperty] private IBrush _surfaceBrush;
    [ObservableProperty] private IBrush _primaryBrush;
    [ObservableProperty] private IBrush _secondaryBrush;
    [ObservableProperty] private IBrush _tertiaryBrush;
    [ObservableProperty] private IBrush _onSurfaceBrush;
    [ObservableProperty] private IBrush _outlineBrush;
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Recomputes this strip's palette against the live seed, mode and contrast, using
    /// <see cref="Variant"/> in place of whatever variant is currently selected.
    /// </summary>
    public void Refresh(string liveSeed, bool liveIsLight, double liveContrast)
    {
        var seed = Color.TryParse(liveSeed, out var parsed) ? parsed : ThemeService.FallbackAccentColor;
        var palette = DynamicColorGenerator.Generate(
            seed,
            Variant,
            isDark: !liveIsLight,
            contrast: liveContrast);

        SurfaceBrush = new SolidColorBrush(palette.Surface);
        PrimaryBrush = new SolidColorBrush(palette.Primary);
        SecondaryBrush = new SolidColorBrush(palette.Secondary);
        TertiaryBrush = new SolidColorBrush(palette.Tertiary);
        OnSurfaceBrush = new SolidColorBrush(palette.OnSurface);
        OutlineBrush = new SolidColorBrush(palette.Outline);
    }
}
