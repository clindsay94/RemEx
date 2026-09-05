using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// One user-saved palette on the sheet (RemEx-ddynd), painted from its OWN recipe in the window's
/// live light/dark — the same shape as <see cref="SeedPresetTileViewModel"/>, minus localisation:
/// the name is the person's own text.
/// </summary>
public sealed partial class SavedPaletteTileViewModel : ObservableObject
{
    public SavedPaletteTileViewModel(SavedPalette palette)
    {
        Record = palette;
        _name = palette.Name;
        _surfaceBrush = Brushes.Transparent;
        _primaryBrush = Brushes.Transparent;
        _secondaryBrush = Brushes.Transparent;
        _tertiaryBrush = Brushes.Transparent;
        _onSurfaceBrush = Brushes.Transparent;
        _outlineBrush = Brushes.Transparent;
    }

    /// <summary>The persisted recipe. Replaced (records are immutable) on rename.</summary>
    public SavedPalette Record { get; private set; }

    /// <summary>Raised after a rename lands in <see cref="Record"/>, so the owner can persist.</summary>
    public event Action<SavedPaletteTileViewModel>? Renamed;

    [ObservableProperty] private string _name;

    [ObservableProperty] private IBrush _surfaceBrush;
    [ObservableProperty] private IBrush _primaryBrush;
    [ObservableProperty] private IBrush _secondaryBrush;
    [ObservableProperty] private IBrush _tertiaryBrush;
    [ObservableProperty] private IBrush _onSurfaceBrush;
    [ObservableProperty] private IBrush _outlineBrush;

    partial void OnNameChanged(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            // A blank name is not a rename; put the old one back without re-entering.
            if (Name != Record.Name) Name = Record.Name;
            return;
        }
        if (string.Equals(trimmed, Record.Name, StringComparison.Ordinal)) return;

        Record = Record with { Name = trimmed };
        Renamed?.Invoke(this);
    }

    /// <summary>Repaints the tile from its recipe. Cheap: one Generate per tile.</summary>
    public void Refresh(bool liveIsLight)
    {
        var seed = Color.TryParse(Record.Seed, out var parsed) ? parsed : ThemeService.FallbackAccentColor;
        var palette = DynamicColorGenerator.Generate(seed, Record.Strategy, isDark: !liveIsLight, Math.Clamp(Record.Contrast, -1.0, 1.0));

        SurfaceBrush = new SolidColorBrush(palette.Surface);
        PrimaryBrush = new SolidColorBrush(palette.Primary);
        SecondaryBrush = new SolidColorBrush(palette.Secondary);
        TertiaryBrush = new SolidColorBrush(palette.Tertiary);
        OnSurfaceBrush = new SolidColorBrush(palette.OnSurface);
        OutlineBrush = new SolidColorBrush(palette.Outline);
    }
}
