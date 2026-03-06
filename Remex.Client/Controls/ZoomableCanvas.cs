using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Remex.Client.Controls;

/// <summary>
/// A panel that supports two-finger pan, pinch-to-zoom, and mouse-wheel zoom.
/// Replaces ScrollViewer for the canvas workspace.
/// Single-finger touch passes through to children (e.g. DraggableCard).
/// Place a single child (e.g. ItemsControl) inside this panel.
/// </summary>
public class ZoomableCanvas : Panel
{
    // ═══════════════ Transform state ═══════════════

    private readonly MatrixTransform _childTransform = new();
    private double _zoom = 1.0;
    private double _offsetX;
    private double _offsetY;

    private const double MinZoom = 0.2;
    private const double MaxZoom = 3.0;
    private const double WheelZoomFactor = 0.1;

    // ═══════════════ Multi-touch tracking ═══════════════

    private readonly Dictionary<long, Point> _activePointers = new();

    // Two-finger gesture state
    private double _gestureStartDistance;
    private double _gestureStartZoom;
    private Point _gestureStartCenter;
    private double _gestureStartOffsetX;
    private double _gestureStartOffsetY;
    private bool _isMultiTouchGesture;

    // ═══════════════ Middle-click pan (desktop) ═══════════════

    private bool _isMiddlePanning;
    private Point _middlePanStart;
    private double _middlePanStartOffsetX;
    private double _middlePanStartOffsetY;

    public ZoomableCanvas()
    {
        ClipToBounds = true;
    }

    // ═══════════════ Layout ═══════════════

    /// <summary>
    /// Measure child at infinite so it can expand to its full desired size (e.g. 4000x4000 Canvas).
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
        }
        // Return the available size (the viewport), not the child's desired size.
        return availableSize;
    }

    /// <summary>
    /// Arrange child at 0,0 with its full desired size, then apply the pan/zoom transform.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.RenderTransform = _childTransform;
            child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
            child.Arrange(new Rect(0, 0, child.DesiredSize.Width, child.DesiredSize.Height));
        }
        return finalSize;
    }

    // ═══════════════ Pointer events ═══════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        var props = point.Properties;

        // Middle-click pan (desktop)
        if (props.IsMiddleButtonPressed)
        {
            _isMiddlePanning = true;
            _middlePanStart = e.GetPosition(this);
            _middlePanStartOffsetX = _offsetX;
            _middlePanStartOffsetY = _offsetY;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Track touch pointers for multi-touch
        if (e.Pointer.Type == PointerType.Touch)
        {
            _activePointers[e.Pointer.Id] = e.GetPosition(this);

            if (_activePointers.Count == 2)
            {
                StartMultiTouchGesture();
                e.Handled = true;
            }
            // Single finger: let it pass through to children (DraggableCard)
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Middle-click pan
        if (_isMiddlePanning)
        {
            var current = e.GetPosition(this);
            _offsetX = _middlePanStartOffsetX + (current.X - _middlePanStart.X);
            _offsetY = _middlePanStartOffsetY + (current.Y - _middlePanStart.Y);
            UpdateTransform();
            e.Handled = true;
            return;
        }

        // Multi-touch gesture
        if (e.Pointer.Type == PointerType.Touch && _activePointers.ContainsKey(e.Pointer.Id))
        {
            _activePointers[e.Pointer.Id] = e.GetPosition(this);

            if (_isMultiTouchGesture && _activePointers.Count >= 2)
            {
                UpdateMultiTouchGesture();
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Middle-click pan end
        if (_isMiddlePanning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isMiddlePanning = false;
            e.Handled = true;
            return;
        }

        // Multi-touch cleanup
        if (e.Pointer.Type == PointerType.Touch)
        {
            _activePointers.Remove(e.Pointer.Id);

            if (_activePointers.Count < 2)
            {
                _isMultiTouchGesture = false;
            }
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isMiddlePanning = false;
        _activePointers.Clear();
        _isMultiTouchGesture = false;
    }

    // ═══════════════ Mouse wheel zoom ═══════════════

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var cursorPos = e.GetPosition(this);

        // World-space position under cursor before zoom
        var worldX = (cursorPos.X - _offsetX) / _zoom;
        var worldY = (cursorPos.Y - _offsetY) / _zoom;

        // Apply zoom
        var delta = e.Delta.Y > 0 ? WheelZoomFactor : -WheelZoomFactor;
        _zoom = Math.Clamp(_zoom + delta * _zoom, MinZoom, MaxZoom);

        // Adjust offset so the world point stays under the cursor
        _offsetX = cursorPos.X - worldX * _zoom;
        _offsetY = cursorPos.Y - worldY * _zoom;

        UpdateTransform();
        e.Handled = true;
    }

    // ═══════════════ Multi-touch helpers ═══════════════

    private void StartMultiTouchGesture()
    {
        _isMultiTouchGesture = true;

        var points = GetTwoPointerPositions();
        _gestureStartDistance = Distance(points.Item1, points.Item2);
        _gestureStartZoom = _zoom;
        _gestureStartCenter = Midpoint(points.Item1, points.Item2);
        _gestureStartOffsetX = _offsetX;
        _gestureStartOffsetY = _offsetY;
    }

    private void UpdateMultiTouchGesture()
    {
        var points = GetTwoPointerPositions();
        var currentDistance = Distance(points.Item1, points.Item2);
        var currentCenter = Midpoint(points.Item1, points.Item2);

        // Pinch-to-zoom
        if (_gestureStartDistance > 10) // Avoid division by near-zero
        {
            var scaleRatio = currentDistance / _gestureStartDistance;
            var newZoom = Math.Clamp(_gestureStartZoom * scaleRatio, MinZoom, MaxZoom);

            // Zoom toward the gesture center
            var worldX = (_gestureStartCenter.X - _gestureStartOffsetX) / _gestureStartZoom;
            var worldY = (_gestureStartCenter.Y - _gestureStartOffsetY) / _gestureStartZoom;

            _zoom = newZoom;
            _offsetX = currentCenter.X - worldX * _zoom;
            _offsetY = currentCenter.Y - worldY * _zoom;
        }
        else
        {
            // Just pan
            _offsetX = _gestureStartOffsetX + (currentCenter.X - _gestureStartCenter.X);
            _offsetY = _gestureStartOffsetY + (currentCenter.Y - _gestureStartCenter.Y);
        }

        UpdateTransform();
    }

    private (Point, Point) GetTwoPointerPositions()
    {
        var enumerator = _activePointers.Values.GetEnumerator();
        enumerator.MoveNext(); var p1 = enumerator.Current;
        enumerator.MoveNext(); var p2 = enumerator.Current;
        return (p1, p2);
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Point Midpoint(Point a, Point b) =>
        new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    // ═══════════════ Transform ═══════════════

    private void UpdateTransform()
    {
        _childTransform.Matrix = new Matrix(_zoom, 0, 0, _zoom, _offsetX, _offsetY);
    }
}
