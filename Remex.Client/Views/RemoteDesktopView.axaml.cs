using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Remex.Client.ViewModels;
using Remex.Core.Models;

namespace Remex.Client.Views;

public partial class RemoteDesktopView : UserControl
{
    private DateTime _lastMouseMoveTime = DateTime.MinValue;
    private static readonly TimeSpan MouseMoveThrottle = TimeSpan.FromMilliseconds(16); // ~60/sec

    public RemoteDesktopView()
    {
        InitializeComponent();

        // Wire up combo box change events for Scale and FPS
        var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
        if (scaleCombo is not null)
            scaleCombo.SelectionChanged += OnScaleChanged;

        var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
        if (fpsCombo is not null)
            fpsCombo.SelectionChanged += OnFpsChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var img = this.FindControl<Image>("ScreenImage");
        if (img is not null)
        {
            img.PointerMoved += OnImagePointerMoved;
            img.PointerPressed += OnImagePointerPressed;
            img.PointerReleased += OnImagePointerReleased;
            img.PointerWheelChanged += OnImagePointerWheel;
        }

        this.KeyDown += OnViewKeyDown;
        this.KeyUp += OnViewKeyUp;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var img = this.FindControl<Image>("ScreenImage");
        if (img is not null)
        {
            img.PointerMoved -= OnImagePointerMoved;
            img.PointerPressed -= OnImagePointerPressed;
            img.PointerReleased -= OnImagePointerReleased;
            img.PointerWheelChanged -= OnImagePointerWheel;
        }

        this.KeyDown -= OnViewKeyDown;
        this.KeyUp -= OnViewKeyUp;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnScaleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale)
                && DataContext is RemoteDesktopViewModel vm)
            {
                vm.Scale = scale;
            }
        }
    }

    private void OnFpsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (int.TryParse(tag, out int fps) && DataContext is RemoteDesktopViewModel vm)
            {
                vm.TargetFps = fps;
            }
        }
    }

    private (int x, int y)? MapToRemoteCoords(Point localPoint)
    {
        if (DataContext is not RemoteDesktopViewModel vm) return null;
        if (vm.ScreenWidth <= 0 || vm.ScreenHeight <= 0) return null;

        var img = this.FindControl<Image>("ScreenImage");
        if (img is null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0) return null;

        // The Image uses Stretch="Uniform", so we need to compute the actual rendered area
        double imgAspect = (double)vm.ScreenWidth / vm.ScreenHeight;
        double controlAspect = img.Bounds.Width / img.Bounds.Height;

        double renderWidth, renderHeight, offsetX, offsetY;

        if (controlAspect > imgAspect)
        {
            // Pillarboxed (bars on left/right)
            renderHeight = img.Bounds.Height;
            renderWidth = renderHeight * imgAspect;
            offsetX = (img.Bounds.Width - renderWidth) / 2;
            offsetY = 0;
        }
        else
        {
            // Letterboxed (bars on top/bottom)
            renderWidth = img.Bounds.Width;
            renderHeight = renderWidth / imgAspect;
            offsetX = 0;
            offsetY = (img.Bounds.Height - renderHeight) / 2;
        }

        double relX = (localPoint.X - offsetX) / renderWidth;
        double relY = (localPoint.Y - offsetY) / renderHeight;

        if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return null;

        int remoteX = (int)(relX * vm.ScreenWidth);
        int remoteY = (int)(relY * vm.ScreenHeight);
        return (remoteX, remoteY);
    }

    private async void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        var now = DateTime.UtcNow;
        if (now - _lastMouseMoveTime < MouseMoveThrottle) return;
        _lastMouseMoveTime = now;

        var img = this.FindControl<Image>("ScreenImage");
        if (img is null) return;

        var pos = e.GetPosition(img);
        var coords = MapToRemoteCoords(pos);
        if (coords is null) return;

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseMove,
            X = coords.Value.x,
            Y = coords.Value.y,
        });
    }

    private async void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        // Capture keyboard focus
        this.Focus();

        var img = this.FindControl<Image>("ScreenImage");
        if (img is null) return;

        var pos = e.GetPosition(img);
        var coords = MapToRemoteCoords(pos);
        if (coords is null) return;

        int button = e.GetCurrentPoint(img).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0,
            PointerUpdateKind.MiddleButtonPressed => 1,
            PointerUpdateKind.RightButtonPressed => 2,
            _ => 0
        };

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseDown,
            X = coords.Value.x,
            Y = coords.Value.y,
            Button = button,
        });
    }

    private async void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        int button = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => 0,
            PointerUpdateKind.MiddleButtonReleased => 1,
            PointerUpdateKind.RightButtonReleased => 2,
            _ => 0
        };

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseUp,
            Button = button,
        });
    }

    private async void OnImagePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseScroll,
            DeltaX = (int)(e.Delta.X * 120),
            DeltaY = (int)(e.Delta.Y * 120),
        });
    }

    private async void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.KeyDown,
            KeyCode = (int)e.Key,
        });

        e.Handled = true;
    }

    private async void OnViewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.KeyUp,
            KeyCode = (int)e.Key,
        });

        e.Handled = true;
    }
}
