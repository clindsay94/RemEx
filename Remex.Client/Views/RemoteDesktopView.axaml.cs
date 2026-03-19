using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Remex.Client.Controls;
using Remex.Client.ViewModels;
using Remex.Core.Models;

namespace Remex.Client.Views;

public partial class RemoteDesktopView : UserControl
{
    // ═══════════════ Timing / throttle ═══════════════
    private DateTime _lastMouseMoveTime = DateTime.MinValue;
    private static readonly TimeSpan MouseMoveThrottle = TimeSpan.FromMilliseconds(16);

    // ═══════════════ Touch gesture state ═══════════════
    private readonly Dictionary<long, Point> _activePointers = new();
    private bool _isMultiTouchGesture;

    // Single-finger gesture detection
    private Point _touchDownPos;
    private DateTime _touchDownTime;
    private Point _lastTapPos;
    private DateTime _lastTapTime = DateTime.MinValue;
    private CancellationTokenSource? _longPressCts;
    private bool _longPressFired;
    private bool _touchMoved;

    private const double TapThreshold = 15;
    private const double DoubleTapDistance = 40;
    private const double DoubleTapMaxMs = 300;
    private const int LongPressMs = 500;

    // ═══════════════ Cursor indicator ═══════════════
    private CancellationTokenSource? _cursorFadeCts;
    private const int CursorFadeMs = 3000;

    // ═══════════════ Viewport zoom/pan (local) ═══════════════
    private readonly MatrixTransform _viewportTransform = new();
    private double _viewportZoom = 1.0;
    private double _viewportOffsetX;
    private double _viewportOffsetY;
    private const double MinViewportZoom = 1.0;
    private const double MaxViewportZoom = 5.0;
    private const double WheelZoomFactor = 0.15;

    // Pinch gesture state
    private double _pinchStartDistance;
    private double _pinchStartZoom;
    private Point _pinchStartCenter;
    private double _pinchStartOffsetX;
    private double _pinchStartOffsetY;

    public RemoteDesktopView()
    {
        InitializeComponent();

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

        var viewport = this.FindControl<Border>("ViewportBorder");
        if (viewport is not null)
        {
            viewport.PointerPressed += OnViewportPointerPressed;
            viewport.PointerMoved += OnViewportPointerMoved;
            viewport.PointerReleased += OnViewportPointerReleased;
            viewport.PointerWheelChanged += OnViewportPointerWheel;
        }

        var cursorPad = this.FindControl<VirtualCursorPad>("CursorPad");
        if (cursorPad is not null)
            cursorPad.InputRequested += OnCursorPadInput;

        this.KeyDown += OnViewKeyDown;
        this.KeyUp += OnViewKeyUp;

        if (DataContext is RemoteDesktopViewModel vm)
            vm.ViewportZoomResetRequested += ResetViewport;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var viewport = this.FindControl<Border>("ViewportBorder");
        if (viewport is not null)
        {
            viewport.PointerPressed -= OnViewportPointerPressed;
            viewport.PointerMoved -= OnViewportPointerMoved;
            viewport.PointerReleased -= OnViewportPointerReleased;
            viewport.PointerWheelChanged -= OnViewportPointerWheel;
        }

        var cursorPad = this.FindControl<VirtualCursorPad>("CursorPad");
        if (cursorPad is not null)
            cursorPad.InputRequested -= OnCursorPadInput;

        this.KeyDown -= OnViewKeyDown;
        this.KeyUp -= OnViewKeyUp;

        if (DataContext is RemoteDesktopViewModel vm)
            vm.ViewportZoomResetRequested -= ResetViewport;

        base.OnDetachedFromVisualTree(e);
    }

    // ═══════════════ Combo box helpers ═══════════════

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

    // ═══════════════ Virtual cursor pad callback ═══════════════

    private void OnCursorPadInput(InputEvent input)
    {
        if (DataContext is RemoteDesktopViewModel vm && vm.IsStreaming)
            _ = vm.SendInputAsync(input);
    }

