using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Remex.Client.ViewModels;

namespace Remex.Client.Controls;

/// <summary>
/// A <see cref="ContentControl"/> that handles pointer-based dragging,
/// corner Thumb resizing, and animated visual feedback (opacity + scale).
/// Designed to be used inside a Canvas with ItemsControl DataTemplates.
/// </summary>
public class DraggableCard : ContentControl
{
    // ═══════════════ Drag state ═══════════════

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _startLeft;
    private double _startTop;

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

        _isDragging = true;
        _dragStartPoint = e.GetPosition(Parent as Visual);

        // Read start position from the ViewModel (the source of truth),
        // since Canvas.Left is bound on the parent ContentPresenter, not this control.
        if (DataContext is CanvasCardViewModel startVm)
        {
            _startLeft = startVm.PositionX;
            _startTop = startVm.PositionY;
        }
        else
        {
            _startLeft = 0;
            _startTop = 0;
        }

        e.Pointer.Capture(this);

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
            // Walk up to find the canvas dashboard VM for Z-order.
            FindCanvasDashboard()?.BringToFront(vm);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isDragging) return;

        var currentPos = e.GetPosition(Parent as Visual);
        var deltaX = currentPos.X - _dragStartPoint.X;
        var deltaY = currentPos.Y - _dragStartPoint.Y;

        var newLeft = Math.Max(0, _startLeft + deltaX);
        var newTop = Math.Max(0, _startTop + deltaY);

        // Update the VM — the Canvas.Left/Top bindings in ItemsControl.Styles
        // handle the visual position automatically.
        if (DataContext is CanvasCardViewModel vm)
        {
            vm.PositionX = newLeft;
            vm.PositionY = newTop;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

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
            FindCanvasDashboard()?.OnCardDropped(vm);
        }

        e.Handled = true;
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
}
