using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Desktop.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Remex.Desktop.ViewModels;

/// <summary>One swatch in a tonal ramp row — the tone it was sampled at, and the colour there.</summary>
public sealed record TonalRampSwatch(int Tone, IBrush Brush);

/// <summary>
/// The Personalization panel's tonal-ramp preview: the primary/secondary/tertiary/neutral tonal
/// palettes at 0,10,…,100, plus the resolved role pairs (Primary/OnPrimary, Surface/OnSurface,
/// Error/OnError) so a contrast problem is visible at a glance rather than inferred from four
/// isolated swatches.
/// </summary>
/// <remarks>
/// EVERYTHING HERE COMES OUT OF THE GENERATOR, same reason as <see cref="SeedPresetTileViewModel"/>
/// and <see cref="SchemeVariantStripViewModel"/>: a ramp preview that could disagree with what the
/// shell actually paints would be worse than no preview.
/// </remarks>
public sealed partial class TonalRampViewModel : ObservableObject
{
    public TonalRampViewModel()
    {
        _primaryBrush = Brushes.Transparent;
        _onPrimaryBrush = Brushes.Transparent;
        _surfaceBrush = Brushes.Transparent;
        _onSurfaceBrush = Brushes.Transparent;
        _errorBrush = Brushes.Transparent;
        _onErrorBrush = Brushes.Transparent;
    }

    public ObservableCollection<TonalRampSwatch> PrimaryTones { get; } = new();
    public ObservableCollection<TonalRampSwatch> SecondaryTones { get; } = new();
    public ObservableCollection<TonalRampSwatch> TertiaryTones { get; } = new();
    public ObservableCollection<TonalRampSwatch> NeutralTones { get; } = new();

    // The role pairs, shown as foreground-on-background rather than as six separate swatches, so a
    // low-contrast pair reads as unreadable text instead of as two chips that merely look similar.
    [ObservableProperty] private IBrush _primaryBrush;
    [ObservableProperty] private IBrush _onPrimaryBrush;
    [ObservableProperty] private IBrush _surfaceBrush;
    [ObservableProperty] private IBrush _onSurfaceBrush;
    [ObservableProperty] private IBrush _errorBrush;
    [ObservableProperty] private IBrush _onErrorBrush;

    /// <summary>
    /// The (seed, variant) the four tone rows were last built from, so a refresh that cannot have
    /// changed them does not rebuild them (review MEDIUM, RemEx-lrxyo).
    /// </summary>
    private (Color Seed, string Variant)? _tonesBuiltFrom;

    /// <summary>Recomputes every ramp and role pair against the live seed, variant, mode and contrast.</summary>
    public void Refresh(string liveSeed, string liveVariant, bool liveIsLight, double liveContrast)
    {
        var seed = Color.TryParse(liveSeed, out var parsed) ? parsed : ThemeService.FallbackAccentColor;

        // THE TONE ROWS DEPEND ON (seed, variant) ONLY — a tonal palette is the raw scale a scheme
        // mapper picks roles off, and neither mode nor contrast changes what tones exist on it
        // (DynamicColorGenerator.GenerateTonalRamps says the same). Everything below this guard does
        // depend on mode and contrast and still runs every time.
        //
        // The guard is not micro-optimisation. Refresh runs at the tail of every ApplyAndSave, and
        // ApplyAndSave fires per TICK of the contrast, glow, opacity and corner-radius sliders — so
        // without it a single drag Clear()s four non-virtualizing ObservableCollections dozens of
        // times, tearing down and recreating 44 Border containers each tick to produce a bitwise
        // identical ramp. RefreshPresetPreviews already carries an `onlyVarying` skip for exactly
        // this reason; this is the ramp's equivalent.
        var tonesKey = (seed, liveVariant);
        if (_tonesBuiltFrom != tonesKey)
        {
            _tonesBuiltFrom = tonesKey;

            var ramps = DynamicColorGenerator.GenerateTonalRamps(seed, liveVariant);
            ReplaceTones(PrimaryTones, ramps.Primary);
            ReplaceTones(SecondaryTones, ramps.Secondary);
            ReplaceTones(TertiaryTones, ramps.Tertiary);
            ReplaceTones(NeutralTones, ramps.Neutral);
        }

        var palette = DynamicColorGenerator.Generate(
            seed,
            liveVariant,
            isDark: !liveIsLight,
            contrast: liveContrast);

        PrimaryBrush = new SolidColorBrush(palette.Primary);
        OnPrimaryBrush = new SolidColorBrush(palette.OnPrimary);
        SurfaceBrush = new SolidColorBrush(palette.Surface);
        OnSurfaceBrush = new SolidColorBrush(palette.OnSurface);
        ErrorBrush = new SolidColorBrush(palette.Error);
        OnErrorBrush = new SolidColorBrush(palette.OnError);
    }

    private static void ReplaceTones(
        ObservableCollection<TonalRampSwatch> target,
        IReadOnlyList<(int Tone, Color Color)> source)
    {
        target.Clear();
        foreach (var (tone, color) in source)
            target.Add(new TonalRampSwatch(tone, new SolidColorBrush(color)));
    }
}