    // ═══════════════ Input mode detection ═══════════════

    /// <summary>
    /// Returns true if this pointer should be treated as a stylus/pen
    /// (either native PointerType.Pen or user-toggled stylus mode).
    /// </summary>
    private bool IsPenInput(PointerEventArgs e) =>
        e.Pointer.Type == PointerType.Pen ||
        (DataContext is RemoteDesktopViewModel vm && vm.StylusMode && e.Pointer.Type == PointerType.Touch);

    /// <summary>
    /// Returns true if this pointer is a touch gesture (not pen/stylus mode).
    /// </summary>
    private bool IsTouchInput(PointerEventArgs e) =>
        e.Pointer.Type == PointerType.Touch && !IsPenInput(e);

    // ═══════════════ Coordinate mapping ═══════════════

    /// <summary>
    /// Maps a point in the ViewportBorder coordinate space to remote screen coords.
    /// Uses the Image control's actual layout position for accurate mapping.
    /// </summary>
    private (int x, int y)? MapToRemoteCoords(Point viewportPoint)
    {
        if (DataContext is not RemoteDesktopViewModel vm) return null;
        if (vm.ScreenWidth <= 0 || vm.ScreenHeight <= 0) return null;

        var img = this.FindControl<Image>("ScreenImage");
        if (img is null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0) return null;

        // Invert viewport zoom/pan to get image-local coordinates
        double imgLocalX = (viewportPoint.X - _viewportOffsetX) / _viewportZoom;
        double imgLocalY = (viewportPoint.Y - _viewportOffsetY) / _viewportZoom;

        // Adjust for the Image control's position within the Panel
        // (should be 0,0 in a Panel but accounts for any layout offset)
        imgLocalX -= img.Bounds.Left;
        imgLocalY -= img.Bounds.Top;

        // Compute the Uniform stretch area within the Image control
        double imgAspect = (double)vm.ScreenWidth / vm.ScreenHeight;
        double controlAspect = img.Bounds.Width / img.Bounds.Height;

        double renderWidth, renderHeight, offsetX, offsetY;

        if (controlAspect > imgAspect)
        {
            renderHeight = img.Bounds.Height;
            renderWidth = renderHeight * imgAspect;
            offsetX = (img.Bounds.Width - renderWidth) / 2;
            offsetY = 0;
        }
        else
        {
            renderWidth = img.Bounds.Width;
            renderHeight = renderWidth / imgAspect;
            offsetX = 0;
            offsetY = (img.Bounds.Height - renderHeight) / 2;
        }

        double relX = (imgLocalX - offsetX) / renderWidth;
        double relY = (imgLocalY - offsetY) / renderHeight;

        if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return null;

        int remoteX = (int)(relX * vm.ScreenWidth);
        int remoteY = (int)(relY * vm.ScreenHeight);
        return (remoteX, remoteY);
    }

    // ═══════════════ Cursor indicator ═══════════════

