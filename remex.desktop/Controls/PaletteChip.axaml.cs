using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Remex.Core.Theming;

namespace Remex.Desktop.Controls;

/// <summary>
/// A palette swatch, filled with its own primary/secondary/tertiary accents (spec 2026-09-13
/// personalize-tabs §3, RemEx-9ql7v). See PaletteChip.axaml's header comment for the geometry and
/// the selection-ring trick; this file owns the styled properties, the "selected" class, and
/// the label ink.
/// </summary>
public partial class PaletteChip : UserControl
{
    /// <summary>Chip footprint. Constants, not literals, so the row XAML and the tests that pin the
    /// size (<c>PaletteChipTests</c>) read the same number this control actually lays out to.</summary>
    public const double ChipWidth = 104;
    public const double ChipHeight = 60;

    public static readonly StyledProperty<IBrush?> PrimaryProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(Primary));

    public static readonly StyledProperty<IBrush?> SecondaryProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(Secondary));

    public static readonly StyledProperty<IBrush?> TertiaryProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(Tertiary));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<PaletteChip, string?>(nameof(Label));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<PaletteChip, bool>(nameof(IsSelected));

    // CornerRadius is NOT redeclared here - TemplatedControl (UserControl's base) already has a
    // CornerRadius StyledProperty, and redeclaring it would hide the inherited one (CS0108) rather
    // than add anything; PaletteChip.axaml's PART_OuterBorder binds straight to that inherited
    // property, so a caller sets it exactly the way it sets any other Control's CornerRadius.

    /// <summary>
    /// Read-only: the label's ink, derived from <see cref="Primary"/> below. Null when
    /// <see cref="Primary"/> isn't a solid fill - PaletteChip.axaml's Foreground binding falls back
    /// to the app's TextPrimaryBrush for that case via TargetNullValue, which keeps the fallback a
    /// live DynamicResource rather than a snapshot this control would have to re-derive on a theme
    /// change.
    /// </summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(LabelBrush));

    public IBrush? Primary
    {
        get => GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public IBrush? Secondary
    {
        get => GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    public IBrush? Tertiary
    {
        get => GetValue(TertiaryProperty);
        set => SetValue(TertiaryProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public IBrush? LabelBrush => GetValue(LabelBrushProperty);

    public PaletteChip()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsSelectedProperty)
        {
            // Classes, not a pseudo-class - see PaletteChip.axaml's header comment for why (a
            // pseudo-class match against this control's own type, from inside this same file, was
            // verified live not to reliably apply; a plain Classes match is the proven pattern).
            Classes.Set("selected", change.GetNewValue<bool>());
        }
        else if (change.Property == PrimaryProperty)
        {
            SetValue(LabelBrushProperty, ResolveLabelBrush(change.GetNewValue<IBrush?>()));
        }
    }

    /// <summary>
    /// Black or white by <see cref="ChipInk"/> against <paramref name="primary"/>'s own colour - the
    /// same tone-based call the palette engine already made when it decided <c>Primary</c> was safe
    /// to paint text on. Anything that isn't a single solid colour (null, a gradient) has no one
    /// colour to read a tone from, so this returns null and lets the DynamicResource fallback in
    /// PaletteChip.axaml handle it instead of guessing.
    /// </summary>
    private static IBrush? ResolveLabelBrush(IBrush? primary)
    {
        if (primary is not ISolidColorBrush solid) return null;

        return ChipInk.For(solid.Color.ToUInt32()) == 0xFF000000u ? Brushes.Black : Brushes.White;
    }
}
