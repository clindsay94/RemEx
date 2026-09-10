using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Network;
using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Remote Desktop page.
/// Provides remote desktop / screen sharing functionality.
/// </summary>
public partial class RemoteDesktopViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly RemoteDesktopService _desktopService;
    private readonly IImmersiveModeService? _immersiveMode;
    private readonly ILogger<RemoteDesktopViewModel> _logger;
    private int _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    public ConnectionViewModel Connection { get; }

    // ═══════════════ Observable Properties ═══════════════

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchDisplayCommand))]
    private bool _isStreaming;

    /// <summary>
    /// Whether the live-stream dot should pulse (RemEx-s19yc): only while actually streaming and
    /// the user hasn't opted in to reduced motion. Notified both when streaming starts/stops and
    /// when the shell's reduced-motion preference changes.
    /// </summary>
    public bool ShowStreamPulse => IsStreaming && !_shell.IsReducedMotion;

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowStreamPulse));

    [ObservableProperty]
    private Bitmap? _currentFrame;

    [ObservableProperty]
    private int _quality = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScaleIndex))]
    private double _scale = 1.0;

    [ObservableProperty]
    private int _targetFps = 120;

    [ObservableProperty]
    private string _resolution = "—";

    [ObservableProperty]
    private double _actualFps;

    // Held as a RESOLVER, not as resolved text. Every one of the ~20 places that set a status
    // used to store an already-localized string, so a language switch could not reach any of them:
    // the message simply stayed in the old language until the next user action replaced it. A
    // resolver is re-invoked on each get, so re-raising the property is enough (RemEx-qlfj).
    private Func<string>? _statusResolver;

    /// <summary>Current one-line status, resolved in the caller's current language.</summary>
    public string StatusText =>
        _statusResolver?.Invoke() ?? LocalizationService.Instance["Status_NotStreaming"];

    /// <summary>Sets the status from a resolver that is re-evaluated whenever the language changes.</summary>
    private void SetStatus(Func<string> resolver)
    {
        _statusResolver = resolver;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Sets the status to text authored by the HOST, shown verbatim and never localized here.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate entry point rather than a resolver over a captured string. The host's
    /// own wording is the most useful text in the flow and must not be replaced by a generic
    /// sentence, which is the distinction RemEx-mznc and RemEx-s4p4 settled for file transfer by
    /// dispatching on exception TYPE rather than on message text.
    /// </remarks>
    private void SetStatusLiteral(string hostText)
    {
        _statusResolver = () => hostText;
        OnPropertyChanged(nameof(StatusText));
    }

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

    public int DesktopLeft { get; private set; }

    public int DesktopTop { get; private set; }

    // ═══════════════ Connection Panel ═══════════════

    /// <summary>Show/hide the inline host panel.</summary>
    [ObservableProperty]
    private bool _showConnectionPanel = true;

    [ObservableProperty]
    private bool _showWindowPanel;

    [ObservableProperty]
    private string _windowSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<DesktopWindowInfo> _availableWindows = new();

    [ObservableProperty]
    private DesktopWindowInfo? _selectedWindow;

    // Same treatment, same reason (RemEx-qlfj).
    private Func<string>? _windowControlStatusResolver;

    /// <summary>Window-control status line, resolved in the caller's current language.</summary>
    public string WindowControlStatusText =>
        _windowControlStatusResolver?.Invoke()
        ?? LocalizationService.Instance["RemoteDesktop_WindowControlUnavailable"];

    private void SetWindowControlStatus(Func<string> resolver)
    {
        _windowControlStatusResolver = resolver;
        OnPropertyChanged(nameof(WindowControlStatusText));
    }

    [ObservableProperty]
    private int _windowResizeWidth = 1280;

    [ObservableProperty]
    private int _windowResizeHeight = 720;

    [ObservableProperty]
    private int _targetDesktopNumber = 1;

    [ObservableProperty]
    private ObservableCollection<DesktopTargetOption> _availableDisplayTargets = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchDisplayCommand))]
    private DesktopTargetOption? _selectedDisplayTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStreamCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchDisplayCommand))]
    private bool _isLoadingDisplays;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchDisplayCommand))]
    private bool _isSwitchingDisplay;

    [ObservableProperty]
    private double _remoteCursorViewportX;

    [ObservableProperty]
    private double _remoteCursorViewportY;

    [ObservableProperty]
    private bool _isRemoteCursorOverlayVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteCursorBitmap))]
    private Bitmap? _remoteCursorBitmap;

    [ObservableProperty]
    private double _remoteCursorBitmapLeft;

    [ObservableProperty]
    private double _remoteCursorBitmapTop;

    [ObservableProperty]
    private double _remoteCursorBitmapWidth;

    [ObservableProperty]
    private double _remoteCursorBitmapHeight;

    private int _remoteCursorHostX;
    private int _remoteCursorHostY;
    private bool _remoteCursorHostVisible;
    private long _currentStreamSerial;
    private long _activeCursorShapeSerial;
    private int _cursorHotspotX;
    private int _cursorHotspotY;
    private int _cursorShapeWidth;
    private int _cursorShapeHeight;
    private readonly Dictionary<long, Bitmap> _cursorShapeCache = new();
    private readonly Dictionary<long, DesktopCursorShape> _cursorShapeInfoCache = new();

    public RemoteDesktopViewModel(
        ConnectionViewModel connection,
        ShellViewModel shell,
        IImmersiveModeService? immersiveMode = null,
        ILogger<RemoteDesktopViewModel>? logger = null)
    {
        Connection = connection;
        _shell = shell;
        _immersiveMode = immersiveMode;
        _logger = logger ?? NullLogger<RemoteDesktopViewModel>.Instance;
        _desktopService = new RemoteDesktopService();
        _desktopService.FrameReceived += OnFrameReceived;
        _desktopService.MetaReceived += OnMetaReceived;
        _desktopService.ErrorReceived += OnErrorReceived;
        _desktopService.StreamDescriptorReceived += OnStreamDescriptorReceived;
        _desktopService.CursorStateReceived += OnCursorStateReceived;
        _desktopService.CursorShapeReceived += OnCursorShapeReceived;
        _desktopService.Disconnected += OnDisconnected;
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
        _shell.PropertyChanged += OnShellPropertyChanged;

        // A REPLACED PROFILE MUST NOT LEAVE Quality/TargetFps/Scale STALE (RemEx-w6ipy). Unlike
        // RemoteViewModel this VM can hold a live stream, so a savefile import must never drop or
        // rebuild the cached instance mid-session - only the three fields snapshotted below are
        // re-read, in place, and the stream itself is left untouched.
        _shell.LayoutService.ProfileReplaced += OnProfileReplaced;

        // Sync stream defaults from persisted Settings panel values.
        // Without this, the Quality/FPS sliders in Settings have no effect on the actual stream.
        var profile = shell.LayoutService.CurrentProfile;
        if (profile != null)
        {
            Quality = profile.StreamQuality;
            TargetFps = profile.StreamFps;
            Scale = SnapScale(profile.StreamScale);
        }

        if (Connection.IsConnected && IsRemoteDesktopSupported)
        {
            _ = RefreshDisplayTargetsAsync();
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

    // ═══════════════ Commands ═══════════════

    public bool IsRemoteDesktopSupported => Connection.SupportsRemoteDesktop;

    /// <summary>
    /// Re-raises the localized computed properties when the user switches language.
    /// </summary>
    /// <remarks>
    /// <see cref="LocalizationService"/> raises <c>PropertyChanged</c> for the indexer on
    /// <c>SetCulture</c>, which refreshes bindings that read it directly - but not properties that
    /// merely CALL the indexer inside an expression body. Those have to be re-raised by name, which
    /// three sibling view-models already do and this one did not, so these two labels kept their
    /// old-language text until something unrelated happened to re-raise them (RemEx-6h3q).
    /// <para>
    /// No cascade rescued it: <c>ConnectionViewModel.OnLocaleChanged</c> reassigns its own
    /// <c>StatusText</c> and never touches <c>IsConnected</c>, <c>HostCapabilities</c> or
    /// <c>SupportsRemoteDesktop</c>, which are the only three triggers here that re-raise them.
    /// </para>
    /// </remarks>
    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(RemoteDesktopCapabilityText));
            OnPropertyChanged(nameof(WindowControlCapabilityText));

            // The two status lines resolve through their resolvers, so re-raising is the whole
            // refresh - no need to know WHICH message is currently showing, which is what made the
            // per-site approach unworkable (several messages set HasStreamError, so no condition
            // distinguishes them).
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(WindowControlStatusText));
        });
    }

    public string RemoteDesktopCapabilityText =>
        Connection.IsConnected
            ? Connection.RemoteDesktopAvailabilitySummary
            : LocalizationService.Instance["Status_ConnectToCheckDesktop"];

    public bool SupportsAdvancedWindowControl => Connection.HostCapabilities?.SupportsAdvancedWindowControl ?? false;

    public string WindowControlCapabilityText =>
        SupportsAdvancedWindowControl
            ? string.Format(
                LocalizationService.Instance["RemoteDesktop_WindowControlViaFormat"],
                // Substituted after a preposition ("via {0}"), so this fallback has to be a
                // noun phrase. The bare-word RemoteDesktop_BackendUnknown reads as an adverb
                // or a finite verb in several languages and is only grammatical in the
                // parenthetical frame RemoteDesktop_BackendReady uses.
                Connection.HostCapabilities?.WindowControlBackend
                    ?? LocalizationService.Instance["RemoteDesktop_BackendUnknownInline"])
            : LocalizationService.Instance["RemoteDesktop_AdvancedWindowControlUnavailable"];

    public bool HasDisplayTargets => AvailableDisplayTargets.Count > 0;

    private bool CanStartStream()
        => !IsStreaming &&
           !IsLoadingDisplays &&
           Connection.IsConnected &&
           IsRemoteDesktopSupported &&
           SelectedDisplayTarget is not null;
    private bool CanStopStream() => IsStreaming;
    private bool CanSwitchDisplay()
        => IsStreaming &&
           !IsLoadingDisplays &&
           !IsSwitchingDisplay &&
           SelectedDisplayTarget is not null;

    [RelayCommand(CanExecute = nameof(CanStartStream))]
    private async Task StartStreamAsync()
    {
        if (!IsRemoteDesktopSupported)
        {
            SetStatus(() => Connection.RemoteDesktopAvailabilitySummary);
            HasStreamError = true;
            return;
        }

        try
        {
            if (SelectedDisplayTarget is null)
            {
                await RefreshDisplayTargetsAsync();
                if (SelectedDisplayTarget is null)
                {
                    SetStatus(() => LocalizationService.Instance["RemoteDesktop_SelectDisplayTarget"]);
                    HasStreamError = true;
                    return;
                }
            }

            SetStatus(() => LocalizationService.Instance["Status_StreamConnecting"]);

            await _desktopService.ConnectAsync(Connection.HostAddress);

            var config = new DesktopConfig
            {
                Quality = Quality,
                Scale = Scale,
                TargetFps = TargetFps,
                DesktopProtocolVersion = 1,
                ClientCapabilities = new DesktopClientCapabilities
                {
                    SupportsDisplaySelection = true,
                    SupportsFrameEnvelope = true,
                    SupportsTargetSwitch = true,
                    SupportsCursorState = true,
                    SupportsCursorShape = true,
                },
                CaptureMode = SelectedDisplayTarget.CaptureMode,
                DisplayId = SelectedDisplayTarget.DisplayId,
                DisplayListVersion = SelectedDisplayTarget.DisplayListVersion,
            };
            await _desktopService.StartStreamAsync(config);

            IsStreaming = true;
            IsSwitchingDisplay = false;
            HasStreamError = false;
            _fpsWindowStart = DateTime.UtcNow;
            _frameCount = 0;
            SetStatus(() => LocalizationService.Instance["Status_Streaming"]);
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

            _logger.LogError(
                ex,
                "Failed to start the remote desktop stream for display {DisplayId} ({CaptureMode}).",
                SelectedDisplayTarget?.DisplayId,
                SelectedDisplayTarget?.CaptureMode);
            SetStatus(() => LocalizationService.Instance["Status_StreamFailed"]);
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
            IsSwitchingDisplay = false;
            HasStreamError = false;
            SetStatus(() => LocalizationService.Instance["Status_Stopped"]);
            ActualFps = 0;
            ResetRemoteCursorOverlay();
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
    private async Task RefreshDisplayTargetsAsync()
    {
        if (!Connection.IsConnected || !IsRemoteDesktopSupported || string.IsNullOrWhiteSpace(Connection.HostAddress))
        {
            ClearDisplayTargets();
            return;
        }

        var previousSelection = SelectedDisplayTarget;

        try
        {
            IsLoadingDisplays = true;
            SetStatus(() => LocalizationService.Instance["RemoteDesktop_LoadingDisplays"]);

            var catalog = await _desktopService.QueryDisplaysAsync(Connection.HostAddress);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyDisplayCatalog(catalog, previousSelection));

            if (!IsStreaming)
            {
                HasStreamError = false;
                SetStatus(() => LocalizationService.Instance["Status_NotStreaming"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load the remote desktop display list from {HostAddress}.",
                Connection.HostAddress);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ClearDisplayTargets();
                SetStatus(() => LocalizationService.Instance["Status_LoadDisplaysFailed"]);
                HasStreamError = true;
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoadingDisplays = false;
                StartStreamCommand.NotifyCanExecuteChanged();
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchDisplay))]
    private async Task SwitchDisplayAsync()
    {
        if (!IsStreaming || SelectedDisplayTarget is null)
        {
            return;
        }

        try
        {
            IsSwitchingDisplay = true;
            SetStatus(() => LocalizationService.Instance["RemoteDesktop_SwitchingDisplay"]);
            HasStreamError = false;
            ClearCurrentFrame();
            ResetRemoteCursorOverlay();

            await _desktopService.SwitchTargetAsync(new DesktopTargetSwitchRequest
            {
                Target = new DesktopCaptureTarget
                {
                    CaptureMode = SelectedDisplayTarget.CaptureMode,
                    DisplayId = SelectedDisplayTarget.DisplayId,
                },
                DisplayListVersion = SelectedDisplayTarget.DisplayListVersion,
            });
        }
        catch (Exception ex)
        {
            // The technical detail goes to the log, not to the status bar: that TextBlock is
            // floored at 140px with CharacterEllipsis (RemEx-9a46), so an exception message
            // would be trimmed to nothing useful anyway.
            _logger.LogError(
                ex,
                "Failed to switch the remote desktop display target to {DisplayId} ({CaptureMode}).",
                SelectedDisplayTarget?.DisplayId,
                SelectedDisplayTarget?.CaptureMode);
            SetStatus(() => LocalizationService.Instance["RemoteDesktop_DisplaySwitchFailed"]);
            HasStreamError = true;
        }
        finally
        {
            IsSwitchingDisplay = false;
        }
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
            SetStatus(() => LocalizationService.Instance["Status_PadCompactTooltip"]);
        }
        else
        {
            CursorPadScale = 1.0;
            CursorPadMargin = new Thickness(0, 0, 24, 24);
            CursorPadModeText = LocalizationService.Instance["Status_PadFull"];
            SetStatus(() => LocalizationService.Instance["Status_PadFullTooltip"]);
        }
    }

    [RelayCommand]
    private void ToggleStylusMode() => StylusMode = !StylusMode;

    [RelayCommand]
    private void ToggleConnectionPanel() => ShowConnectionPanel = !ShowConnectionPanel;

    [RelayCommand]
    private void ToggleWindowPanel() => ShowWindowPanel = !ShowWindowPanel;

    [RelayCommand]
    private void SaveConnection()
    {
        // Connection.HostAddress is already bound via XAML
        // and auto-persists through ConnectionViewModel's property change handlers.
        ShowConnectionPanel = false;
        SetStatus(() => LocalizationService.Instance["Status_ConnectionSaved"]);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        IsViewportZoomed = false;
        ViewportZoomText = string.Format(LocalizationService.Instance["Status_ZoomFormat"], 1.0);
        ViewportZoomResetRequested?.Invoke();
    }

    [RelayCommand]
    private async Task RefreshWindowsAsync()
    {
        if (!IsStreaming || !SupportsAdvancedWindowControl)
        {
            SetWindowControlStatus(() => WindowControlCapabilityText);
            return;
        }

        try
        {
            var result = await _desktopService.QueryWindowsAsync(new DesktopWindowQuery
            {
                SearchText = WindowSearchText,
                Limit = 50,
                IncludeAllDesktops = true,
            });

            ApplyWindowResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to query remote windows with search text {WindowSearchText}.",
                WindowSearchText);
            SetWindowControlStatus(() => LocalizationService.Instance["RemoteDesktop_WindowQueryFailed"]);
        }
    }

    [RelayCommand]
    private async Task ActivateSelectedWindowAsync() => await ExecuteWindowActionAsync(DesktopWindowActionTypes.Activate);

    [RelayCommand]
    private async Task RaiseSelectedWindowAsync() => await ExecuteWindowActionAsync(DesktopWindowActionTypes.Raise);

    [RelayCommand]
    private async Task MinimizeSelectedWindowAsync() => await ExecuteWindowActionAsync(DesktopWindowActionTypes.Minimize);

    [RelayCommand]
    private async Task CloseSelectedWindowAsync() => await ExecuteWindowActionAsync(DesktopWindowActionTypes.Close);

    [RelayCommand]
    private async Task ResizeSelectedWindowAsync()
    {
        await ExecuteWindowActionAsync(DesktopWindowActionTypes.Resize, action => action with
        {
            Width = Math.Max(100, WindowResizeWidth),
            Height = Math.Max(100, WindowResizeHeight),
        });
    }

    [RelayCommand]
    private async Task MoveSelectedWindowToDesktopAsync()
    {
        await ExecuteWindowActionAsync(DesktopWindowActionTypes.MoveToDesktop, action => action with
        {
            DesktopNumber = Math.Max(1, TargetDesktopNumber),
        });
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

    private async Task ExecuteWindowActionAsync(string actionType, Func<DesktopWindowAction, DesktopWindowAction>? transform = null)
    {
        if (!IsStreaming || !SupportsAdvancedWindowControl || SelectedWindow is null)
        {
            SetWindowControlStatus(() => WindowControlCapabilityText);
            return;
        }

        // Captured before the call, not read in the catch: ApplyWindowResult re-resolves
        // SelectedWindow from the refreshed list and falls back to the first entry if the id
        // is gone, so reading it after a throw can name a window the action never ran on.
        var windowId = SelectedWindow.Id;

        try
        {
            var action = new DesktopWindowAction
            {
                Action = actionType,
                WindowId = windowId,
            };

            var result = await _desktopService.ExecuteWindowActionAsync(transform?.Invoke(action) ?? action);
            ApplyWindowResult(result);

            if (result.Success && actionType is not DesktopWindowActionTypes.Close)
            {
                await RefreshWindowsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to run window action {WindowAction} on window {WindowId}.",
                actionType,
                windowId);
            SetWindowControlStatus(() => LocalizationService.Instance["RemoteDesktop_WindowActionFailed"]);
        }
    }

    private void ApplyWindowResult(DesktopWindowResult result)
    {
        SetWindowControlStatus(() => result.Success
            ? string.Format(
                LocalizationService.Instance["RemoteDesktop_BackendReady"],
                result.Backend ?? LocalizationService.Instance["RemoteDesktop_BackendUnknown"])
            : result.ErrorText ?? LocalizationService.Instance["RemoteDesktop_WindowControlFailed"]);

        if (result.Windows is null)
        {
            return;
        }

        AvailableWindows = new ObservableCollection<DesktopWindowInfo>(result.Windows);
        if (SelectedWindow is not null)
        {
            SelectedWindow = AvailableWindows.FirstOrDefault(window => window.Id == SelectedWindow.Id);
        }

        if (SelectedWindow is null && AvailableWindows.Count > 0)
        {
            SelectedWindow = AvailableWindows[0];
        }
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
                // The frame size and exception type are diagnostics, not something a
                // non-technical user can act on — they go to the log; the status bar gets
                // a plain-English sentence with a next step. Logged on the same <= 3
                // cadence as the status update so a sustained failure can't spam the log.
                _logger.LogWarning(
                    ex,
                    "Remote desktop frame decode failed ({FrameBytes} bytes, consecutive failure {FailureCount})",
                    jpegBytes.Length,
                    _consecutiveDecodeFailures);

                Dispatcher.UIThread.Post(() =>
                {
                    SetStatus(() => LocalizationService.Instance["Status_FrameDecodeError"]);
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
                SetStatus(() => LocalizationService.Instance["Status_SelfConnection"]);
                await StopStreamAsync();
            });
            return;
        }

        if (meta.StreamSerial > 0 && meta.StreamSerial != _currentStreamSerial)
        {
            _currentStreamSerial = meta.StreamSerial;
            Dispatcher.UIThread.Post(ClearCurrentFrame);
            ResetRemoteCursorOverlay();
        }

        ScreenWidth = meta.ScreenWidth;
        ScreenHeight = meta.ScreenHeight;
        DesktopLeft = meta.DesktopLeft;
        DesktopTop = meta.DesktopTop;

        Dispatcher.UIThread.Post(() =>
        {
            Resolution = $"{meta.ScreenWidth}×{meta.ScreenHeight}";
            if (IsStreaming && !IsSwitchingDisplay)
            {
                SetStatus(() => LocalizationService.Instance["Status_Streaming"]);
            }
        });
    }

    private void OnStreamDescriptorReceived(DesktopStreamDescriptor descriptor)
    {
        if (descriptor.StreamSerial > 0 && descriptor.StreamSerial != _currentStreamSerial)
        {
            _currentStreamSerial = descriptor.StreamSerial;
            Dispatcher.UIThread.Post(ClearCurrentFrame);
            ResetRemoteCursorOverlay();
        }
    }

    private void OnCursorStateReceived(DesktopCursorState state)
    {
        if (state.StreamSerial > 0 && state.StreamSerial < _currentStreamSerial)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _currentStreamSerial = Math.Max(_currentStreamSerial, state.StreamSerial);
            _remoteCursorHostX = state.X;
            _remoteCursorHostY = state.Y;
            _remoteCursorHostVisible = state.Visible;
            _activeCursorShapeSerial = state.ShapeSerial;
            _cursorHotspotX = state.HotspotX;
            _cursorHotspotY = state.HotspotY;
            if (_activeCursorShapeSerial > 0 &&
                _cursorShapeCache.TryGetValue(_activeCursorShapeSerial, out var cursorBitmap))
            {
                if (_cursorShapeInfoCache.TryGetValue(_activeCursorShapeSerial, out var cursorShape))
                {
                    _cursorShapeWidth = cursorShape.Width;
                    _cursorShapeHeight = cursorShape.Height;
                }
                RemoteCursorBitmap = cursorBitmap;
            }
            else
            {
                RemoteCursorBitmap = null;
            }
            OnPropertyChanged(nameof(RemoteCursorHostX));
            OnPropertyChanged(nameof(RemoteCursorHostY));
            OnPropertyChanged(nameof(RemoteCursorHostVisible));
            OnPropertyChanged(nameof(ActiveCursorShapeSerial));
            OnPropertyChanged(nameof(CursorHotspotX));
            OnPropertyChanged(nameof(CursorHotspotY));
            OnPropertyChanged(nameof(CursorShapeWidth));
            OnPropertyChanged(nameof(CursorShapeHeight));
        });
    }

    private void OnCursorShapeReceived(DesktopCursorShape shape)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var bitmap = CreateCursorBitmap(shape);
            if (_cursorShapeCache.TryGetValue(shape.ShapeSerial, out var existingBitmap) && !ReferenceEquals(existingBitmap, bitmap))
            {
                existingBitmap.Dispose();
            }

            _cursorShapeCache[shape.ShapeSerial] = bitmap;
            _cursorShapeInfoCache[shape.ShapeSerial] = shape;
            if (shape.ShapeSerial == _activeCursorShapeSerial)
            {
                _cursorShapeWidth = shape.Width;
                _cursorShapeHeight = shape.Height;
                RemoteCursorBitmap = bitmap;
                OnPropertyChanged(nameof(CursorShapeWidth));
                OnPropertyChanged(nameof(CursorShapeHeight));
            }
        });
    }

    private void OnDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsStreaming = false;
            IsSwitchingDisplay = false;
            SetStatus(() => LocalizationService.Instance["Status_Disconnected"]);
            ActualFps = 0;
            ResetRemoteCursorOverlay();
        });
    }

    private void OnErrorReceived(string errorText)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetStatusLiteral(errorText);
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
                OnPropertyChanged(nameof(SupportsAdvancedWindowControl));
                OnPropertyChanged(nameof(WindowControlCapabilityText));
                OnPropertyChanged(nameof(HasDisplayTargets));
                StartStreamCommand.NotifyCanExecuteChanged();

                if (!Connection.IsConnected && !IsStreaming)
                {
                    ClearDisplayTargets();
                    SetStatus(() => LocalizationService.Instance["Status_NotStreaming"]);
                    HasStreamError = false;
                    SetWindowControlStatus(() => WindowControlCapabilityText);
                }
                else if (Connection.IsConnected && IsRemoteDesktopSupported && !IsStreaming)
                {
                    _ = RefreshDisplayTargetsAsync();
                }
            });
        }
    }

    /// <summary>Re-raises <see cref="ShowStreamPulse"/> when reduced motion is toggled mid-stream.</summary>
    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsReducedMotion))
        {
            OnPropertyChanged(nameof(ShowStreamPulse));
        }
    }

    /// <summary>
    /// Re-reads Quality/TargetFps/Scale off the freshly-replaced profile (RemEx-w6ipy). Marshalled
    /// through <see cref="ShellViewModel.ProfileReplacedDispatch"/>, same reason as
    /// ShellViewModel's own ProfileReplaced handler: ReloadAsync is not guaranteed to complete on
    /// the UI thread. Deliberately does not touch <see cref="IsStreaming"/> or the desktop service -
    /// a live stream must survive a savefile import untouched; only the next ApplySettingsAsync
    /// picks up the imported values.
    /// </summary>
    private void OnProfileReplaced() => _shell.ProfileReplacedDispatch(() =>
    {
        var profile = _shell.LayoutService.CurrentProfile;
        if (profile is null)
            return;

        Quality = profile.StreamQuality;
        TargetFps = profile.StreamFps;
        Scale = SnapScale(profile.StreamScale);
    });

    public void Dispose()
    {
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell.LayoutService.ProfileReplaced -= OnProfileReplaced;
        _desktopService.FrameReceived -= OnFrameReceived;
        _desktopService.MetaReceived -= OnMetaReceived;
        _desktopService.ErrorReceived -= OnErrorReceived;
        _desktopService.StreamDescriptorReceived -= OnStreamDescriptorReceived;
        _desktopService.CursorStateReceived -= OnCursorStateReceived;
        _desktopService.CursorShapeReceived -= OnCursorShapeReceived;
        _desktopService.Disconnected -= OnDisconnected;
        _desktopService.Dispose();
        CurrentFrame?.Dispose();
        foreach (var bitmap in _cursorShapeCache.Values)
        {
            bitmap.Dispose();
        }
    }

    public int RemoteCursorHostX => _remoteCursorHostX;

    public int RemoteCursorHostY => _remoteCursorHostY;

    public bool RemoteCursorHostVisible => _remoteCursorHostVisible;

    public bool HasRemoteCursorBitmap => RemoteCursorBitmap is not null;

    public long ActiveCursorShapeSerial => _activeCursorShapeSerial;

    public int CursorHotspotX => _cursorHotspotX;

    public int CursorHotspotY => _cursorHotspotY;

    public int CursorShapeWidth => _cursorShapeWidth;

    public int CursorShapeHeight => _cursorShapeHeight;

    private void ApplyDisplayCatalog(DesktopDisplayCatalog catalog, DesktopTargetOption? previousSelection)
    {
        AvailableDisplayTargets.Clear();

        if (catalog.SupportedCaptureModes.Contains(DesktopCaptureMode.VirtualDesktop))
        {
            var virtualBounds = catalog.Displays.Aggregate(default(Rect?),
                static (current, display) =>
                {
                    var bounds = new Rect(display.Left, display.Top, display.Width, display.Height);
                    return current is null ? bounds : current.Value.Union(bounds);
                });

            var width = (int)Math.Round(virtualBounds?.Width ?? 0);
            var height = (int)Math.Round(virtualBounds?.Height ?? 0);
            AvailableDisplayTargets.Add(new DesktopTargetOption(
                "All Displays",
                DesktopCaptureMode.VirtualDesktop,
                null,
                catalog.DisplayListVersion,
                false,
                width,
                height));
        }

        foreach (var display in catalog.Displays)
        {
            var label = display.IsPrimary
                ? $"{display.Name} - {display.Width}x{display.Height} (Primary)"
                : $"{display.Name} - {display.Width}x{display.Height}";
            AvailableDisplayTargets.Add(new DesktopTargetOption(
                label,
                DesktopCaptureMode.Monitor,
                display.DisplayId,
                catalog.DisplayListVersion,
                display.IsPrimary,
                display.Width,
                display.Height));
        }

        SelectedDisplayTarget =
            AvailableDisplayTargets.FirstOrDefault(option =>
                previousSelection is not null &&
                option.CaptureMode == previousSelection.CaptureMode &&
                string.Equals(option.DisplayId, previousSelection.DisplayId, StringComparison.Ordinal)) ??
            AvailableDisplayTargets.FirstOrDefault(option => option.CaptureMode == DesktopCaptureMode.Monitor && option.IsPrimary) ??
            AvailableDisplayTargets.FirstOrDefault();

        OnPropertyChanged(nameof(HasDisplayTargets));
    }

    private void ClearDisplayTargets()
    {
        AvailableDisplayTargets.Clear();
        SelectedDisplayTarget = null;
        OnPropertyChanged(nameof(HasDisplayTargets));
        StartStreamCommand.NotifyCanExecuteChanged();
    }

    private void ClearCurrentFrame()
    {
        var old = CurrentFrame;
        CurrentFrame = null;
        old?.Dispose();
    }

    private void ResetRemoteCursorOverlay()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _remoteCursorHostVisible = false;
            _activeCursorShapeSerial = 0;
            _cursorHotspotX = 0;
            _cursorHotspotY = 0;
            _cursorShapeWidth = 0;
            _cursorShapeHeight = 0;
            OnPropertyChanged(nameof(RemoteCursorHostVisible));
            OnPropertyChanged(nameof(ActiveCursorShapeSerial));
            OnPropertyChanged(nameof(CursorHotspotX));
            OnPropertyChanged(nameof(CursorHotspotY));
            OnPropertyChanged(nameof(CursorShapeWidth));
            OnPropertyChanged(nameof(CursorShapeHeight));
            RemoteCursorBitmap = null;
            IsRemoteCursorOverlayVisible = false;
        });
    }

    private static Bitmap CreateCursorBitmap(DesktopCursorShape shape)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(shape.Width, shape.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var locked = bitmap.Lock();
        Marshal.Copy(shape.ShapeBytes, 0, locked.Address, Math.Min(shape.ShapeBytes.Length, locked.RowBytes * shape.Height));
        return bitmap;
    }

    public sealed record DesktopTargetOption(
        string Label,
        DesktopCaptureMode CaptureMode,
        string? DisplayId,
        int DisplayListVersion,
        bool IsPrimary,
        int Width,
        int Height);
}
