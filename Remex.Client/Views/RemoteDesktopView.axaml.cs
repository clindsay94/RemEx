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
    private bool _longPressFired;
    private bool _touchMoved;
    private bool _isTouchDragging;

    private const double TapThreshold = 10;
    private const double DragThreshold = 15;
    private const int LongPressMs = 500;

    // ═══════════════ Cursor indicator ═══════════════
    private CancellationTokenSource? _cursorFadeCts;
    private const int CursorFadeMs = 3000;

    // ═══════════════ Viewport zoom/pan (local) ═══════════════
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

        var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
        if (scaleCombo is not null)
            scaleCombo.SelectionChanged += OnScaleChanged;

        var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
        if (fpsCombo is not null)
            fpsCombo.SelectionChanged += OnFpsChanged;

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

        var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
        if (scaleCombo is not null)
            scaleCombo.SelectionChanged -= OnScaleChanged;

        var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
        if (fpsCombo is not null)
            fpsCombo.SelectionChanged -= OnFpsChanged;

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
                if (vm.IsStreaming)
                    _ = vm.ApplySettingsCommand.ExecuteAsync(null);
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
                if (vm.IsStreaming)
                    _ = vm.ApplySettingsCommand.ExecuteAsync(null);
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

    private bool IsPenInput(PointerEventArgs e) =>
        e.Pointer.Type == PointerType.Pen ||
        (DataContext is RemoteDesktopViewModel vm && vm.StylusMode && e.Pointer.Type == PointerType.Touch);

    private bool IsTouchInput(PointerEventArgs e) =>
        e.Pointer.Type == PointerType.Touch && !IsPenInput(e);

    // ═══════════════ Coordinate mapping ═══════════════

    private (int x, int y)? MapToRemoteCoords(Point viewportPoint)
    {
        if (DataContext is not RemoteDesktopViewModel vm) return null;
        if (vm.ScreenWidth <= 0 || vm.ScreenHeight <= 0) return null;

        var img = this.FindControl<Image>("ScreenImage");
        if (img is null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0) return null;

        var viewport = this.FindControl<Border>("ViewportBorder");
        if (viewport is null) return null;

        // Use TransformToVisual to convert viewport coords to Image-local coords.
        // This correctly inverts the RenderTransform (zoom/pan) at any zoom level.
        Point imgLocal;
        var transform = viewport.TransformToVisual(img);
        if (transform is not null)
        {
            imgLocal = transform.Value.Transform(viewportPoint);
        }
        else
        {
            // Fallback: manual inversion
            imgLocal = new Point(
                (viewportPoint.X - _viewportOffsetX) / _viewportZoom - img.Bounds.Left,
                (viewportPoint.Y - _viewportOffsetY) / _viewportZoom - img.Bounds.Top);
        }

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

        double relX = (imgLocal.X - offsetX) / renderWidth;
        double relY = (imgLocal.Y - offsetY) / renderHeight;

        if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return null;

        int remoteX = (int)(relX * vm.ScreenWidth);
        int remoteY = (int)(relY * vm.ScreenHeight);
        return (remoteX, remoteY);
    }

    private void ShowCursorAt(Point viewportPoint)
    {
        if (DataContext is not RemoteDesktopViewModel vm) return;

        // Since the TransformContainer and ViewportBorder are the same size (filling the Panel),
        // but the TransformContainer is scaled/panned, we must calculate where the cursor
        // appears in the scaled coordinate space so it matches physical finger position.
        
        // We want the indicator to be exactly where the user is touching.
        // But the indicator is inside the TransformContainer.
        // So we need the pointer position relative to the UNTRANSFORMED TransformContainer.
        
        vm.CursorIndicatorX = (viewportPoint.X - _viewportOffsetX) / _viewportZoom;
        vm.CursorIndicatorY = (viewportPoint.Y - _viewportOffsetY) / _viewportZoom;
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
        var container = this.FindControl<Panel>("TransformContainer");
        if (container?.RenderTransform is TransformGroup group)
        {
            foreach (var t in group.Children)
            {
                if (t is ScaleTransform scale)
                {
                    scale.ScaleX = _viewportZoom;
                    scale.ScaleY = _viewportZoom;
                }
                else if (t is TranslateTransform trans)
                {
                    trans.X = _viewportOffsetX;
                    trans.Y = _viewportOffsetY;
                }
            }
        }
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

        // ── Touch ──
        if (IsTouchInput(e))
        {
            if (_activePointers.Count == 2)
            {
                _isMultiTouchGesture = true;
                _isTouchDragging = false;

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
                _isTouchDragging = false;

                ShowCursorAt(pointer.Position);
            }
            return;
        }

        // ── Mouse (desktop) ──
        this.Focus();
        {
            var coords = MapToRemoteCoords(pointer.Position);
            if (coords is null) return;

            ShowCursorAt(pointer.Position);

            int button = 0;
            if (pointer.Properties.IsLeftButtonPressed) button = 0;
            else if (pointer.Properties.IsMiddleButtonPressed) button = 1;
            else if (pointer.Properties.IsRightButtonPressed) button = 2;
            else if (pointer.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed) button = 2;

            _ = vm.SendInputAsync(new InputEvent
            {
                EventType = InputEventTypes.MouseDown,
                X = coords.Value.x,
                Y = coords.Value.y,
                Button = button,
            });
        }
    }

    private async void OnViewportHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;
        if (e.HoldingState != HoldingState.Started || _touchMoved || _isMultiTouchGesture) return;

        var pos = e.Position;
        var coords = MapToRemoteCoords(pos);
        if (coords is null) return;

        _longPressFired = true;
        _isTouchDragging = true; 

        ShowCursorAt(pos);

        await vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseDown,
            X = coords.Value.x,
            Y = coords.Value.y,
            Button = 2,
        });
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
            if (coords is null) return;

            ShowCursorAt(pos);
            _ = vm.SendInputAsync(new InputEvent
            {
                EventType = InputEventTypes.MouseMove,
                X = coords.Value.x,
                Y = coords.Value.y,
            });
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
                    _touchMoved = true;
                }

                if (_touchMoved || _isTouchDragging)
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastMouseMoveTime < MouseMoveThrottle) return;
                    _lastMouseMoveTime = now;

                    var coords = MapToRemoteCoords(pos);
                    if (coords is not null)
                    {
                        ShowCursorAt(pos);

                        if (!_isTouchDragging && !_longPressFired && dist > DragThreshold)
                        {
                            _isTouchDragging = true;
                            _ = vm.SendInputAsync(new InputEvent
                            {
                                EventType = InputEventTypes.MouseDown,
                                X = coords.Value.x,
                                Y = coords.Value.y,
                                Button = 0,
                            });
                        }
                        else
                        {
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

        if (IsTouchInput(e))
        {
            _activePointers.Remove(e.Pointer.Id);

            if (_isMultiTouchGesture)
            {
                if (_activePointers.Count < 2)
                    _isMultiTouchGesture = false;
                return;
            }

            if (_isTouchDragging || _longPressFired)
            {
                int button = _longPressFired ? 2 : 0;
                _ = vm.SendInputAsync(new InputEvent
                {
                    EventType = InputEventTypes.MouseUp,
                    Button = button,
                });
                _isTouchDragging = false;
                return;
            }

            if (!_touchMoved && _activePointers.Count == 0)
            {
                var elapsed = (DateTime.UtcNow - _touchDownTime).TotalMilliseconds;
                if (elapsed < LongPressMs)
                {
                    var coords = MapToRemoteCoords(_touchDownPos);
                    if (coords is not null)
                    {
                        ShowCursorAt(_touchDownPos);
                        _ = vm.SendInputAsync(new InputEvent
                        {
                            EventType = InputEventTypes.MouseClick,
                            X = coords.Value.x,
                            Y = coords.Value.y,
                            Button = 0,
                        });
                    }
                }
            }
            return;
        }

        _activePointers.Remove(e.Pointer.Id);

        int mouseButton = 0;
        var props = e.GetCurrentPoint(this).Properties;

        if (props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased) mouseButton = 0;
        else if (props.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased) mouseButton = 1;
        else if (props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased) mouseButton = 2;
        else
        {
            mouseButton = e.InitialPressMouseButton switch
            {
                Avalonia.Input.MouseButton.Left => 0,
                Avalonia.Input.MouseButton.Middle => 1,
                Avalonia.Input.MouseButton.Right => 2,
                _ => 0
            };
        }

        _ = vm.SendInputAsync(new InputEvent
        {
            EventType = InputEventTypes.MouseUp,
            Button = mouseButton,
        });
    }

    private void OnViewportPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
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
            e.Handled = true;
        }
    }

    // ═══════════════ Keyboard ═══════════════

    private static readonly Dictionary<Key, int> _keyToVirtualKey = new()
    {
        { Key.A, 0x41 }, { Key.B, 0x42 }, { Key.C, 0x43 }, { Key.D, 0x44 }, { Key.E, 0x45 },
        { Key.F, 0x46 }, { Key.G, 0x47 }, { Key.H, 0x48 }, { Key.I, 0x49 }, { Key.J, 0x4A },
        { Key.K, 0x4B }, { Key.L, 0x4C }, { Key.M, 0x4D }, { Key.N, 0x4E }, { Key.O, 0x4F },
        { Key.P, 0x50 }, { Key.Q, 0x51 }, { Key.R, 0x52 }, { Key.S, 0x53 }, { Key.T, 0x54 },
        { Key.U, 0x55 }, { Key.V, 0x56 }, { Key.W, 0x57 }, { Key.X, 0x58 }, { Key.Y, 0x59 },
        { Key.Z, 0x5A },
        { Key.D0, 0x30 }, { Key.D1, 0x31 }, { Key.D2, 0x32 }, { Key.D3, 0x33 }, { Key.D4, 0x34 },
        { Key.D5, 0x35 }, { Key.D6, 0x36 }, { Key.D7, 0x37 }, { Key.D8, 0x38 }, { Key.D9, 0x39 },
        { Key.NumPad0, 0x60 }, { Key.NumPad1, 0x61 }, { Key.NumPad2, 0x62 }, { Key.NumPad3, 0x63 },
        { Key.NumPad4, 0x64 }, { Key.NumPad5, 0x65 }, { Key.NumPad6, 0x66 }, { Key.NumPad7, 0x67 },
        { Key.NumPad8, 0x68 }, { Key.NumPad9, 0x69 },
        { Key.F1, 0x70 }, { Key.F2, 0x71 }, { Key.F3, 0x72 }, { Key.F4, 0x73 }, { Key.F5, 0x74 },
        { Key.F6, 0x75 }, { Key.F7, 0x76 }, { Key.F8, 0x77 }, { Key.F9, 0x78 }, { Key.F10, 0x79 },
        { Key.F11, 0x7A }, { Key.F12, 0x7B },
        { Key.Enter, 0x0D }, { Key.Escape, 0x1B }, { Key.Back, 0x08 }, { Key.Tab, 0x09 },
        { Key.Space, 0x20 },
        { Key.Left, 0x25 }, { Key.Up, 0x26 }, { Key.Right, 0x27 }, { Key.Down, 0x28 },
        { Key.Insert, 0x2D }, { Key.Delete, 0x2E }, { Key.Home, 0x24 }, { Key.End, 0x23 },
        { Key.PageUp, 0x21 }, { Key.PageDown, 0x22 },
        { Key.LeftShift, 0xA0 }, { Key.RightShift, 0xA1 },
        { Key.LeftCtrl, 0xA2 }, { Key.RightCtrl, 0xA3 },
        { Key.LeftAlt, 0xA4 }, { Key.RightAlt, 0xA5 },
        { Key.LWin, 0x5B }, { Key.RWin, 0x5C },
        { Key.OemPlus, 0xBB }, { Key.OemComma, 0xBC }, { Key.OemMinus, 0xBD }, { Key.OemPeriod, 0xBE },
        { Key.OemQuestion, 0xBF }, { Key.OemTilde, 0xC0 },
        { Key.OemOpenBrackets, 0xDB }, { Key.OemPipe, 0xDC }, { Key.OemCloseBrackets, 0xDD }, { Key.OemQuotes, 0xDE },
    };

    private static int MapKeyToVirtualKey(Key key) => _keyToVirtualKey.TryGetValue(key, out var vk) ? vk : 0;

    private async void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;
        var keyCode = MapKeyToVirtualKey(e.Key);
        if (keyCode == 0) return;
        await vm.SendInputAsync(new InputEvent { EventType = InputEventTypes.KeyDown, KeyCode = keyCode });
        e.Handled = true;
    }

    private async void OnViewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm || !vm.IsStreaming) return;
        var keyCode = MapKeyToVirtualKey(e.Key);
        if (keyCode == 0) return;
        await vm.SendInputAsync(new InputEvent { EventType = InputEventTypes.KeyUp, KeyCode = keyCode });
        e.Handled = true;
    }

    // ═══════════════ Helpers ═══════════════

    private void CancelLongPress() => _longPressFired = false;

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
