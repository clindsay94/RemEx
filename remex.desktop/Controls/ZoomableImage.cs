using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Remex.Desktop.Controls;

/// <summary>
/// The full-size image in the File Transfer preview (2026-10-08 redesign): fitted to start with; the wheel zooms
/// around the pointer, dragging pans, and a double-click fits it again. Clips to its own bounds.
/// </summary>
public sealed class ZoomableImage : Border
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ZoomableImage, IImage?>(nameof(Source));

    private const double MinZoom = 1.0;
    private const double MaxZoom = 16.0;
    private const double WheelStep = 1.2;

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private double _zoom = 1;
    private Vector _offset;
    private Point? _dragFrom;

    public ZoomableImage()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // hit-testable everywhere, so the whole area pans
        Child = _image;
        _image.RenderTransformOrigin = RelativePoint.TopLeft;
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The current zoom, 1 = fitted.</summary>
    public double Zoom => _zoom;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _image.Source = Source;
            Fit();
        }
    }

    /// <summary>Back to fitted, centred.</summary>
    public void Fit()
    {
        _zoom = 1;
        _offset = default;
        Apply();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? WheelStep : 1 / WheelStep;
        ZoomAround(e.GetPosition(this), factor);
        e.Handled = true;
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the image point under <paramref name="anchor"/> still.</summary>
    public void ZoomAround(Point anchor, double factor)
    {
        var next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 1e-9)
            return;
        var applied = next / _zoom;
        _offset = new Vector(anchor.X - (anchor.X - _offset.X) * applied, anchor.Y - (anchor.Y - _offset.Y) * applied);
        _zoom = next;
        if (_zoom <= MinZoom)
            _offset = default;
        Apply();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount >= 2)
        {
            Fit();
            e.Handled = true;
            return;
        }
        if (_zoom > MinZoom && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var now = e.GetPosition(this);
        _offset += now - from;
        _dragFrom = now;
        Apply();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null)
            return;
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    private void Apply()
    {
        _image.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(_zoom, _zoom),
                new TranslateTransform(_offset.X, _offset.Y),
            },
        };
    }
}
