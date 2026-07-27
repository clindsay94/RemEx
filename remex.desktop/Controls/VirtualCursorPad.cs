using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Remex.Core.Models;

namespace Remex.Desktop.Controls;

/// <summary>
/// A floating circular cursor pad divided into four directional quadrants
/// with a center button for clicking.  Press a quadrant to nudge the remote
/// cursor; hold to auto-repeat.  Center tap = left click, long-press center = right click.
/// </summary>
public class VirtualCursorPad : Control
{
    private const double OuterRadius = 80;
    private const double InnerRadius = 26;
    private const int NudgePixels = 6;
    private const int RepeatDelayMs = 400;
    private const int RepeatIntervalMs = 60;
    private const int LongPressMs = 500;

    // StyledProperties — default values replicate the original hardcoded brushes; themes
    // can override these via style selectors and {DynamicResource} bindings.
    public static readonly StyledProperty<IBrush> PadBackgroundBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadBackgroundBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(160, 20, 20, 50)));

    public static readonly StyledProperty<IBrush> PadHoveredBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadHoveredBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(180, 50, 50, 100)));

    public static readonly StyledProperty<IBrush> PadCenterBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadCenterBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(200, 40, 40, 80)));

    public static readonly StyledProperty<IBrush> PadCenterPressedBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadCenterPressedBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(230, 80, 80, 160)));

    public static readonly StyledProperty<IBrush> PadBorderBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadBorderBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(120, 128, 128, 255)));

    public static readonly StyledProperty<IBrush> PadOuterGlowBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadOuterGlowBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(40, 128, 128, 255)));

    public static readonly StyledProperty<IBrush> PadArrowBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadArrowBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(220, 192, 192, 255)));

    public static readonly StyledProperty<IBrush> PadClickLabelBrushProperty =
        AvaloniaProperty.Register<VirtualCursorPad, IBrush>(nameof(PadClickLabelBrush),
            defaultValue: new SolidColorBrush(Color.FromArgb(220, 107, 255, 107)));

    public IBrush PadBackgroundBrush
    {
        get => GetValue(PadBackgroundBrushProperty);
        set => SetValue(PadBackgroundBrushProperty, value);
    }

    public IBrush PadHoveredBrush
    {
        get => GetValue(PadHoveredBrushProperty);
        set => SetValue(PadHoveredBrushProperty, value);
    }

    public IBrush PadCenterBrush
    {
        get => GetValue(PadCenterBrushProperty);
        set => SetValue(PadCenterBrushProperty, value);
    }

    public IBrush PadCenterPressedBrush
    {
        get => GetValue(PadCenterPressedBrushProperty);
        set => SetValue(PadCenterPressedBrushProperty, value);
    }

    public IBrush PadBorderBrush
    {
        get => GetValue(PadBorderBrushProperty);
        set => SetValue(PadBorderBrushProperty, value);
    }

    public IBrush PadOuterGlowBrush
    {
        get => GetValue(PadOuterGlowBrushProperty);
        set => SetValue(PadOuterGlowBrushProperty, value);
    }

    public IBrush PadArrowBrush
    {
        get => GetValue(PadArrowBrushProperty);
        set => SetValue(PadArrowBrushProperty, value);
    }

    public IBrush PadClickLabelBrush
    {
        get => GetValue(PadClickLabelBrushProperty);
        set => SetValue(PadClickLabelBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PadBackgroundBrushProperty ||
            change.Property == PadHoveredBrushProperty ||
            change.Property == PadCenterBrushProperty ||
            change.Property == PadCenterPressedBrushProperty ||
            change.Property == PadBorderBrushProperty ||
            change.Property == PadOuterGlowBrushProperty ||
            change.Property == PadArrowBrushProperty ||
            change.Property == PadClickLabelBrushProperty)
        {
            InvalidateVisual();
        }
    }

    private enum Region { None, Up, Down, Left, Right, Center }

    private Region _hoveredRegion = Region.None;
    private Region _pressedRegion = Region.None;
    private CancellationTokenSource? _repeatCts;
    private CancellationTokenSource? _longPressCts;

    /// <summary>
    /// Raised when the pad wants to send an input event to the remote machine.
    /// The View code-behind subscribes and forwards to the ViewModel.
    /// </summary>
    public event Action<InputEvent>? InputRequested;

    public VirtualCursorPad()
    {
        Width = OuterRadius * 2;
        Height = OuterRadius * 2;
        IsHitTestVisible = true;
        ClipToBounds = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var center = new Point(OuterRadius, OuterRadius);
        var borderPen = new Pen(PadBorderBrush, 2);
        var outerGlowPen = new Pen(PadOuterGlowBrush, 4);

        // Outer glow ring
        context.DrawEllipse(null, outerGlowPen, center, OuterRadius + 1, OuterRadius + 1);

        // Outer circle
        context.DrawEllipse(PadBackgroundBrush, borderPen, center, OuterRadius, OuterRadius);

        // Divider lines (X shape from center circle to outer edge)
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 255)), 1);
        context.DrawLine(pen, new Point(0, 0), new Point(OuterRadius * 2, OuterRadius * 2));
        context.DrawLine(pen, new Point(OuterRadius * 2, 0), new Point(0, OuterRadius * 2));

        // Highlight pressed/hovered quadrant
        DrawQuadrantHighlight(context, center);

        // Center circle
        var centerBrush = _pressedRegion == Region.Center ? PadCenterPressedBrush : PadCenterBrush;
        context.DrawEllipse(centerBrush, borderPen, center, InnerRadius, InnerRadius);

        // Directional arrows
        DrawArrow(context, center, Region.Up);
        DrawArrow(context, center, Region.Down);
        DrawArrow(context, center, Region.Left);
        DrawArrow(context, center, Region.Right);

        // Center label
        var labelText = new FormattedText("●", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 18, PadClickLabelBrush);
        context.DrawText(labelText, new Point(center.X - labelText.Width / 2, center.Y - labelText.Height / 2));
    }

    private void DrawQuadrantHighlight(DrawingContext context, Point center)
    {
        var region = _pressedRegion != Region.None ? _pressedRegion : _hoveredRegion;
        if (region == Region.None || region == Region.Center) return;

        // Draw a filled wedge for the active quadrant
        var geo = new StreamGeometry();
        using var ctx = geo.Open();

        double r = OuterRadius - 1;
        double ir = InnerRadius + 2;

        switch (region)
        {
            case Region.Up:
                ctx.BeginFigure(new Point(center.X - ir * 0.707, center.Y - ir * 0.707), true);
                ctx.ArcTo(new Point(center.X + ir * 0.707, center.Y - ir * 0.707), new Size(ir, ir), 0, false, SweepDirection.CounterClockwise);
                ctx.LineTo(new Point(center.X + r * 0.707, center.Y - r * 0.707));
                ctx.ArcTo(new Point(center.X - r * 0.707, center.Y - r * 0.707), new Size(r, r), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(true);
                break;
            case Region.Down:
                ctx.BeginFigure(new Point(center.X + ir * 0.707, center.Y + ir * 0.707), true);
                ctx.ArcTo(new Point(center.X - ir * 0.707, center.Y + ir * 0.707), new Size(ir, ir), 0, false, SweepDirection.CounterClockwise);
                ctx.LineTo(new Point(center.X - r * 0.707, center.Y + r * 0.707));
                ctx.ArcTo(new Point(center.X + r * 0.707, center.Y + r * 0.707), new Size(r, r), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(true);
                break;
            case Region.Left:
                ctx.BeginFigure(new Point(center.X - ir * 0.707, center.Y + ir * 0.707), true);
                ctx.ArcTo(new Point(center.X - ir * 0.707, center.Y - ir * 0.707), new Size(ir, ir), 0, false, SweepDirection.CounterClockwise);
                ctx.LineTo(new Point(center.X - r * 0.707, center.Y - r * 0.707));
                ctx.ArcTo(new Point(center.X - r * 0.707, center.Y + r * 0.707), new Size(r, r), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(true);
                break;
            case Region.Right:
                ctx.BeginFigure(new Point(center.X + ir * 0.707, center.Y - ir * 0.707), true);
                ctx.ArcTo(new Point(center.X + ir * 0.707, center.Y + ir * 0.707), new Size(ir, ir), 0, false, SweepDirection.CounterClockwise);
                ctx.LineTo(new Point(center.X + r * 0.707, center.Y + r * 0.707));
                ctx.ArcTo(new Point(center.X + r * 0.707, center.Y - r * 0.707), new Size(r, r), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(true);
                break;
        }

        context.DrawGeometry(PadHoveredBrush, null, geo);
    }

    private void DrawArrow(DrawingContext context, Point center, Region direction)
    {
        double midR = (OuterRadius + InnerRadius) / 2;
        Point tip;
        Point left;
        Point right;
        double arrowSize = 10;

        switch (direction)
        {
            case Region.Up:
                tip = new Point(center.X, center.Y - midR - arrowSize / 2);
                left = new Point(tip.X - arrowSize, tip.Y + arrowSize);
                right = new Point(tip.X + arrowSize, tip.Y + arrowSize);
                break;
            case Region.Down:
                tip = new Point(center.X, center.Y + midR + arrowSize / 2);
                left = new Point(tip.X + arrowSize, tip.Y - arrowSize);
                right = new Point(tip.X - arrowSize, tip.Y - arrowSize);
                break;
            case Region.Left:
                tip = new Point(center.X - midR - arrowSize / 2, center.Y);
                left = new Point(tip.X + arrowSize, tip.Y - arrowSize);
                right = new Point(tip.X + arrowSize, tip.Y + arrowSize);
                break;
            case Region.Right:
                tip = new Point(center.X + midR + arrowSize / 2, center.Y);
                left = new Point(tip.X - arrowSize, tip.Y + arrowSize);
                right = new Point(tip.X - arrowSize, tip.Y - arrowSize);
                break;
            default:
                return;
        }

        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        ctx.BeginFigure(tip, true);
        ctx.LineTo(left);
        ctx.LineTo(right);
        ctx.EndFigure(true);
        context.DrawGeometry(PadArrowBrush, null, geo);
    }

    private Region HitTest(Point pos)
    {
        double dx = pos.X - OuterRadius;
        double dy = pos.Y - OuterRadius;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist > OuterRadius) return Region.None;
        if (dist <= InnerRadius) return Region.Center;

        // Quadrant: use the dominant axis
        if (Math.Abs(dx) > Math.Abs(dy))
            return dx > 0 ? Region.Right : Region.Left;
        else
            return dy > 0 ? Region.Down : Region.Up;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var pos = e.GetPosition(this);
        var region = HitTest(pos);
        if (region == Region.None) return;

        _pressedRegion = region;
        InvalidateVisual();
        e.Handled = true;

        if (region == Region.Center)
        {
            // Start long-press timer for right-click
            _longPressCts?.Cancel();
            _longPressCts = new CancellationTokenSource();
            var cts = _longPressCts;
            _ = System.Threading.Tasks.Task.Delay(LongPressMs, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        InputRequested?.Invoke(new InputEvent
                        {
                            EventType = InputEventTypes.MouseClick,
                            Button = 2,
                        });
                        _pressedRegion = Region.None;
                        InvalidateVisual();
                    });
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        else
        {
            // Send first nudge immediately
            SendNudge(region);

            // Start repeat timer
            _repeatCts?.Cancel();
            _repeatCts = new CancellationTokenSource();
            var cts = _repeatCts;
            _ = RepeatNudgeAsync(region, cts.Token);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var pos = e.GetPosition(this);
        var region = HitTest(pos);

        if (_pressedRegion == Region.None)
        {
            if (region != _hoveredRegion)
            {
                _hoveredRegion = region;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _repeatCts?.Cancel();
        _repeatCts = null;

        if (_pressedRegion == Region.Center && _longPressCts is not null)
        {
            // If long-press did not fire, this is a quick tap → left click
            _longPressCts.Cancel();
            _longPressCts = null;

            InputRequested?.Invoke(new InputEvent
            {
                EventType = InputEventTypes.MouseClick,
                Button = 0,
            });
        }

        _longPressCts?.Cancel();
        _longPressCts = null;

        _pressedRegion = Region.None;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoveredRegion = Region.None;
        InvalidateVisual();
    }

    private void SendNudge(Region direction)
    {
        int dx = 0, dy = 0;
        switch (direction)
        {
            case Region.Up: dy = -NudgePixels; break;
            case Region.Down: dy = NudgePixels; break;
            case Region.Left: dx = -NudgePixels; break;
            case Region.Right: dx = NudgePixels; break;
        }

        InputRequested?.Invoke(new InputEvent
        {
            EventType = InputEventTypes.MouseMove,
            DeltaX = dx,
            DeltaY = dy,
        });
    }

    private async System.Threading.Tasks.Task RepeatNudgeAsync(Region direction, CancellationToken ct)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(RepeatDelayMs, ct);
            while (!ct.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SendNudge(direction));
                await System.Threading.Tasks.Task.Delay(RepeatIntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { /* cancellation is how the pad stops, not a failure */ }
    }
}