    /// <summary>
    /// Shows the cursor crosshair at the given viewport position.
    /// </summary>
    private void ShowCursorAt(Point viewportPoint)
    {
        if (DataContext is not RemoteDesktopViewModel vm) return;

        vm.CursorIndicatorX = viewportPoint.X;
        vm.CursorIndicatorY = viewportPoint.Y;
        vm.IsCursorVisible = true;

        // Auto-fade after timeout
        _cursorFadeCts?.Cancel();
        _cursorFadeCts = new CancellationTokenSource();
        var cts = _cursorFadeCts;
        _ = Task.Delay(CursorFadeMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is RemoteDesktopViewModel v)
                        v.IsCursorVisible = false;
                });
        }, TaskScheduler.Default);
    }

    // ═══════════════ Viewport transform ═══════════════

    private void ApplyViewportTransform()
    {
        var img = this.FindControl<Image>("ScreenImage");
        if (img is null) return;

        var matrix = Matrix.Identity;
        matrix *= Matrix.CreateScale(_viewportZoom, _viewportZoom);
        matrix *= Matrix.CreateTranslation(_viewportOffsetX, _viewportOffsetY);

        _viewportTransform.Matrix = matrix;
        img.RenderTransform = _viewportTransform;
    }

    public void ResetViewport()
    {
        _viewportZoom = 1.0;
        _viewportOffsetX = 0;
        _viewportOffsetY = 0;
        ApplyViewportTransform();
    }

    private void ClampViewportOffset()
    {
        var viewport = this.FindControl<Border>("ViewportBorder");
        if (viewport is null) return;

        double maxOffsetX = viewport.Bounds.Width * (_viewportZoom - 1) / 2;
        double maxOffsetY = viewport.Bounds.Height * (_viewportZoom - 1) / 2;

        _viewportOffsetX = Math.Clamp(_viewportOffsetX, -maxOffsetX, maxOffsetX);
        _viewportOffsetY = Math.Clamp(_viewportOffsetY, -maxOffsetY, maxOffsetY);
    }

    // ═══════════════ Pointer handlers ═══════════════

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        var viewport = this.FindControl<Border>("ViewportBorder")!;
        var pointer = e.GetCurrentPoint(viewport);
        _activePointers[e.Pointer.Id] = pointer.Position;

        // ── Pen / S-Pen / Stylus Mode ──
        if (IsPenInput(e))
        {
            this.Focus();
            var coords = MapToRemoteCoords(pointer.Position);
            if (coords is null) return;

            ShowCursorAt(pointer.Position);

            // Check barrel button for right-click
            int button = pointer.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed ? 2 : 0;

            _ = vm.SendInputAsync(new InputEvent
            {
                EventType = InputEventTypes.MouseDown,
                X = coords.Value.x,
                Y = coords.Value.y,
                Button = button,
            });
            return;
        }

        // ── Touch (no stylus mode) ──
        if (IsTouchInput(e))
        {
            if (_activePointers.Count == 2)
            {
                _isMultiTouchGesture = true;
                CancelLongPress();

                var pts = GetTwoPointerPositions();
                if (pts is not null)
                {
                    _pinchStartDistance = Distance(pts.Value.p1, pts.Value.p2);
                    _pinchStartZoom = _viewportZoom;
                    _pinchStartCenter = Midpoint(pts.Value.p1, pts.Value.p2);
                    _pinchStartOffsetX = _viewportOffsetX;
                    _pinchStartOffsetY = _viewportOffsetY;
                }
                return;
            }

            if (_activePointers.Count == 1)
            {
                _touchDownPos = pointer.Position;
                _touchDownTime = DateTime.UtcNow;
                _touchMoved = false;
                _longPressFired = false;
                _isMultiTouchGesture = false;

                _longPressCts?.Cancel();
                _longPressCts = new CancellationTokenSource();
                var cts = _longPressCts;
                var downPos = _touchDownPos;
                _ = Task.Delay(LongPressMs, cts.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && !_touchMoved && _activePointers.Count == 1)
                    {
                        _longPressFired = true;
                        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                        {
                            var coords = MapToRemoteCoords(downPos);
                            if (coords is not null)
                            {
                                ShowCursorAt(downPos);
                                await vm.SendInputAsync(new InputEvent
                                {
                                    EventType = InputEventTypes.MouseClick,
                                    X = coords.Value.x,
                                    Y = coords.Value.y,
                                    Button = 2,
                                });
                            }
                        });
                    }
                }, TaskScheduler.Default);
            }
            return;
        }

        // ── Mouse (desktop) ──
        this.Focus();
        {
            var coords = MapToRemoteCoords(pointer.Position);
            if (coords is null) return;

            ShowCursorAt(pointer.Position);

            int button = pointer.Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => 0,
                PointerUpdateKind.MiddleButtonPressed => 1,
                PointerUpdateKind.RightButtonPressed => 2,
                _ => 0
            };

            _ = vm.SendInputAsync(new InputEvent
            {
                EventType = InputEventTypes.MouseDown,
                X = coords.Value.x,
                Y = coords.Value.y,
                Button = button,
            });
        }
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        var viewport = this.FindControl<Border>("ViewportBorder");
        if (viewport is null) return;

        var pos = e.GetPosition(viewport);
        _activePointers[e.Pointer.Id] = pos;

        // ── Pen / S-Pen / Stylus Mode ──
        if (IsPenInput(e))
        {
            var now = DateTime.UtcNow;
            if (now - _lastMouseMoveTime < MouseMoveThrottle) return;
            _lastMouseMoveTime = now;

            var coords = MapToRemoteCoords(pos);
            if (coords is not null)
            {
                ShowCursorAt(pos);
                _ = vm.SendInputAsync(new InputEvent
                {
                    EventType = InputEventTypes.MouseMove,
                    X = coords.Value.x,
                    Y = coords.Value.y,
                });
            }
            return;
        }

        // ── Touch ──
        if (IsTouchInput(e))
        {
            if (_isMultiTouchGesture && _activePointers.Count >= 2)
            {
                var pts = GetTwoPointerPositions();
                if (pts is null) return;

                double dist = Distance(pts.Value.p1, pts.Value.p2);
                var center = Midpoint(pts.Value.p1, pts.Value.p2);

                if (_pinchStartDistance > 10)
                {
                    double newZoom = _pinchStartZoom * (dist / _pinchStartDistance);
                    _viewportZoom = Math.Clamp(newZoom, MinViewportZoom, MaxViewportZoom);
                }

                _viewportOffsetX = _pinchStartOffsetX + (center.X - _pinchStartCenter.X);
                _viewportOffsetY = _pinchStartOffsetY + (center.Y - _pinchStartCenter.Y);

                ClampViewportOffset();
                ApplyViewportTransform();
                NotifyZoomChanged();

                e.Handled = true;
                return;
            }

            if (_activePointers.Count == 1 && !_isMultiTouchGesture)
            {
                double dx = pos.X - _touchDownPos.X;
                double dy = pos.Y - _touchDownPos.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist > TapThreshold)
                {
                    if (!_touchMoved)
                    {
                        _touchMoved = true;
                        CancelLongPress();
                    }

                    var now = DateTime.UtcNow;
                    if (now - _lastMouseMoveTime < MouseMoveThrottle) return;
                    _lastMouseMoveTime = now;

                    var coords = MapToRemoteCoords(pos);
                    if (coords is not null)
                    {
                        ShowCursorAt(pos);
                        _ = vm.SendInputAsync(new InputEvent
                        {
                            EventType = InputEventTypes.MouseMove,
                            X = coords.Value.x,
                            Y = coords.Value.y,
                        });
                    }
                }
            }
            return;
        }

        // ── Mouse ──
        {
            var now = DateTime.UtcNow;
            if (now - _lastMouseMoveTime < MouseMoveThrottle) return;
            _lastMouseMoveTime = now;

            var coords = MapToRemoteCoords(pos);
            if (coords is not null)
            {
                ShowCursorAt(pos);
                _ = vm.SendInputAsync(new InputEvent
                {
                    EventType = InputEventTypes.MouseMove,
                    X = coords.Value.x,
                    Y = coords.Value.y,
                });
            }
        }
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming)
        {
            _activePointers.Remove(e.Pointer.Id);
            return;
        }

        var viewport = this.FindControl<Border>("ViewportBorder");

        // ── Pen / S-Pen / Stylus Mode ──
        if (IsPenInput(e))
        {
            _activePointers.Remove(e.Pointer.Id);
            int button = e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased ? 2 : 0;

            _ = vm.SendInputAsync(new InputEvent
            {
                EventType = InputEventTypes.MouseUp,
                Button = button,
            });
            return;
        }

        // ── Touch ──
        if (IsTouchInput(e))
        {
            _activePointers.Remove(e.Pointer.Id);
            CancelLongPress();

            if (_isMultiTouchGesture)
            {
                if (_activePointers.Count < 2)
                    _isMultiTouchGesture = false;
                return;
            }

            if (_longPressFired)
                return;

            if (!_touchMoved && _activePointers.Count == 0)
            {
                var elapsed = (DateTime.UtcNow - _touchDownTime).TotalMilliseconds;
                if (elapsed < LongPressMs)
                {
                    var timeSinceLastTap = (DateTime.UtcNow - _lastTapTime).TotalMilliseconds;
                    var distFromLastTap = Distance(_touchDownPos, _lastTapPos);

                    if (timeSinceLastTap < DoubleTapMaxMs && distFromLastTap < DoubleTapDistance)
                    {
                        // Double-tap → left click at FIRST tap position
                        _lastTapTime = DateTime.MinValue;
                        var coords = MapToRemoteCoords(_lastTapPos);
                        if (coords is not null)
                        {
                            ShowCursorAt(_lastTapPos);
                            _ = vm.SendInputAsync(new InputEvent
                            {
                                EventType = InputEventTypes.MouseClick,
                                X = coords.Value.x,
                                Y = coords.Value.y,
                                Button = 0,
                            });
                        }
                    }
                    else
                    {
                        // Single tap → move cursor (no click)
                        _lastTapTime = DateTime.UtcNow;
                        _lastTapPos = _touchDownPos;
                        var coords = MapToRemoteCoords(_touchDownPos);
                        if (coords is not null)
                        {
                            ShowCursorAt(_touchDownPos);
                            _ = vm.SendInputAsync(new InputEvent
                            {
                                EventType = InputEventTypes.MouseMove,
                                X = coords.Value.x,
                                Y = coords.Value.y,
                            });
                        }
                    }
                }
            }
            return;
        }

        // ── Mouse ──
        _activePointers.Remove(e.Pointer.Id);

        int mouseButton = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => 0,
            PointerUpdateKind.MiddleButtonReleased => 1,
            PointerUpdateKind.RightButtonReleased => 2,
            _ => 0
        };

        _ = vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseUp,
            Button = mouseButton,
        });
    }

    private void OnViewportPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        if (_viewportZoom > 1.01)
        {
            double zoomDelta = e.Delta.Y > 0 ? WheelZoomFactor : -WheelZoomFactor;
            _viewportZoom = Math.Clamp(_viewportZoom + zoomDelta, MinViewportZoom, MaxViewportZoom);

            if (_viewportZoom <= MinViewportZoom + 0.01)
                ResetViewport();
            else
            {
                ClampViewportOffset();
                ApplyViewportTransform();
            }

            NotifyZoomChanged();
            e.Handled = true;
        }
        else
        {
            _ = vm.SendInputAsync(new InputEvent
            {
                EventType = InputEventTypes.MouseScroll,
                DeltaX = (int)(e.Delta.X * 120),
                DeltaY = (int)(e.Delta.Y * 120),
            });
        }
    }

    // ═══════════════ Keyboard ═══════════════

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

    // ═══════════════ Helpers ═══════════════

    private void CancelLongPress()
    {
        _longPressCts?.Cancel();
        _longPressCts = null;
    }

    private void NotifyZoomChanged()
    {
        if (DataContext is RemoteDesktopViewModel vm)
            vm.UpdateViewportZoom(_viewportZoom);
    }

    private (Point p1, Point p2)? GetTwoPointerPositions()
    {
        using var enumerator = _activePointers.Values.GetEnumerator();
        if (!enumerator.MoveNext()) return null;
        var p1 = enumerator.Current;
        if (!enumerator.MoveNext()) return null;
        var p2 = enumerator.Current;
        return (p1, p2);
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Point Midpoint(Point a, Point b) =>
        new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
