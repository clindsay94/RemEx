using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remex.Client.ViewModels;

namespace Remex.Client.Controls;

/// <summary>
/// A <see cref="ContentControl"/> that handles pointer-based dragging,
/// corner Thumb resizing, and animated visual feedback (opacity + scale).
/// On touch: long-press (400ms) to initiate drag.
/// On mouse: instant drag on left-click.
/// Designed to be used inside a Canvas with ItemsControl DataTemplates.
/// </summary>
public class DraggableCard : ContentControl
{
    // ═══════════════ Drag state ═══════════════

    private bool _isDragging;
    private Point _pointerOffsetInCard; // Where on the card the user grabbed
    private Visual? _stableParent;      // The Canvas panel (doesn't move)

    // ═══════════════ Long-press state (touch) ═══════════════

    private CancellationTokenSource? _longPressCts;
    private bool _isWaitingForLongPress;
    private Point _touchStartPoint;
    private const int LongPressDelayMs = 400;
    private const double LongPressMoveThreshold = 12; // px of movement to cancel long-press

    // ═══════════════ Resize Thumb ═══════════════

    private Thumb? _resizeThumb;
    private const double CardMinWidth = 120;
    private const double CardMinHeight = 80;

    // ═══════════════ Styled Properties ═══════════════

    public static readonly StyledProperty<bool> IsDraggingProperty =
        AvaloniaProperty.Register<DraggableCard, bool>(nameof(IsDragging));

    public bool IsDragging
    {
        get => GetValue(IsDraggingProperty);
        set => SetValue(IsDraggingProperty, value);
    }

    public DraggableCard()
    {
        // Set up the scale/opacity transitions for visual drag feedback.
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = new ScaleTransform(1, 1);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Wire up the resize thumb if present in the template.
        _resizeThumb = e.NameScope.Find<Thumb>("PART_ResizeThumb");
        if (_resizeThumb != null)
        {
            _resizeThumb.DragDelta += OnResizeDragDelta;
            _resizeThumb.DragCompleted += OnResizeDragCompleted;
        }
    }

    // ═══════════════ Pointer Drag ═══════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Don't initiate drag if the resize thumb was hit.
        if (e.Source is Thumb) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        if (e.Pointer.Type == PointerType.Touch)
        {
            // Touch: start long-press timer
            _touchStartPoint = e.GetPosition(this);
            _isWaitingForLongPress = true;
            _longPressCts?.Cancel();
            _longPressCts = new CancellationTokenSource();

            var cts = _longPressCts;
            var pointer = e.Pointer;

            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(LongPressDelayMs, cts.Token);

                    if (_isWaitingForLongPress && !cts.IsCancellationRequested)
                    {
                        // Long-press fired — enter drag mode
                        _isWaitingForLongPress = false;
                        EnterDragMode(pointer, _touchStartPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Long-press was cancelled (finger moved too much or released)
                }
            });

            // DO NOT set e.Handled — let the event bubble to ZoomableCanvas
            // so single-finger pan still works until long-press fires
        }
        else
        {
            // Mouse: instant drag
            EnterDragMode(e.Pointer, e.GetPosition(this));
            e.Handled = true;
        }
    }

    private void EnterDragMode(IPointer pointer, Point pointerInCard)
    {
        _isDragging = true;
        _pointerOffsetInCard = pointerInCard;

        // Find the Canvas panel — Parent is ContentPresenter (moves!),
        // Grandparent is the Canvas (stable reference frame).
        _stableParent = (Parent as Visual)?.GetVisualParent() as Visual;

        pointer.Capture(this);

        // Visual feedback: shrink + fade.
        IsDragging = true;
        if (RenderTransform is ScaleTransform st)
        {
            st.ScaleX = 0.95;
            st.ScaleY = 0.95;
        }
        Opacity = 0.75;

        // Bring to front.
        if (DataContext is CanvasCardViewModel vm)
        {
            vm.IsDragging = true;
            FindCanvasDashboard()?.BringToFront(vm);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // If waiting for long-press, check if finger moved too much
        if (_isWaitingForLongPress && e.Pointer.Type == PointerType.Touch)
        {
            var current = e.GetPosition(this);
            var dx = current.X - _touchStartPoint.X;
            var dy = current.Y - _touchStartPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > LongPressMoveThreshold)
            {
                // Finger moved too much — cancel long-press, let canvas pan
                CancelLongPress();
            }
            return;
        }

        if (!_isDragging || _stableParent is null) return;

        // Get pointer position in Canvas space (stable) and subtract the grab offset.
        var pointerInCanvas = e.GetPosition(_stableParent);
        var newLeft = Math.Max(0, pointerInCanvas.X - _pointerOffsetInCard.X);
        var newTop = Math.Max(0, pointerInCanvas.Y - _pointerOffsetInCard.Y);

        if (DataContext is CanvasCardViewModel vm)
        {
            vm.PositionX = newLeft;
            vm.PositionY = newTop;
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Cancel any pending long-press
        CancelLongPress();

        if (!_isDragging) return;

        _isDragging = false;
        e.Pointer.Capture(null);

        // Restore visual state.
        IsDragging = false;
        if (RenderTransform is ScaleTransform st)
        {
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;
        }
        Opacity = 1.0;

        if (DataContext is CanvasCardViewModel vm)
        {
            vm.IsDragging = false;

            // Get drop position relative to the ZoomableCanvas (the viewport)
            // to detect if the card was dropped over the staging drawer.
            var dashboard = FindCanvasDashboard();
            if (dashboard != null)
            {
                var zoomable = FindZoomableCanvas();
                double dropX = zoomable != null
                    ? e.GetPosition(zoomable).X
                    : 0;
                dashboard.OnCardDropped(vm, dropX);
            }
        }

        e.Handled = true;
    }

    private void CancelLongPress()
    {
        _isWaitingForLongPress = false;
        _longPressCts?.Cancel();
        _longPressCts = null;
    }

    // ═══════════════ Resize Thumb ═══════════════

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        var newWidth = Math.Max(CardMinWidth, Width + e.Vector.X);
        var newHeight = Math.Max(CardMinHeight, Height + e.Vector.Y);

        Width = newWidth;
        Height = newHeight;

        if (DataContext is CanvasCardViewModel vm)
        {
            vm.Width = newWidth;
            vm.Height = newHeight;
        }
    }

    private void OnResizeDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is CanvasCardViewModel vm)
        {
            FindCanvasDashboard()?.OnCardResized(vm);
        }
    }

    // ═══════════════ Helpers ═══════════════

    private CanvasDashboardViewModel? FindCanvasDashboard()
    {
        // Walk up the visual tree to find a parent whose DataContext is the canvas VM.
        Visual? current = this;
        while (current != null)
        {
            if (current.DataContext is CanvasDashboardViewModel cdvm)
                return cdvm;
            current = current.GetVisualParent();
        }
        return null;
    }

    private ZoomableCanvas? FindZoomableCanvas()
    {
        Visual? current = this;
        while (current != null)
        {
            if (current is ZoomableCanvas zc)
                return zc;
            current = current.GetVisualParent();
        }
        return null;
    }
}
