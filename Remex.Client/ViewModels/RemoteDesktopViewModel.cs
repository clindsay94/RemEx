using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Client.Services.Network;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel for the Remote Desktop page.
/// Provides remote desktop / screen sharing functionality.
/// </summary>
public partial class RemoteDesktopViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly RemoteDesktopService _desktopService;
    private readonly IImmersiveModeService? _immersiveMode;
    private int _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    public ConnectionViewModel Connection { get; }

    // ═══════════════ Observable Properties ═══════════════

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopStreamCommand))]
    private bool _isStreaming;

    [ObservableProperty]
    private Bitmap? _currentFrame;

    [ObservableProperty]
    private int _quality = 50;

    [ObservableProperty]
    private double _scale = 0.5;

    [ObservableProperty]
    private int _targetFps = 10;

    [ObservableProperty]
    private string _resolution = "—";

    [ObservableProperty]
    private double _actualFps;

    [ObservableProperty]
    private string _statusText = "Not streaming";

    /// <summary>True when the host reports an error (e.g. capture failure). Shown as overlay during streaming.</summary>
    [ObservableProperty]
    private bool _hasStreamError;

    [ObservableProperty]
    private bool _isFullScreen;

    [ObservableProperty]
    private bool _isViewportZoomed;

    [ObservableProperty]
    private string _viewportZoomText = "Zoom: 1.0×";

    [ObservableProperty]
    private bool _showCursorPad;

    /// <summary>
    /// When true, all touch input is treated as pen/stylus (tap = click, drag = click-drag).
    /// Useful when S-Pen is not auto-detected by the platform.
    /// </summary>
    [ObservableProperty]
    private bool _stylusMode;

    /// <summary>
    /// Last known cursor X position within the viewport (local coords, for crosshair overlay).
    /// </summary>
    [ObservableProperty]
    private double _cursorIndicatorX;

    /// <summary>
    /// Last known cursor Y position within the viewport (local coords, for crosshair overlay).
    /// </summary>
    [ObservableProperty]
    private double _cursorIndicatorY;

    /// <summary>
    /// Whether the cursor indicator should be visible (hidden after timeout or when not streaming).
    /// </summary>
    [ObservableProperty]
    private bool _isCursorVisible;

    /// <summary>Remote screen width in pixels (native).</summary>
    public int ScreenWidth { get; private set; }

    /// <summary>Remote screen height in pixels (native).</summary>
    public int ScreenHeight { get; private set; }

    // ═══════════════ Connection Panel ═══════════════

    /// <summary>Show/hide the inline host panel.</summary>
    [ObservableProperty]
    private bool _showConnectionPanel = true;

    public RemoteDesktopViewModel(ConnectionViewModel connection, ShellViewModel shell, IImmersiveModeService? immersiveMode = null)
    {
        Connection = connection;
        _shell = shell;
        _immersiveMode = immersiveMode;
        _desktopService = new RemoteDesktopService();
        _desktopService.FrameReceived += OnFrameReceived;
        _desktopService.MetaReceived += OnMetaReceived;
        _desktopService.ErrorReceived += OnErrorReceived;
        _desktopService.Disconnected += OnDisconnected;
    }

    // ═══════════════ Commands ═══════════════

    private bool CanStartStream() => !IsStreaming && Connection.IsConnected;
    private bool CanStopStream() => IsStreaming;

    [RelayCommand(CanExecute = nameof(CanStartStream))]
    private async Task StartStreamAsync()
    {
        try
        {
            StatusText = "Connecting...";

            await _desktopService.ConnectAsync(Connection.HostAddress);

            var config = new DesktopConfig
            {
                Quality = Quality,
                Scale = Scale,
                TargetFps = TargetFps,
            };
            await _desktopService.StartStreamAsync(config);

            IsStreaming = true;
            HasStreamError = false;
            _fpsWindowStart = DateTime.UtcNow;
            _frameCount = 0;
            StatusText = "Streaming";
        }
        catch (Exception ex)
        {
            try
            {
                // Best-effort cleanup in case ConnectAsync succeeded but StartStreamAsync failed.
                await _desktopService.StopStreamAsync();
            }
            catch
            {
                // Ignore cleanup errors.
            }
            finally
            {
                _desktopService.Disconnect();
            }

            StatusText = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopStream))]
    private async Task StopStreamAsync()
    {
        try
        {
            await _desktopService.StopStreamAsync();
        }
        catch { /* best effort */ }
        finally
        {
            _desktopService.Disconnect();
            IsStreaming = false;
            HasStreamError = false;
            StatusText = "Stopped";
            ActualFps = 0;
        }
    }

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        if (!IsStreaming) return;

        var config = new DesktopConfig
        {
            Quality = Quality,
            Scale = Scale,
            TargetFps = TargetFps,
        };
        await _desktopService.SendConfigAsync(config);
    }

    [RelayCommand]
    private void NavigateBack()
    {
        _shell.IsShellChromeHidden = false;
        IsFullScreen = false;
        _immersiveMode?.ExitImmersiveMode();
        _shell.NavigateToHome();
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
        _shell.IsShellChromeHidden = IsFullScreen;

        if (IsFullScreen)
            _immersiveMode?.EnterImmersiveMode();
        else
            _immersiveMode?.ExitImmersiveMode();
    }

    [RelayCommand]
    private void ToggleCursorPad() => ShowCursorPad = !ShowCursorPad;

    [RelayCommand]
    private void ToggleStylusMode() => StylusMode = !StylusMode;

    [RelayCommand]
    private void ToggleConnectionPanel() => ShowConnectionPanel = !ShowConnectionPanel;

    [RelayCommand]
    private void SaveConnection()
    {
        // Connection.HostAddress and Connection.AccessKey are already bound via XAML
        // and auto-persist through ConnectionViewModel's property change handlers.
        ShowConnectionPanel = false;
        StatusText = "Connection settings saved";
    }

    [RelayCommand]
    private void ResetZoom()
    {
        IsViewportZoomed = false;
        ViewportZoomText = "Zoom: 1.0×";
        ViewportZoomResetRequested?.Invoke();
    }

    /// <summary>
    /// Raised when the ViewModel requests the View to reset its viewport transform.
    /// </summary>
    public event Action? ViewportZoomResetRequested;

    /// <summary>
    /// Called from the View when the local viewport zoom changes.
    /// </summary>
    public void UpdateViewportZoom(double zoom)
    {
        IsViewportZoomed = zoom > 1.01;
        ViewportZoomText = $"Zoom: {zoom:F1}×";
    }

    // ═══════════════ Input Forwarding ═══════════════

    public async Task SendInputAsync(InputEvent input)
    {
        if (!IsStreaming) return;
        await _desktopService.SendInputAsync(input);
    }

    // ═══════════════ Event Handlers ═══════════════

    private int _consecutiveDecodeFailures;

    private void OnFrameReceived(byte[] jpegBytes)
    {
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var bitmap = new Bitmap(ms);

            Dispatcher.UIThread.Post(() =>
            {
                var old = CurrentFrame;
                CurrentFrame = bitmap;
                old?.Dispose();
            });

            _consecutiveDecodeFailures = 0;
            HasStreamError = false;

            // FPS calculation
            _frameCount++;
            var elapsed = (DateTime.UtcNow - _fpsWindowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                var fps = _frameCount / elapsed;
                Dispatcher.UIThread.Post(() => ActualFps = Math.Round(fps, 1));
                _frameCount = 0;
                _fpsWindowStart = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _consecutiveDecodeFailures++;
            if (_consecutiveDecodeFailures <= 3)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = $"Frame decode error ({jpegBytes.Length} bytes): {ex.GetType().Name}: {ex.Message}";
                    HasStreamError = true;
                });
            }
        }
    }

    private void OnMetaReceived(DesktopMeta meta)
    {
        // Detect self-connection (infinite mirror prevention)
        if (!string.IsNullOrEmpty(meta.HostInstanceId) &&
            meta.HostInstanceId == App.EmbeddedHostInstanceId)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                StatusText = "Self-connection detected — cannot mirror own screen";
                await StopStreamAsync();
            });
            return;
        }

        ScreenWidth = meta.ScreenWidth;
        ScreenHeight = meta.ScreenHeight;

        Dispatcher.UIThread.Post(() =>
        {
            Resolution = $"{meta.ScreenWidth}×{meta.ScreenHeight}";
        });
    }

    private void OnDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsStreaming = false;
            StatusText = "Disconnected";
            ActualFps = 0;
        });
    }

    private void OnErrorReceived(string errorText)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = errorText;
            HasStreamError = true;
        });
    }

    public void Dispose()
    {
        _desktopService.FrameReceived -= OnFrameReceived;
        _desktopService.MetaReceived -= OnMetaReceived;
        _desktopService.ErrorReceived -= OnErrorReceived;
        _desktopService.Disconnected -= OnDisconnected;
        _desktopService.Dispose();
        CurrentFrame?.Dispose();
    }
}
