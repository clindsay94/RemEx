using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    // ═══════════════ Brushes (cached) ═══════════════

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#1A1A2E"), 0.92);
    private static readonly IBrush CardBrush       = new SolidColorBrush(Color.Parse("#4466AA"), 0.55);
    private static readonly IBrush AlertCardBrush  = new SolidColorBrush(Color.Parse("#FF4444"), 0.80);
    private static readonly IBrush ViewportFill    = new SolidColorBrush(Color.Parse("#4ADE80"),  0.10);
    private static readonly IPen  ViewportPen      = new Pen(new SolidColorBrush(Color.Parse("#4ADE80")), 1.5);

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

        // Cards
        if (Cards != null)
        {
            foreach (var card in Cards)
            {
                double cx = card.PositionX * scaleX;
                double cy = card.PositionY * scaleY;
                double cw = Math.Max(2, card.Width  * scaleX);
                double ch = Math.Max(2, card.Height * scaleY);

                var brush = card.IsAlertActive ? AlertCardBrush : CardBrush;
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
