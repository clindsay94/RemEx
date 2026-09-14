using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Remex.Core.Theming;

namespace Remex.Desktop.Controls;

/// <summary>
/// A palette swatch, filled with its own primary/secondary/tertiary accents (spec 2026-09-13
/// personalize-tabs §3, RemEx-9ql7v; fix round 1, RemEx-4kv0g.4.1). See PaletteChip.axaml's header
/// comment for the geometry; this file owns the styled properties and the label ink. Selection is
/// entirely the host Button's ".tile.selected" ring (App.axaml:576-612) - this control has no
/// "selected" concept of its own any more.
/// </summary>
public partial class PaletteChip : UserControl
{
    /// <summary>Chip footprint. Constants, not literals, so the row XAML and the tests that pin the
    /// size (<c>PaletteChipTests</c>) read the same number this control actually lays out to.</summary>
    public const double ChipMinWidth = 150;
    public const double ChipHeight = 60;

    public static readonly StyledProperty<IBrush?> PrimaryProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(Primary));

    public static readonly StyledProperty<IBrush?> SecondaryProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(Secondary));

    public static readonly StyledProperty<IBrush?> TertiaryProperty =
        AvaloniaProperty.Register<PaletteChip, IBrush?>(nameof(Tertiary));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<PaletteChip, string?>(nameof(Label));

    // CornerRadius is NOT redeclared here - TemplatedControl (UserControl's base) already has a
    // CornerRadius StyledProperty, and redeclaring it would hide the inherited one (CS0108) rather
    // than add anything; PaletteChip.axaml's PART_OuterBorder binds straight to that inherited
    // property, so a caller sets it exactly the way it sets any other Control's CornerRadius.

    /// <summary>
    /// Read-only: the label's ink, derived from <see cref="Primary"/> below. Null when
    /// <see cref="Primary"/> isn't a solid fill - PaletteChip.axaml's plain "TextBlock#PART_Label"
    /// style keeps the app's TextPrimaryBrush as the default Foreground for that case, so the
    /// fallback is a live DynamicResource rather than a snapshot this control would have to
    /// re-derive on a theme change. A DirectProperty, not a StyledProperty: nothing outside this
    /// class ever sets it, so nothing outside this class should be able to.
    /// </summary>
    public static readonly DirectProperty<PaletteChip, IBrush?> LabelBrushProperty =
        AvaloniaProperty.RegisterDirect<PaletteChip, IBrush?>(nameof(LabelBrush), o => o.LabelBrush);

    private IBrush? _labelBrush;

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

    public IBrush? LabelBrush
    {
        get => _labelBrush;
        private set => SetAndRaise(LabelBrushProperty, ref _labelBrush, value);
    }

    public PaletteChip()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PrimaryProperty)
        {
            var brush = ResolveLabelBrush(change.GetNewValue<IBrush?>());
            LabelBrush = brush;

            // Classes, not a pseudo-class - see PaletteChip.axaml's header comment: a pseudo-class
            // match against this control's own type, from inside this same file, was verified live
            // not to reliably apply; a plain Classes match (as DashboardBackgroundControl.axaml
            // already uses) is the proven pattern. PART_Label is this UserControl's own named
            // content, not a ControlTemplate part, so InitializeComponent populates the field before
            // any caller can set Primary - the null-conditional only guards a change firing mid-load.
            PART_Label?.Classes.Set("inked", brush is not null);
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
