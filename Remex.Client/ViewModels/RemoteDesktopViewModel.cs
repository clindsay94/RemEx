using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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
    [NotifyPropertyChangedFor(nameof(SelectedScaleIndex))]
    private double _scale = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFpsIndex))]
    private int _targetFps = 10;

    [ObservableProperty]
    private string _resolution = "—";

    [ObservableProperty]
    private double _actualFps;

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance["Status_NotStreaming"];

    /// <summary>True when the host reports an error (e.g. capture failure). Shown as overlay during streaming.</summary>
    [ObservableProperty]
    private bool _hasStreamError;

    [ObservableProperty]
    private bool _isFullScreen;

    [ObservableProperty]
    private bool _isViewportZoomed;

    [ObservableProperty]
    private string _viewportZoomText = string.Format(LocalizationService.Instance["Status_ZoomFormat"], 1.0);

    [ObservableProperty]
    private bool _showCursorPad;

    [ObservableProperty]
    private bool _isCompactCursorPad;

    [ObservableProperty]
    private double _cursorPadScale = 1.0;

    [ObservableProperty]
    private Thickness _cursorPadMargin = new(0, 0, 24, 24);

    [ObservableProperty]
    private string _cursorPadModeText = LocalizationService.Instance["Status_PadFull"];

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
        Connection.PropertyChanged += OnConnectionPropertyChanged;

        // Sync stream defaults from persisted Settings panel values.
        // Without this, the Quality/FPS sliders in Settings have no effect on the actual stream.
        var profile = shell.LayoutService.CurrentProfile;
        if (profile != null)
        {
            Quality = profile.StreamQuality;
            TargetFps = profile.StreamFps;
            Scale = SnapScale(profile.StreamScale);
        }
    }

    // ═══════════════ ComboBox index helpers ═══════════════

    /// <summary>Snaps a scale value to the nearest valid option (0.25, 0.50, 0.75, 1.0).</summary>
    private static double SnapScale(double value) => value switch
    {
        < 0.375 => 0.25,
        < 0.625 => 0.50,
        < 0.875 => 0.75,
        _ => 1.0,
    };

    /// <summary>Zero-based index of the current Scale value in the Scale ComboBox (25%, 50%, 75%, 100%).</summary>
    public int SelectedScaleIndex
    {
        get => Scale switch
        {
            <= 0.25 => 0,
            <= 0.5 => 1,
            <= 0.75 => 2,
            _ => 3,
        };
        set => Scale = value switch
        {
            0 => 0.25,
            1 => 0.5,
            2 => 0.75,
            3 => 1.0,
            _ => 0.5,
        };
    }

    /// <summary>Zero-based index of the current TargetFps value in the FPS ComboBox (5/10/15/20/30/60/120/240/360).</summary>
    public int SelectedFpsIndex
    {
        get => TargetFps switch
        {
            <= 5 => 0,
            <= 10 => 1,
            <= 15 => 2,
            <= 20 => 3,
            <= 30 => 4,
            <= 60 => 5,
            <= 120 => 6,
            <= 240 => 7,
            _ => 8,
        };
        set => TargetFps = value switch
        {
            0 => 5,
            1 => 10,
            2 => 15,
            3 => 20,
            4 => 30,
            5 => 60,
            6 => 120,
            7 => 240,
            8 => 360,
            _ => 10,
        };
    }

    // ═══════════════ Commands ═══════════════

    public bool IsRemoteDesktopSupported => Connection.SupportsRemoteDesktop;

    public string RemoteDesktopCapabilityText =>
        Connection.IsConnected
            ? Connection.RemoteDesktopAvailabilitySummary
            : LocalizationService.Instance["Status_ConnectToCheckDesktop"];

    private bool CanStartStream() => !IsStreaming && Connection.IsConnected && IsRemoteDesktopSupported;
    private bool CanStopStream() => IsStreaming;

    [RelayCommand(CanExecute = nameof(CanStartStream))]
    private async Task StartStreamAsync()
    {
        if (!IsRemoteDesktopSupported)
        {
            StatusText = Connection.RemoteDesktopAvailabilitySummary;
            HasStreamError = true;
            return;
        }

        try
        {
            StatusText = LocalizationService.Instance["Status_StreamConnecting"];

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
            StatusText = LocalizationService.Instance["Status_Streaming"];
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

            StatusText = string.Format(LocalizationService.Instance["Status_StreamFailedFormat"], ex.Message);
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
            StatusText = LocalizationService.Instance["Status_Stopped"];
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
        PersistStreamSettings();
    }

    private void PersistStreamSettings()
    {
        var updated = _shell.LayoutService.CurrentProfile with
        {
            StreamQuality = Quality,
            StreamFps = TargetFps,
            StreamScale = Scale,
        };
        _shell.LayoutService.RequestSave(updated);
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
    private void ToggleCompactCursorPad()
    {
        IsCompactCursorPad = !IsCompactCursorPad;
        ShowCursorPad = true;

        if (IsCompactCursorPad)
        {
            CursorPadScale = 0.62;
            CursorPadMargin = new Thickness(0, 0, 14, 12);
            CursorPadModeText = LocalizationService.Instance["Status_PadCompact"];
            StatusText = LocalizationService.Instance["Status_PadCompactTooltip"];
        }
        else
        {
            CursorPadScale = 1.0;
            CursorPadMargin = new Thickness(0, 0, 24, 24);
            CursorPadModeText = LocalizationService.Instance["Status_PadFull"];
            StatusText = LocalizationService.Instance["Status_PadFullTooltip"];
        }
    }

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
        StatusText = LocalizationService.Instance["Status_ConnectionSaved"];
    }

    [RelayCommand]
    private void ResetZoom()
    {
        IsViewportZoomed = false;
        ViewportZoomText = string.Format(LocalizationService.Instance["Status_ZoomFormat"], 1.0);
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
        ViewportZoomText = string.Format(LocalizationService.Instance["RemoteDesktop_ZoomFormat"], $"{zoom:F1}");
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
                    StatusText = string.Format(LocalizationService.Instance["Status_FrameDecodeErrorFormat"], jpegBytes.Length, ex.GetType().Name, ex.Message);
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
                StatusText = LocalizationService.Instance["Status_SelfConnection"];
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
            StatusText = LocalizationService.Instance["Status_Disconnected"];
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

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.HostCapabilities)
            or nameof(ConnectionViewModel.SupportsRemoteDesktop))
        {
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsRemoteDesktopSupported));
                OnPropertyChanged(nameof(RemoteDesktopCapabilityText));
                StartStreamCommand.NotifyCanExecuteChanged();

                if (!Connection.IsConnected && !IsStreaming)
                {
                    StatusText = LocalizationService.Instance["Status_NotStreaming"];
                    HasStreamError = false;
                }
            });
        }
    }

    public void Dispose()
    {
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _desktopService.FrameReceived -= OnFrameReceived;
        _desktopService.MetaReceived -= OnMetaReceived;
        _desktopService.ErrorReceived -= OnErrorReceived;
        _desktopService.Disconnected -= OnDisconnected;
        _desktopService.Dispose();
        CurrentFrame?.Dispose();
    }
}
