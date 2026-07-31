using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Controls;

/// <summary>
/// A 200×160 px custom-drawn overview of the entire 4000×4000 canvas.
/// Shows card positions and a viewport indicator rectangle.
/// Clicking inside pans the viewport to the clicked world position.
/// </summary>
public class CanvasMinimap : Control
{
    // ═══════════════ Constants ═══════════════

    private const double WorldWidth  = 4000;
    private const double WorldHeight = 4000;

    // ═══════════════ Styled Properties ═══════════════

    public static readonly StyledProperty<ObservableCollection<CanvasCardViewModel>?> CardsProperty =
        AvaloniaProperty.Register<CanvasMinimap, ObservableCollection<CanvasCardViewModel>?>(nameof(Cards));

    public static readonly StyledProperty<Rect> ViewportRectProperty =
        AvaloniaProperty.Register<CanvasMinimap, Rect>(nameof(ViewportRect));

    public ObservableCollection<CanvasCardViewModel>? Cards
    {
        get => GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
    }

    /// <summary>Viewport rectangle in minimap coordinate space (pixels within this control).</summary>
    public Rect ViewportRect
    {
        get => GetValue(ViewportRectProperty);
        set => SetValue(ViewportRectProperty, value);
    }

    /// <summary>Fired with (worldX, worldY) when the user clicks inside the minimap.</summary>
    public event Action<double, double>? PanRequested;

    // ═══════════════ Brushes (resolved per render) ═══════════════
    //
    // These were static readonly brushes built from hex literals, which made the minimap draw the
    // same dark slab on all four themes — visibly wrong on SolarFlare (RemEx-fy0a). They are now
    // resolved from the active theme on every Render, because a static field is captured once at
    // type-initialisation and would survive every subsequent theme switch.
    //
    // The COLOUR is taken from the theme and the OPACITY re-applied here, rather than asking the
    // theme for a pre-alphaed brush: these four opacities are properties of the minimap's own
    // layering (a plate, a translucent card, a louder alert, a barely-there viewport wash), not of
    // the palette, and every theme would otherwise have to ship a variant of each.
    //
    // The original literals are kept as the fallbacks, so a missing key degrades to exactly what
    // this control drew before rather than to null.

    private static IBrush BackgroundBrush => Plate("GlassBaseDark", "#1A1A2E", 0.92);
    private static IBrush CardBrush       => Plate("AccentPrimary", "#4466AA", 0.55);
    private static IBrush AlertCardBrush  => Plate("SystemError",   "#FF4444", 0.80);
    private static IBrush ViewportFill    => Plate("SystemSuccess", "#4ADE80", 0.10);
    private static IPen   ViewportPen     => new Pen(Plate("SystemSuccess", "#4ADE80", 1.0), 1.5);

    // OpaqueColor, not Color: SolidColorBrush.Opacity MULTIPLIES with the colour's own alpha, and
    // GlassBaseDark is already translucent on two of the four themes (#A00A0A10, #D90A0A10). Taking
    // it as-is would have made this plate 0.58 opaque on the default theme instead of 0.92 — more
    // transparent than the literal it replaced, on some themes and not others.
    private static IBrush Plate(string key, string fallbackHex, double opacity)
        => new SolidColorBrush(ThemeResources.OpaqueColor(key, Color.Parse(fallbackHex)), opacity);

    // ═══════════════ Statics ═══════════════

    static CanvasMinimap()
    {
        AffectsRender<CanvasMinimap>(CardsProperty, ViewportRectProperty);
    }

    // ═══════════════ Property changes ═══════════════

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CardsProperty)
        {
            if (change.OldValue is ObservableCollection<CanvasCardViewModel> old)
                old.CollectionChanged -= OnCardsChanged;

            if (change.NewValue is ObservableCollection<CanvasCardViewModel> newCards)
                newCards.CollectionChanged += OnCardsChanged;

            InvalidateVisual();
        }
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    /// <summary>
    /// Redraws when the theme changes. Resolving colours per render is necessary but NOT sufficient
    /// for a drawing control: nothing else invalidates it on a theme switch, so without this the
    /// minimap would keep showing the previous theme's colours until some unrelated change happened
    /// to trigger a repaint. Controls that bind through <c>DynamicResource</c> get this for free;
    /// one that paints in <see cref="Render"/> does not. (RemEx-fy0a.)
    /// </summary>
    /// <remarks>
    /// <c>ResourcesChanged</c> and deliberately NOT <c>ActualThemeVariantChanged</c>, which would be
    /// dead code for most switches: <c>ThemeService.ApplyBaseThemeInternal</c> sets
    /// <c>RequestedThemeVariant</c> to Dark for BaseDarkGlass, CyberNOC AND Monolith, and Light only
    /// for SolarFlare — so switching between the three dark themes assigns the variant the value it
    /// already had and raises nothing. It always swaps the merged dictionaries, though, so
    /// <c>ResourcesChanged</c> fires on every switch. It also covers the accent override and the
    /// hardware-accent path, which change colours with no variant change at all.
    /// <para>Subscribed rather than overridden: the event has no virtual counterpart here.
    /// Subscribing to its own event keeps no reference anything else can hold, so there is nothing
    /// to unsubscribe.</para>
    /// </remarks>
    public CanvasMinimap()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    // ═══════════════ Render ═══════════════

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;

        if (w <= 0 || h <= 0) return;

        double scaleX = w / WorldWidth;
        double scaleY = h / WorldHeight;

        // Background
        context.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));

        // Cards. Both brushes are hoisted out of the loop: each access is a resource lookup and a
        // fresh SolidColorBrush, which the static fields these replaced did not cost per card.
        var cardBrush = CardBrush;
        var alertBrush = AlertCardBrush;

        if (Cards != null)
        {
            foreach (var card in Cards)
            {
                double cx = card.PositionX * scaleX;
                double cy = card.PositionY * scaleY;
                double cw = Math.Max(2, card.Width  * scaleX);
                double ch = Math.Max(2, card.Height * scaleY);

                var brush = card.IsAlertActive ? alertBrush : cardBrush;
                context.DrawRectangle(brush, null, new Rect(cx, cy, cw, ch));
            }
        }

        // Viewport indicator
        var vr = ViewportRect;
        if (vr.Width > 0 && vr.Height > 0)
            context.DrawRectangle(ViewportFill, ViewportPen, vr);
    }

    // ═══════════════ Click-to-pan ═══════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var pos = e.GetPosition(this);
        double w = Bounds.Width;
        double h = Bounds.Height;

        if (w > 0 && h > 0)
        {
            double worldX = pos.X / w * WorldWidth;
            double worldY = pos.Y / h * WorldHeight;
            PanRequested?.Invoke(worldX, worldY);
        }

        e.Handled = true;
    }
}
