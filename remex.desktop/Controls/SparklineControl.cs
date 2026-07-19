using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Remex.Core.Models;

namespace Remex.Desktop.Controls;

/// <summary>
/// Custom sparkline control that renders sensor history in multiple graph styles.
/// Uses DrawingContext for efficient rendering.
/// </summary>
public class SparklineControl : Control
{
    // ═══════════════ Styled Properties ═══════════════

    public static readonly StyledProperty<IList<double>?> HistoryProperty =
        AvaloniaProperty.Register<SparklineControl, IList<double>?>(nameof(History));

    public static readonly StyledProperty<GraphType> GraphTypeProperty =
        AvaloniaProperty.Register<SparklineControl, GraphType>(nameof(GraphType), GraphType.Bar);

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<SparklineControl, Color>(nameof(AccentColor), Color.Parse("#C0C0FF"));

    public static readonly StyledProperty<double> CurrentValueProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(CurrentValue));

    public static readonly StyledProperty<double> MinSeenProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(MinSeen));

    public static readonly StyledProperty<double> MaxSeenProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(MaxSeen));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(TrackBrush));

    /// <summary>Second normalized (0–1) history series, drawn by <see cref="GraphType.DualMetric"/>.</summary>
    public static readonly StyledProperty<IList<double>?> SecondaryHistoryProperty =
        AvaloniaProperty.Register<SparklineControl, IList<double>?>(nameof(SecondaryHistory));

    /// <summary>Accent for the secondary series in <see cref="GraphType.DualMetric"/>.</summary>
    public static readonly StyledProperty<Color> SecondaryAccentColorProperty =
        AvaloniaProperty.Register<SparklineControl, Color>(nameof(SecondaryAccentColor), Color.Parse("#FFB020"));

    private const byte AreaFillAlpha = 60;
    private const byte GlowAlpha = 100;
    private const byte TrackAlpha = 40;

    private ISolidColorBrush? _accentBrush;
    private ISolidColorBrush? _areaFillBrush;
    private ISolidColorBrush? _glowBrush;
    private Pen? _linePen;

    public IList<double>? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public GraphType GraphType
    {
        get => GetValue(GraphTypeProperty);
        set => SetValue(GraphTypeProperty, value);
    }

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public double CurrentValue
    {
        get => GetValue(CurrentValueProperty);
        set => SetValue(CurrentValueProperty, value);
    }

    public double MinSeen
    {
        get => GetValue(MinSeenProperty);
        set => SetValue(MinSeenProperty, value);
    }

    public double MaxSeen
    {
        get => GetValue(MaxSeenProperty);
        set => SetValue(MaxSeenProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IList<double>? SecondaryHistory
    {
        get => GetValue(SecondaryHistoryProperty);
        set => SetValue(SecondaryHistoryProperty, value);
    }

    public Color SecondaryAccentColor
    {
        get => GetValue(SecondaryAccentColorProperty);
        set => SetValue(SecondaryAccentColorProperty, value);
    }

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(HistoryProperty, GraphTypeProperty, AccentColorProperty,
            CurrentValueProperty, MinSeenProperty, MaxSeenProperty, TrackBrushProperty,
            SecondaryHistoryProperty, SecondaryAccentColorProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AccentColorProperty)
        {
            UpdateBrushes();
        }

        if (change.Property == HistoryProperty)
        {
            // Unsubscribe from old collection
            if (change.OldValue is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= OnHistoryCollectionChanged;

            // Subscribe to new collection
            if (change.NewValue is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += OnHistoryCollectionChanged;

            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Unsubscribe from the live data collection when recycled or removed,
        // so virtualized items don't keep accumulating listeners.
        if (History is INotifyCollectionChanged ncc)
            ncc.CollectionChanged -= OnHistoryCollectionChanged;
    }

    private void UpdateBrushes()
    {
        var accent = AccentColor;
        _accentBrush = new ImmutableSolidColorBrush(accent);
        _areaFillBrush = new ImmutableSolidColorBrush(new Color(AreaFillAlpha, accent.R, accent.G, accent.B));
        _glowBrush = new ImmutableSolidColorBrush(new Color(GlowAlpha, accent.R, accent.G, accent.B));
        _linePen = new Pen(_accentBrush, 1.5);
    }

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        switch (GraphType)
        {
            case GraphType.Bar:
                RenderBars(context, bounds);
                break;
            case GraphType.Line:
            case GraphType.BigValue:   // retired view — renders as a line (saved-layout compat)
                RenderLine(context, bounds, filled: false);
                break;
            case GraphType.Gauge:
                RenderGauge(context, bounds);
                break;
            case GraphType.Radial:     // retired view — renders as the ring gauge (saved-layout compat)
            case GraphType.Ring:
                RenderRing(context, bounds);
                break;
            case GraphType.LedMeter:
                RenderLedMeter(context, bounds);
                break;
            case GraphType.Area:       // retired view — renders as glow area (saved-layout compat)
            case GraphType.ValueSpark: // retired view — renders as glow area (saved-layout compat)
            case GraphType.GlowArea:
            case GraphType.HuePulse:   // retired view — renders as glow area (saved-layout compat)
                RenderGlowArea(context, bounds);
                break;
            case GraphType.DualMetric:
                RenderDualMetric(context, bounds);
                break;
            default:
                RenderBars(context, bounds);
                break;
        }
    }

    // ═══════════════ Ring Gauge (bold full ring) ═══════════════

    private void RenderRing(DrawingContext context, Rect bounds)
    {
        double range = MaxSeen - MinSeen;
        double fraction = range > 0 ? Math.Clamp((CurrentValue - MinSeen) / range, 0, 1) : 0;

        if (_accentBrush == null) UpdateBrushes();

        double size = Math.Min(bounds.Width, bounds.Height);
        double centerX = bounds.Width / 2;
        double centerY = bounds.Height / 2;
        double strokeWidth = size * 0.16;
        double radius = (size - strokeWidth) / 2 - 1;
        if (radius <= 0) return;

        var trackPen = new Pen(TrackBrush ?? new ImmutableSolidColorBrush(new Color(TrackAlpha, 128, 128, 128)), strokeWidth);
        var accentPen = new Pen(_accentBrush!, strokeWidth, lineCap: PenLineCap.Round);

        // Full track ring.
        context.DrawEllipse(null, trackPen, new Point(centerX, centerY), radius, radius);

        if (fraction > 0)
        {
            double sweepAngle = Math.Min(fraction * 360, 359.999);
            var geometry = new StreamGeometry();
            Point endPt;
            using (var ctx = geometry.Open())
            {
                double startAngle = -90; // top center
                double endAngle = startAngle + sweepAngle;
                double startRad = startAngle * Math.PI / 180;
                double endRad = endAngle * Math.PI / 180;

                Point startPt = new(centerX + radius * Math.Cos(startRad), centerY + radius * Math.Sin(startRad));
                endPt = new(centerX + radius * Math.Cos(endRad), centerY + radius * Math.Sin(endRad));

                ctx.BeginFigure(startPt, false);
                ctx.ArcTo(endPt, new Size(radius, radius), 0, sweepAngle > 180, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, accentPen, geometry);

            // Glow at the leading tip.
            context.DrawEllipse(_glowBrush!, null, endPt, strokeWidth * 0.6, strokeWidth * 0.6);
        }
    }

    // ═══════════════ Segmented LED Meter ═══════════════

    private void RenderLedMeter(DrawingContext context, Rect bounds)
    {
        double range = MaxSeen - MinSeen;
        double fraction = range > 0 ? Math.Clamp((CurrentValue - MinSeen) / range, 0, 1) : 0;

        if (_accentBrush == null) UpdateBrushes();

        const int segments = 14;
        const double gapRatio = 0.28;                 // total horizontal space given to gaps
        double totalGap = bounds.Width * gapRatio;
        double segW = (bounds.Width - totalGap) / segments;
        double gap = segments > 1 ? totalGap / (segments - 1) : 0;
        if (segW <= 0) return;

        double barH = bounds.Height * 0.55;
        double y = (bounds.Height - barH) / 2;

        int litCount = (int)Math.Round(fraction * segments);
        var trackBrush = TrackBrush ?? new ImmutableSolidColorBrush(new Color(TrackAlpha, 128, 128, 128));
        var hotBrush = new ImmutableSolidColorBrush(Color.Parse("#FF3B30"));

        for (int i = 0; i < segments; i++)
        {
            double x = i * (segW + gap);
            bool lit = i < litCount;
            bool isHotSegment = i >= segments - 2;    // the top ~15% of the scale

            IBrush brush = !lit ? trackBrush : (isHotSegment ? hotBrush : _accentBrush!);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, y, segW, barH), 2));
        }
    }

    // ═══════════════ Glow Area (gradient fill + neon top edge) ═══════════════

    private void RenderGlowArea(DrawingContext context, Rect bounds)
    {
        var data = History;
        if (data == null || data.Count < 2) return;

        if (_accentBrush == null) UpdateBrushes();

        // The glow shifts from the card's accent (idle) toward warm red as the reading climbs,
        // so the tile visibly reacts to load — not just a static-colored area chart.
        double range = MaxSeen - MinSeen;
        double fraction = range > 0 ? Math.Clamp((CurrentValue - MinSeen) / range, 0, 1) : 0;
        var accent = LerpColor(AccentColor, Color.Parse("#FF3B30"), fraction * 0.8);
        var accentBrush = new ImmutableSolidColorBrush(accent);

        int count = data.Count;
        double maxHeight = bounds.Height;
        double stepX = bounds.Width / Math.Max(count - 1, 1);

        var points = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            double h = Math.Clamp(data[i] * maxHeight, 0, maxHeight);
            points.Add(new Point(i * stepX, bounds.Height - h));
        }

        // Vertical gradient fill under the curve.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, bounds.Height), true);
            foreach (var pt in points) ctx.LineTo(pt);
            ctx.LineTo(new Point(points[^1].X, bounds.Height));
            ctx.EndFigure(true);
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(new Color(150, accent.R, accent.G, accent.B), 0),
                new GradientStop(new Color(10, accent.R, accent.G, accent.B), 1),
            },
        };
        context.DrawGeometry(gradient, null, geometry);

        // Neon top edge: a thick low-alpha glow pass under a thin crisp line (both value-reactive).
        var glowPen = new Pen(new ImmutableSolidColorBrush(new Color(90, accent.R, accent.G, accent.B)), 4);
        var crispPen = new Pen(accentBrush, 1.5);
        for (int i = 1; i < points.Count; i++)
            context.DrawLine(glowPen, points[i - 1], points[i]);
        for (int i = 1; i < points.Count; i++)
            context.DrawLine(crispPen, points[i - 1], points[i]);
    }


    // ═══════════════ Dual Metric (two overlaid series) ═══════════════

    private void RenderDualMetric(DrawingContext context, Rect bounds)
    {
        var primary = History;
        if (primary == null || primary.Count < 2) return;
        if (_accentBrush == null) UpdateBrushes();

        DrawSeriesLine(context, bounds, primary, _accentBrush!, filledAlpha: 40);

        var secondary = SecondaryHistory;
        if (secondary != null && secondary.Count >= 2)
        {
            var secAccent = SecondaryAccentColor;
            var secBrush = new ImmutableSolidColorBrush(secAccent);
            DrawSeriesLine(context, bounds, secondary, secBrush, filledAlpha: 0);
        }
    }

    /// <summary>Draws a normalized (0–1) series as a line, optionally with a faint area fill.</summary>
    private static void DrawSeriesLine(DrawingContext context, Rect bounds, IList<double> data,
        ISolidColorBrush brush, byte filledAlpha)
    {
        int count = data.Count;
        double stepX = bounds.Width / Math.Max(count - 1, 1);
        var points = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            double h = Math.Clamp(data[i] * bounds.Height, 0, bounds.Height);
            points.Add(new Point(i * stepX, bounds.Height - h));
        }

        if (filledAlpha > 0)
        {
            var c = brush.Color;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, bounds.Height), true);
                foreach (var pt in points) ctx.LineTo(pt);
                ctx.LineTo(new Point(points[^1].X, bounds.Height));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(new ImmutableSolidColorBrush(new Color(filledAlpha, c.R, c.G, c.B)), null, geometry);
        }

        var pen = new Pen(brush, 1.5);
        for (int i = 1; i < points.Count; i++)
            context.DrawLine(pen, points[i - 1], points[i]);
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    // ═══════════════ Bar Chart ═══════════════

    private void RenderBars(DrawingContext context, Rect bounds)
    {
        var data = History;
        if (data == null || data.Count == 0) return;

        if (_accentBrush == null) UpdateBrushes();

        int count = data.Count;
        double barWidth = Math.Max(2, (bounds.Width / Math.Max(count, 1)) - 1);
        double maxHeight = bounds.Height;

        for (int i = 0; i < count; i++)
        {
            double h = Math.Clamp(data[i] * maxHeight, 0, maxHeight);
            double x = i * (barWidth + 1);
            double y = bounds.Height - h;

            context.DrawRectangle(_accentBrush!, null,
                new RoundedRect(new Rect(x, y, barWidth, h), 1, 1, 0, 0));
        }
    }

    // ═══════════════ Line / Area Chart ═══════════════

    private void RenderLine(DrawingContext context, Rect bounds, bool filled)
    {
        var data = History;
        if (data == null || data.Count < 2) return;

        if (_accentBrush == null) UpdateBrushes();

        int count = data.Count;
        double maxHeight = bounds.Height;
        double stepX = bounds.Width / Math.Max(count - 1, 1);

        var points = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            double h = Math.Clamp(data[i] * maxHeight, 0, maxHeight);
            double x = i * stepX;
            double y = bounds.Height - h;
            points.Add(new Point(x, y));
        }

        if (filled && points.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, bounds.Height), true);
                foreach (var pt in points)
                    ctx.LineTo(pt);
                ctx.LineTo(new Point(points[^1].X, bounds.Height));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(_areaFillBrush!, null, geometry);
        }

        // Draw the line
        for (int i = 1; i < points.Count; i++)
        {
            context.DrawLine(_linePen!, points[i - 1], points[i]);
        }

        // Draw dots at each point
        foreach (var pt in points)
        {
            context.DrawEllipse(_accentBrush!, null, pt, 1.5, 1.5);
        }
    }

    // ═══════════════ Gauge (Horizontal Fill) ═══════════════

    private void RenderGauge(DrawingContext context, Rect bounds)
    {
        double range = MaxSeen - MinSeen;
        double fraction = range > 0 ? Math.Clamp((CurrentValue - MinSeen) / range, 0, 1) : 0;

        if (_accentBrush == null) UpdateBrushes();

        // Background track
        var bgRect = new Rect(0, bounds.Height * 0.35, bounds.Width, bounds.Height * 0.3);
        var trackBrush = TrackBrush ?? new ImmutableSolidColorBrush(new Color(TrackAlpha, 128, 128, 128));
        context.DrawRectangle(trackBrush, null, new RoundedRect(bgRect, 3));

        // Fill bar
        var fillWidth = fraction * bounds.Width;
        var fillRect = new Rect(0, bounds.Height * 0.35, fillWidth, bounds.Height * 0.3);
        context.DrawRectangle(_accentBrush!, null, new RoundedRect(fillRect, 3));

        // Glow at the leading edge
        if (fillWidth > 2)
        {
            var glowRect = new Rect(fillWidth - 4, bounds.Height * 0.25, 8, bounds.Height * 0.5);
            context.DrawRectangle(_glowBrush!, null, new RoundedRect(glowRect, 4));
        }
    }
}
