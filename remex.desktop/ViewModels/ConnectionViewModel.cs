using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;
using Remex.Desktop.Services;
using Remex.Core;
using Remex.Core.Exceptions;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Network;
using Remex.Core.Services.Security;
using Remex.Core.Validation;
using Remex.Desktop.Services.Security;

namespace Remex.Desktop.ViewModels;

public partial class ConnectionViewModel : ObservableValidator, IDisposable
{
    private const int MaxLatencyPoints = 30;
    private const int MaxReconnectDelaySeconds = 30;
    private ClientWebSocket? _webSocket;
    // Serializes all outbound sends. A WebSocket permits only one outstanding SendAsync at a time, so a
    // file-transfer chunk loop sending concurrently with a browse/ping/launcher/layout send throws
    // "There is already one outstanding 'SendAsync'..." and can fault the socket mid-transfer.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _reconnectCts;
    private bool _userDisconnected;
    private bool _isPairedWithCurrentHost;
    private string? _cachedLocalIpv4;

    // Snapshot of pinned host SPKI hashes captured immediately before each WebSocket connect
    // attempt. The TLS validation callback reads this synchronously, eliminating the
    // .GetAwaiter().GetResult() deadlock risk inside the callback.
    private IReadOnlyDictionary<string, string>? _pinSnapshot;
    // Set true when the user has explicitly initiated pairing for the current connect attempt.
    // Allows trust-on-first-use only when the operator opted in; otherwise the cert callback
    // fails closed on any unrecognized SPKI hash.
    private bool _allowFirstTimeTrustForCurrentConnect;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendPingCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessageResourceType = typeof(Localization.Strings), ErrorMessageResourceName = nameof(Localization.Strings.Connection_PcAddressRequired))]
    [ValidWebSocketUri]
    private string _hostAddress = $"wss://localhost:{RemexConstants.DefaultPort}{RemexConstants.WebSocketPath}";

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance["Status_Disconnected"];

    [ObservableProperty]
    private string _latencyText = "—";

    [ObservableProperty]
    private bool _isAutoReconnecting;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _qrCodeImage;

    [ObservableProperty]
    private bool _showQrCode;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private HostCapabilities? _hostCapabilities;

    /// <summary>
    /// Active pairing PIN published by the in-process host. Null when no pairing is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePairingPin))]
    private string? _activePairingPin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePairingPin))]
    [NotifyPropertyChangedFor(nameof(PairingPinExpiresInText))]
    private DateTimeOffset? _activePairingExpiresAt;

    public bool HasActivePairingPin => !string.IsNullOrEmpty(ActivePairingPin);

    /// <summary>
    /// Whether the pairing-PIN panel should be visible. Auto-set to true when a PIN
    /// arrives so the user sees it without having to click; the user can dismiss it
    /// and re-open via <see cref="ShowPairingPinCommand"/>.
    /// </summary>
    [ObservableProperty]
    private bool _showPairingPin;

    /// <summary>
    /// Localized "Expires in Ns" countdown string driven by <see cref="ActivePairingExpiresAt"/>.
    /// Refreshed once per second by <see cref="_pairingExpiryTimer"/>.
    /// </summary>
    public string PairingPinExpiresInText
    {
        get
        {
            if (ActivePairingExpiresAt is null) return string.Empty;
            var seconds = (int)Math.Max(0, (ActivePairingExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
            return string.Format(LocalizationService.Instance["Settings_PairingPinExpiresIn"], seconds);
        }
    }

    private DispatcherTimer? _pairingExpiryTimer;
    private DispatcherTimer? _standalonePairingPinPollingTimer;

    /// <summary>
    /// LAN address phones on the same network should use to reach this PC's host.
    /// Computed lazily from the loopback host address.
    /// </summary>
    public string? LanHostAddress
    {
        get
        {
            try
            {
                var uri = new Uri(HostAddress);
                if (uri.Host is "localhost" or "127.0.0.1" or "::1")
                {
                    var ip = _cachedLocalIpv4 ??= GetLocalIpv4Address();
                    if (ip is null) return null;
                    var port = uri.Port > 0 ? uri.Port : RemexConstants.DefaultPort;
                    return $"{uri.Scheme}://{ip}:{port}{uri.AbsolutePath}";
                }
                return HostAddress;
            }
            catch { return null; }
        }
    }

    partial void OnHostAddressChanged(string value) => OnPropertyChanged(nameof(LanHostAddress));

    private IPairingService? _pairingService;
    private IPairingPinQueryService? _standalonePairingPinQueryService;

    /// <summary>
    /// Subscribes to pairing-pin events on the in-process host's PairingService so the
    /// desktop UI can show the user the PIN their phone is asking for.
    /// </summary>
    public void AttachEmbeddedPairingService(IPairingService service)
    {
        Guard.NotNull(service);
        StopStandalonePairingPinPolling();
        _pairingService = service;
        service.PinDisplayed += (pin, expires) =>
            Dispatcher.UIThread.Post(() =>
            {
                ActivePairingPin = pin;
                ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires);
                ShowPairingPin = true;
                StartPairingExpiryTimer();
            });
        service.PinCleared += () =>
            Dispatcher.UIThread.Post(() =>
            {
                ActivePairingPin = null;
                ActivePairingExpiresAt = null;
                ShowPairingPin = false;
                StopPairingExpiryTimer();
            });

        if (service.TryGetActivePinInfo(out var activePin, out var activeExpiresAt))
        {
            ActivePairingPin = activePin;
            ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activeExpiresAt);
            ShowPairingPin = true;
            StartPairingExpiryTimer();
        }
        else
        {
            ActivePairingPin = null;
            ActivePairingExpiresAt = null;
            ShowPairingPin = false;
            StopPairingExpiryTimer();
        }
    }

    public void AttachStandalonePairingPinQueryService(IPairingPinQueryService service)
    {
        Guard.NotNull(service);
        _standalonePairingPinQueryService = service;
    }

    public void StartStandalonePairingPinPolling()
    {
        if (_standalonePairingPinQueryService is null || _standalonePairingPinPollingTimer is not null)
        {
            return;
        }

        _standalonePairingPinPollingTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            async (_, _) => await RefreshStandalonePairingPinAsync());
        _standalonePairingPinPollingTimer.Start();
    }

    public async Task RefreshStandalonePairingPinAsync()
    {
        if (_standalonePairingPinQueryService is null)
        {
            return;
        }

        try
        {
            var activePin = await _standalonePairingPinQueryService.GetActivePairingPinAsync();
            var applySnapshot = () =>
            {
                if (activePin is not null)
                {
                    if (ActivePairingPin != activePin.Pin || ActivePairingExpiresAt != DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs))
                    {
                        ActivePairingPin = activePin.Pin;
                        ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs);
                        ShowPairingPin = true;
                        StartPairingExpiryTimer();
                    }
                    return;
                }

                if (HasActivePairingPin && (_pairingService is null || !_pairingService.IsPairingActive))
                {
                    ActivePairingPin = null;
                    ActivePairingExpiresAt = null;
                    ShowPairingPin = false;
                    StopPairingExpiryTimer();
                }
            };

            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            {
                applySnapshot();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(applySnapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Standalone pairing PIN refresh failed.");
        }
    }

    private void StartPairingExpiryTimer()
    {
        if (_pairingExpiryTimer is not null) return;
        _pairingExpiryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            OnPropertyChanged(nameof(PairingPinExpiresInText));
            if (ActivePairingExpiresAt is { } expires && DateTimeOffset.UtcNow >= expires)
            {
                ActivePairingPin = null;
                ActivePairingExpiresAt = null;
                ShowPairingPin = false;
                StopPairingExpiryTimer();
            }
        });
        _pairingExpiryTimer.Start();
    }

    private void StopPairingExpiryTimer()
    {
        _pairingExpiryTimer?.Stop();
        _pairingExpiryTimer = null;
    }

    private void StopStandalonePairingPinPolling()
    {
        _standalonePairingPinPollingTimer?.Stop();
        _standalonePairingPinPollingTimer = null;
    }

    [RelayCommand]
    private void ShowPairingPinPanel() => ShowPairingPin = HasActivePairingPin;

    [RelayCommand]
    public async Task RevealPairingPinAsync()
    {
        // 1. If embedded host is active, start pairing directly on it
        if (_pairingService is not null)
        {
            try
            {
                var state = await _pairingService.GetOrStartPairingAsync(default);
                ActivePairingPin = state.Pin;
                ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(state.ExpiresAtUnixMs);
                ShowPairingPin = true;
                StartPairingExpiryTimer();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start embedded pairing session.");
                StatusText = LocalizationService.Instance["Status_FailedGeneratePin"];
            }
        }
        // 2. If standalone host query service is active, request it over IPC
        else if (_standalonePairingPinQueryService is not null)
        {
            try
            {
                var activePin = await _standalonePairingPinQueryService.GeneratePairingPinAsync();
                if (activePin is not null)
                {
                    ActivePairingPin = activePin.Pin;
                    ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs);
                    ShowPairingPin = true;
                    StartPairingExpiryTimer();
                }
                else
                {
                    StatusText = LocalizationService.Instance["Status_FailedGeneratePinHost"];
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start standalone pairing session.");
                StatusText = LocalizationService.Instance["Status_FailedGeneratePinHost"];
            }
        }
        else
        {
            StatusText = LocalizationService.Instance["Status_PairingServiceUnavailable"];
        }
    }

    [RelayCommand]
    private void ClosePairingPin() => ShowPairingPin = false;

    /// <summary>Rolling window of latency samples (ms) for charting.</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    /// <summary>Hosts discovered via mDNS; populated after <see cref="DiscoverHostsCommand"/> completes.</summary>
    public ObservableCollection<string> DiscoveredHosts { get; } = new();

    /// <summary>Recently used connection addresses (most-recent first, max 10).</summary>
    public ObservableCollection<Remex.Core.Models.ConnectionProfile> ConnectionHistory { get; } = new();

    private readonly IMdnsDiscoveryService? _discoveryService;
    private readonly Remex.Desktop.Services.DashboardLayoutService? _layoutService;
    private readonly ILogger<ConnectionViewModel> _logger;

    public ConnectionViewModel() : this(null, null, null) { }

    public ConnectionViewModel(
        IMdnsDiscoveryService? discoveryService,
        Remex.Desktop.Services.DashboardLayoutService? layoutService,
        ILogger<ConnectionViewModel>? logger = null)
    {
        _discoveryService = discoveryService;
        _layoutService = layoutService;
        _logger = logger ?? NullLogger<ConnectionViewModel>.Instance;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Refresh idle status when language changes
        if (!IsConnecting && !IsAutoReconnecting)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = IsConnected
                    ? LocalizationService.Instance["Status_Connected"]
                    : LocalizationService.Instance["Status_Disconnected"];
            });
        }
    }

    [RelayCommand]
    private async Task DiscoverHostsAsync()
    {
        if (_discoveryService == null)
        {
            StatusText = LocalizationService.Instance["Status_DiscoveryUnavailable"];
            return;
        }

        StatusText = LocalizationService.Instance["Status_SearchingHosts"];
        var foundHosts = await _discoveryService.DiscoverHostsAsync(TimeSpan.FromSeconds(5));
        var defaultAddress = $"wss://localhost:{RemexConstants.DefaultPort}{RemexConstants.WebSocketPath}";

        Dispatcher.UIThread.Post(() =>
        {
            DiscoveredHosts.Clear();
            foreach (var host in foundHosts)
                DiscoveredHosts.Add(host);

            if (foundHosts.Any())
            {
                var firstHost = foundHosts.First();
                if (string.IsNullOrWhiteSpace(HostAddress) || HostAddress == defaultAddress)
                {
                    HostAddress = firstHost;
                    StatusText = string.Format(LocalizationService.Instance["Status_FoundHostFormat"], firstHost);
                }
                else
                {
                    StatusText = string.Format(LocalizationService.Instance["Status_FoundMultipleHostsFormat"], foundHosts.Count);
                }
            }
            else
            {
                StatusText = LocalizationService.Instance["Status_NoHostsFound"];
            }
        });
    }

    public event Action<System.Collections.Generic.List<Remex.Core.Models.AppEntry>>? LauncherEntriesReceived;
    public event Action<TelemetryPayload>? TelemetryReceived;
    public event Action<Remex.Core.Models.DashboardProfile>? LayoutProfileReceived;
    public event Action<System.Collections.Generic.List<Remex.Core.Models.ProcessInfo>>? ProcessListReceived;
    public event Action<Remex.Core.Messages.RemexMessage>? FileTransferMessageReceived;

    [ObservableProperty]
    private ObservableCollection<Remex.Core.Models.ProcessInfo> _processes = new();

    public async Task RequestProcessListAsync()
    {
        var msg = new RemexMessage { Type = MessageTypes.ProcessListRequest };
        await SendGuardedAsync(msg);
    }

    [RelayCommand]
    public async Task LockAsync() => await SendCommandAsync("Lock");

    [RelayCommand]
    public async Task SleepAsync() => await SendCommandAsync("Sleep");

    [RelayCommand]
    public async Task HibernateAsync() => await SendCommandAsync("Hibernate");

    [RelayCommand]
    public async Task SignOutAsync() => await SendCommandAsync("SignOut");

    [RelayCommand]
    public async Task ShutdownAsync() => await SendCommandAsync("Shutdown");

    [RelayCommand]
    public async Task ForceShutdownAsync() => await SendCommandAsync("ForceShutdown");

    [RelayCommand]
    public async Task RestartAsync() => await SendCommandAsync("Restart");

    [RelayCommand]
    public async Task RestartToUefiAsync() => await SendCommandAsync("RestartToUefi");

    [RelayCommand]
    public async Task WakeOnLanAsync()
    {
        // This needs parameters usually, but for a simple widget toggle it might use defaults
        await SendCommandAsync("WakeOnLan");
    }

    public async Task<Remex.Core.Models.IPC.CommandResponse> KillProcessWithResponseAsync(int processId, bool elevated = false)
    {
        if (_webSocket?.State != WebSocketState.Open) return new Remex.Core.Models.IPC.CommandResponse(false, "Not connected", null);
        var msg = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = elevated ? "KillProcessElevated" : "KillProcess",
            CommandParameters = new System.Collections.Generic.Dictionary<string, string> { { "ProcessId", processId.ToString() } }
        };
        try
        {
            var response = await SendCommandAndWaitAsync(msg);
            return new Remex.Core.Models.IPC.CommandResponse(response.CommandSuccess ?? false, response.CommandMessage ?? "", null);
        }
        catch (OperationCanceledException)
        {
            return new Remex.Core.Models.IPC.CommandResponse(false, "Timeout waiting for server response", null);
        }
    }

    [ObservableProperty]
    private double _averageLatency;

    [ObservableProperty]
    private double _maxLatency;

    public bool SupportsRemoteDesktop => HostCapabilities?.SupportsRemoteDesktop ?? true;

    /// <summary>
    /// The localized label for how the host is running. Note that the wire value "service" is
    /// legacy naming for a non-interactive/Session-0 Windows process, NOT a service install —
    /// there is none (RemEx-9z0f). Exposed so callers show this instead of the raw wire token.
    /// </summary>
    public string HostRuntimeLabel => HostCapabilities?.RuntimeMode switch
    {
        "interactive" => LocalizationService.Instance["Status_InteractiveHost"],
        "service" => LocalizationService.Instance["Status_ServiceHost"],
        "headless" => LocalizationService.Instance["Status_HeadlessHost"],
        _ => LocalizationService.Instance["Status_Host"]
    };

    public string HostRuntimeSummary
    {
        get
        {
            if (HostCapabilities is null)
            {
                return IsConnected ? LocalizationService.Instance["Status_ConnectedToHost"] : LocalizationService.Instance["Status_HostNotConnected"];
            }

            var runtimeLabel = HostRuntimeLabel;


            return $"{runtimeLabel} on {HostCapabilities.Platform}";
        }
    }

    public string RemoteDesktopAvailabilitySummary =>
        SupportsRemoteDesktop
            ? LocalizationService.Instance["Status_RemoteDesktopAvailable"]
            : HostCapabilities?.RemoteDesktopUnavailableReason
                ?? LocalizationService.Instance["Status_RemoteDesktopUnavailable"];

    private TelemetryPayload? _telemetry;
    public TelemetryPayload? Telemetry
    {
        get => _telemetry;
        set
        {
            _telemetry = value;
            OnPropertyChanged(nameof(Telemetry));
        }
    }

    partial void OnHostCapabilitiesChanged(HostCapabilities? value)
    {
        OnPropertyChanged(nameof(SupportsRemoteDesktop));
        OnPropertyChanged(nameof(HostRuntimeSummary));
        OnPropertyChanged(nameof(RemoteDesktopAvailabilitySummary));
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    private void SaveConnectionToHistory()
    {
        const int MaxHistoryEntries = 10;
        var address = HostAddress;

        var existing = ConnectionHistory.FirstOrDefault(h => h.HostAddress == address);
        if (existing != null)
            ConnectionHistory.Remove(existing);

        ConnectionHistory.Insert(0, new Remex.Core.Models.ConnectionProfile
        {
            Name = address,
            HostAddress = address,
            LastConnected = DateTime.Now
        });

        while (ConnectionHistory.Count > MaxHistoryEntries)
            ConnectionHistory.RemoveAt(ConnectionHistory.Count - 1);

        if (_layoutService != null)
        {
            var profile = _layoutService.CurrentProfile ?? new Remex.Core.Models.DashboardProfile();
            _layoutService.RequestSave(profile with { ConnectionHistory = ConnectionHistory.ToList() });
        }
    }
    private bool CanDisconnect() => IsConnected || IsConnecting;

    public System.Net.WebSockets.WebSocket? GetWebSocket() => _webSocket;
    public async Task<(bool Success, string Message)> SendCommandAsync(string action, System.Collections.Generic.Dictionary<string, string>? parameters = null)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return (false, LocalizationService.Instance["Status_NotConnected"]);

        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.Command,
                CommandAction = action,
                CommandParameters = parameters,
                Timestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
            };
            var response = await SendCommandAndWaitAsync(msg);
            return (response.CommandSuccess ?? false, response.CommandMessage ?? LocalizationService.Instance["Status_NoMessage"]);
        }
        catch (OperationCanceledException)
        {
            return (false, LocalizationService.Instance["Status_CommandTimedOut"]);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error sending command {Action}", action);
            return (false, $"Network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation sending command {Action}", action);
            return (false, $"Invalid operation: {ex.Message}");
        }
        // Let unexpected exceptions propagate
    }

    private bool CanSendPing() => IsConnected;

    // ---------------------------------------------------------------------------
    // Correlated command/response infrastructure
    // ---------------------------------------------------------------------------

    /// <summary>
    /// How long to wait for a command response before giving up.
    /// </summary>
    private const int CommandTimeoutSeconds = 10;

    /// <summary>
    /// Pending command awaiters keyed by correlation ID.
    /// Replaces the former single <c>_pendingCommandResponse</c> field so concurrent
    /// callers no longer overwrite each other.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _pendingCommands = new();

    /// <summary>
    /// Stamps a correlation ID onto <paramref name="msg"/>, registers a TCS, sends the
    /// message, and awaits the matching response with a <see cref="CommandTimeoutSeconds"/>
    /// timeout.  Cleans up the dictionary entry regardless of outcome.
    /// </summary>
    private async Task<RemexMessage> SendCommandAndWaitAsync(RemexMessage msg, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[correlationId] = tcs;
        try
        {
            await SendGuardedAsync(msg with { CorrelationId = correlationId }, ct);
            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(CommandTimeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                throw new OperationCanceledException("Command timed out.");
            }
        }
        finally
        {
            _pendingCommands.TryRemove(correlationId, out _);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // Validate inputs before attempting connection
        ValidateAllProperties();
        if (HasErrors)
        {
            var errors = GetErrors(nameof(HostAddress))
                .Cast<ValidationResult>()
                .Select(e => e.ErrorMessage)
                .FirstOrDefault();
            StatusText = errors ?? LocalizationService.Instance["Status_InvalidSettings"];
            return;
        }

        _userDisconnected = false;
        IsConnecting = true;
        HostCapabilities = null;
        StopReconnecting();

        // Define CTS outside try to be accessible in catch
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _receiveCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts.Token, timeoutCts.Token);

        try
        {
            StatusText = LocalizationService.Instance["Status_Connecting"];
            var uri = await PrepareTlsValidationForConnectAsync(allowTrustOnFirstUseForEmptyStore: true);
            _webSocket = CreateConfiguredWebSocket();

            await _webSocket.ConnectAsync(uri, linkedCts.Token);

            // Loopback connections target the in-process embedded host on the same machine.
            // Pairing exists to bootstrap trust with a *remote* host, so it adds no security
            // here — and would prompt the user for a PIN their own desktop generated.
            if (IsLoopbackHost(uri))
                _isPairedWithCurrentHost = true;

            if (!_isPairedWithCurrentHost)
            {
                StatusText = LocalizationService.Instance["Status_Pairing"];
                var certStore = App.Services.GetRequiredService<Remex.Desktop.Services.Security.PinnedCertStore>();
                var pairingClient = new Remex.Core.Native.PairingClient(_webSocket, null);

                var response = await pairingClient.StartPairingAsync(Environment.MachineName, "2.0.0", linkedCts.Token);
                if (response == null)
                {
                    StatusText = LocalizationService.Instance["Status_PairingFailed"];
                    Cleanup();
                    return;
                }

                var pin = await PromptForPinAsync();
                if (string.IsNullOrEmpty(pin))
                {
                    StatusText = LocalizationService.Instance["Status_PairingCancelled"];
                    Cleanup();
                    return;
                }

                var success = await pairingClient.CompletePairingAsync(pin, response, linkedCts.Token);
                if (!success)
                {
                    StatusText = LocalizationService.Instance["Status_PairingFailed"];
                    Cleanup();
                    return;
                }

                // Pairing successful, save the SPKI hash!
                await certStore.SetPinAsync(response.HostId, response.CertificateSpkiHashBase64);
                _isPairedWithCurrentHost = true;
            }

            IsConnected = true;
            IsConnecting = false;
            StatusText = LocalizationService.Instance["Status_Connected"];
            LatencyText = "—";

            SaveConnectionToHistory();

            // Start background receive loop.
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = linkedCts.Token.IsCancellationRequested && !timeoutCts.Token.IsCancellationRequested
                ? LocalizationService.Instance["Status_ConnectionCancelled"]
                : LocalizationService.Instance["Status_ConnectionTimedOut"];
            Cleanup();
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket connection failed to {HostAddress}", HostAddress);
            StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], "Connection failed");
            Cleanup();
        }
        catch (UriFormatException ex)
        {
            _logger.LogError(ex, "Invalid WebSocket URI: {HostAddress}", HostAddress);
            StatusText = LocalizationService.Instance["Status_InvalidHostAddress"] ?? "Invalid host address format";
            Cleanup();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid WebSocket state during connection");
            StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], ex.Message);
            Cleanup();
        }
        // Let unexpected exceptions (OutOfMemoryException, etc.) propagate to app-level handler
        finally
        {
            IsConnecting = false;
            // Always close the trust window on exit, even if ConnectAsync threw.
            _allowFirstTimeTrustForCurrentConnect = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        _userDisconnected = true;
        StopReconnecting();

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "User disconnected",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Best-effort close - WebSocket already in bad state
        }
        catch (OperationCanceledException)
        {
            // Best-effort close - operation was cancelled
        }
        catch (ObjectDisposedException)
        {
            // Best-effort close - WebSocket already disposed
        }

        Cleanup();
        StatusText = IsConnecting ? LocalizationService.Instance["Status_ConnectionCancelled"] : LocalizationService.Instance["Status_Disconnected"];
        LatencyText = "—";
    }

    [RelayCommand(CanExecute = nameof(CanSendPing))]
    private async Task SendPingAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            var ping = new RemexMessage
            {
                Type = MessageTypes.Ping,
                Timestamp = Stopwatch.GetTimestamp(),
            };
            await SendGuardedAsync(ping);
            StatusText = LocalizationService.Instance["Status_PingSent"];
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send ping message");
            StatusText = string.Format(LocalizationService.Instance["Status_SendErrorFormat"], "Network error");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation sending ping");
            StatusText = string.Format(LocalizationService.Instance["Status_SendErrorFormat"], ex.Message);
        }
    }

    public Task SendAsync(RemexMessage message) => SendGuardedAsync(message);

    /// <summary>
    /// The single outbound-send choke point. Acquires <see cref="_sendLock"/> so only one
    /// WebSocket.SendAsync is ever in flight — every caller (file-transfer chunks, browse, ping,
    /// commands, launcher/layout) routes through here instead of touching the socket directly.
    /// </summary>
    private async Task SendGuardedAsync(RemexMessage message, CancellationToken ct = default)
    {
        var ws = _webSocket;
        if (ws?.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            await MessageSerializer.SendAsync(ws, message, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendLayoutUpdateAsync(Remex.Core.Models.DashboardProfile profile)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.LayoutUpdate,
                DashboardProfile = profile,
            };
            await SendGuardedAsync(msg);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send layout update to host");
            Debug.WriteLine($"Failed to send layout update: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation sending layout update");
            Debug.WriteLine($"Failed to send layout update: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await MessageSerializer.ReceiveAsync(_webSocket, ct);

                if (message is null)
                    break;

                switch (message.Type)
                {
                    case MessageTypes.Pong when message.Timestamp.HasValue:
                        var elapsed = Stopwatch.GetElapsedTime(message.Timestamp.Value);
                        var ms = elapsed.TotalMilliseconds;
                        Dispatcher.UIThread.Post(() =>
                        {
                            LatencyText = $"{ms:F1} ms";
                            StatusText = string.Format(LocalizationService.Instance["Status_PongFormat"], ms);
                            PushLatency(ms);
                        });
                        break;

                    case MessageTypes.Pong:
                        Dispatcher.UIThread.Post(() =>
                        {
                            LatencyText = LocalizationService.Instance["Status_PongNoTimestamp"];
                            StatusText = LocalizationService.Instance["Status_Pong"];
                        });
                        break;

                    case MessageTypes.Telemetry when message.Telemetry is not null:
                        Dispatcher.UIThread.Post(() =>
                        {
                            Telemetry = message.Telemetry;
                            TelemetryReceived?.Invoke(message.Telemetry);
                        });
                        break;

                    case MessageTypes.CommandResponse:
                        if (message.CorrelationId is string cid
                            && _pendingCommands.TryGetValue(cid, out var matchedTcs))
                        {
                            // Normal path: correlation ID present and matches a pending request
                            matchedTcs.TrySetResult(message);
                        }
                        else if (message.CorrelationId is null && !_pendingCommands.IsEmpty)
                        {
                            // Fallback for hosts that do not echo correlation IDs back in their
                            // CommandResponse messages (i.e. unpatched / older host versions).
                            // LIMITATION: With multiple concurrent in-flight commands this path
                            // delivers the response to at most one caller (the first whose TCS
                            // accepts it); all remaining concurrent callers will eventually time
                            // out.  Upgrade the host so it echoes CorrelationId to avoid this.
                            if (_pendingCommands.Count > 1)
                                Debug.WriteLine(
                                    "[ConnectionViewModel] WARNING: Fallback correlation path taken with " +
                                    $"{_pendingCommands.Count} concurrent in-flight commands. " +
                                    "Only one caller will receive this response; the rest will time out. " +
                                    "Upgrade the host to a version that echoes CorrelationId.");

                            foreach (var entry in _pendingCommands)
                            {
                                if (entry.Value.TrySetResult(message))
                                    break;
                            }
                        }
                        break;

                    case MessageTypes.LauncherSync when message.LauncherEntries is not null:
                        Dispatcher.UIThread.Post(() => LauncherEntriesReceived?.Invoke(message.LauncherEntries));
                        break;

                    case MessageTypes.ProcessListSync when message.ProcessList is not null:
                        Dispatcher.UIThread.Post(() =>
                        {
                            Processes = new ObservableCollection<Remex.Core.Models.ProcessInfo>(message.ProcessList);
                            ProcessListReceived?.Invoke(message.ProcessList);
                        });
                        break;

                    case MessageTypes.HostInfo when message.HostCapabilities is not null:
                        Dispatcher.UIThread.Post(() => HostCapabilities = message.HostCapabilities);
                        break;

                    case MessageTypes.LayoutSync when message.DashboardProfile is not null:
                        Dispatcher.UIThread.Post(() => LayoutProfileReceived?.Invoke(message.DashboardProfile));
                        break;

                    case MessageTypes.FileBrowseResponse:
                    case MessageTypes.FileRootsResponse:
                    case MessageTypes.FileTransferChunk:
                    case MessageTypes.FileTransferEnd:
                    case MessageTypes.FileTransferProgress:
                    case MessageTypes.FileManageResponse:
                    case MessageTypes.FileHashResponse:
                    case MessageTypes.FileRootManageResponse:
                    // ── 2.1 File Sharing Overhaul (protocolVersion 3) responses — additive; older
                    //    hosts never send these, so v2 peers are unaffected. ──
                    case MessageTypes.FileVolumesResponse:
                    case MessageTypes.FileSearchResponse:
                    case MessageTypes.FileMetadataResponse:
                    case MessageTypes.FileThumbnailResponse:
                        FileTransferMessageReceived?.Invoke(message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect
            _logger.LogDebug("Receive loop cancelled during shutdown");
        }
        catch (WebSocketException ex)
        {
            // Connection lost
            _logger.LogWarning(ex, "WebSocket connection lost in receive loop");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize message from host");
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], LocalizationService.Instance["Status_InvalidMessageFormat"]);
            });
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error in receive loop");
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = string.Format(LocalizationService.Instance["Status_ReceiveErrorFormat"], "Connection error");
            });
        }
        // Let unexpected exceptions propagate to app-level handler

        // If we exited the loop because the server closed, update UI state.
        if (IsConnected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Cleanup();
                StatusText = LocalizationService.Instance["Status_ServerClosed"];
                LatencyText = "—";
            });

            // Auto-reconnect unless the user explicitly disconnected.
            if (!_userDisconnected)
            {
                _ = ReconnectLoopAsync();
            }
        }
    }

    /// <summary>
    /// Attempts to connect automatically on app startup.
    /// Retries with exponential backoff until connected or cancelled.
    /// </summary>
    public async Task AutoConnectAsync()
    {
        _userDisconnected = false;
        await ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        StopReconnecting();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        int delay = 2;

        Dispatcher.UIThread.Post(() => IsAutoReconnecting = true);

        try
        {
            while (!ct.IsCancellationRequested && !IsConnected)
            {
                Dispatcher.UIThread.Post(() => StatusText = string.Format(LocalizationService.Instance["Status_ReconnectingFormat"], delay));
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                if (ct.IsCancellationRequested) break;

                ClientWebSocket? ws = null;
                try
                {
                    Dispatcher.UIThread.Post(() => StatusText = LocalizationService.Instance["Status_Connecting"]);
                    var uri = await PrepareTlsValidationForConnectAsync(allowTrustOnFirstUseForEmptyStore: false);
                    ws = CreateConfiguredWebSocket();

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(uri, connectCts.Token);

                    if (IsLoopbackHost(uri))
                        _isPairedWithCurrentHost = true;

                    // Success — adopt the new socket.
                    _webSocket = ws;
                    _receiveCts = new CancellationTokenSource();
                    HostCapabilities = null;

                    Dispatcher.UIThread.Post(() =>
                    {
                        IsConnected = true;
                        IsAutoReconnecting = false;
                        StatusText = LocalizationService.Instance["Status_Connected"];
                        LatencyText = "—";
                    });

                    _ = ReceiveLoopAsync(_receiveCts.Token);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    ws?.Dispose();
                    // Exponential backoff: 2, 4, 8, 16, 30, 30, ...
                    delay = Math.Min(delay * 2, MaxReconnectDelaySeconds);
                }
                finally
                {
                    _allowFirstTimeTrustForCurrentConnect = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled.
        }

        Dispatcher.UIThread.Post(() => IsAutoReconnecting = false);
    }

    private void StopReconnecting()
    {
        CancelAndDispose(ref _reconnectCts);
        IsAutoReconnecting = false;
    }

    private void PushLatency(double ms)
    {
        if (LatencyHistory.Count >= MaxLatencyPoints)
            LatencyHistory.RemoveAt(0);

        LatencyHistory.Add(ms);
        AverageLatency = LatencyHistory.Average();
        MaxLatency = LatencyHistory.Max();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellationTokenSource)
    {
        var current = Interlocked.Exchange(ref cancellationTokenSource, null);
        if (current is null)
            return;

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another teardown path already disposed this source.
        }

        current.Dispose();
    }

    private static void DisposeWebSocket(ref ClientWebSocket? webSocket)
    {
        var current = Interlocked.Exchange(ref webSocket, null);
        if (current is null)
            return;

        try
        {
            current.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Another teardown path already disposed this socket.
        }
    }

    private void Cleanup()
    {
        CancelAndDispose(ref _receiveCts);
        DisposeWebSocket(ref _webSocket);

        // Cancel all in-flight command awaiters so callers don't hang after disconnect
        foreach (var (_, pendingTcs) in _pendingCommands)
            pendingTcs.TrySetCanceled();
        _pendingCommands.Clear();

        IsConnected = false;
        HostCapabilities = null;
    }

    [RelayCommand]
    private async Task GenerateQrCodeAsync()
    {
        try
        {
            var uri = new Uri(HostAddress);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : RemexConstants.DefaultPort;

            // If the configured host is loopback, substitute the machine's LAN IPv4
            // address so the QR code is scannable from a phone on the same network.
            if (host == "localhost" || host == "127.0.0.1" || host == "::1")
            {
                var lanIp = GetLocalIpv4Address();
                if (lanIp is not null)
                    host = lanIp;
            }

            var certService = App.Services.GetService<ICertificateService>() // optional service
                           ?? App.EmbeddedHostServices?.GetService<ICertificateService>(); // optional service
            var spkiHash = certService?.GetSpkiSha256Base64() ?? "";

            // Generate a fresh pairing PIN to embed in the QR code so the mobile client
            // can automatically pair without requiring the user to type any PIN manually.
            string? pairingPin = null;
            if (_pairingService is not null)
            {
                try
                {
                    var state = await _pairingService.GetOrStartPairingAsync(default);
                    pairingPin = state.Pin;
                    ActivePairingPin = state.Pin;
                    ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(state.ExpiresAtUnixMs);
                    StartPairingExpiryTimer();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start embedded pairing session for QR code.");
                }
            }
            else if (_standalonePairingPinQueryService is not null)
            {
                try
                {
                    var activePin = await _standalonePairingPinQueryService.GeneratePairingPinAsync();
                    if (activePin is not null)
                    {
                        pairingPin = activePin.Pin;
                        ActivePairingPin = activePin.Pin;
                        ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(activePin.ExpiresAtUnixMs);
                        StartPairingExpiryTimer();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start standalone pairing session for QR code.");
                }
            }

            var payload = JsonSerializer.Serialize(new
            {
                host,
                port,
                hostId = Environment.MachineName,
                spkiHashBase64 = spkiHash,
                pin = pairingPin,
            });

            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new PngByteQRCode(qrCodeData);
            var pngBytes = qrCode.GetGraphic(10);

            using var ms = new MemoryStream(pngBytes);
            var oldBitmap = QrCodeImage;
            QrCodeImage = new Avalonia.Media.Imaging.Bitmap(ms);
            oldBitmap?.Dispose();
            ShowQrCode = true;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to serialize QR code payload");
            StatusText = string.Format(LocalizationService.Instance["Status_ErrorFormat"], LocalizationService.Instance["Status_InvalidQrData"]);
            ShowQrCode = false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Invalid argument generating QR code");
            StatusText = string.Format(LocalizationService.Instance["Status_QrCodeFailed"], "Invalid data");
            ShowQrCode = false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to generate QR code image");
            Debug.WriteLine($"Failed to generate QR code: {ex}");
            StatusText = string.Format(LocalizationService.Instance["Status_QrCodeFailed"], ex.Message);
            ShowQrCode = false;
        }
    }

    [RelayCommand]
    private void CloseQrCode()
    {
        ShowQrCode = false;
        var old = QrCodeImage;
        QrCodeImage = null;
        old?.Dispose();
    }

    /// <summary>
    /// Returns the machine's preferred outbound LAN IPv4 address by connecting a
    /// UDP socket (no data sent) so the OS selects the correct local interface.
    /// </summary>
    private static string? GetLocalIpv4Address()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLoopbackHost(Uri uri) =>
        uri.Host is "localhost" or "127.0.0.1" or "::1";

    /// <summary>
    /// TLS server-certificate validation callback. Enforces SPKI pinning against the snapshot
    /// captured by <see cref="LoadPinSnapshotAsync"/> immediately before each socket connect.
    ///
    /// Security model:
    ///   - If the snapshot contains the incoming cert's SPKI hash, accept (matched pin).
    ///   - If the snapshot is non-empty and the hash does not match, reject (MITM defence).
    ///   - If the snapshot is empty AND the operator has opted into first-time trust for this
    ///     connect attempt, accept; the downstream PIN-based pairing handshake protects against
    ///     MITM in this trust-on-first-use window.
    ///   - Any other state (no snapshot loaded, opt-in not granted) → reject.
    ///
    /// The callback is fully synchronous: no <c>.GetAwaiter().GetResult()</c>, no DI lookup,
    /// no async I/O. Pins must be loaded into <see cref="_pinSnapshot"/> before the connect call.
    /// </summary>
    private bool AcceptSelfSignedCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null) return false;

        using var cert2 = new X509Certificate2(certificate);
        var spki = cert2.PublicKey.ExportSubjectPublicKeyInfo();
        var hashBytes = System.Security.Cryptography.SHA256.HashData(spki);
        var hashBase64 = Convert.ToBase64String(hashBytes);

        var pins = _pinSnapshot;
        if (pins is null)
        {
            // ConnectAsync must populate the snapshot before invoking ConnectAsync on the socket.
            // Hitting this branch is a programming error; fail closed.
            _logger.LogError(
                "TLS validation callback invoked without a pin snapshot. Rejecting cert {Hash}.",
                hashBase64);
            _isPairedWithCurrentHost = false;
            return false;
        }

        if (pins.Count > 0)
        {
            if (pins.Values.Contains(hashBase64))
            {
                _isPairedWithCurrentHost = true;
                return true;
            }

            _logger.LogError(
                "Rejecting host certificate: SPKI {Hash} does not match any pinned host. " +
                "If the host certificate has legitimately rotated, the operator must re-pair.",
                hashBase64);
            _isPairedWithCurrentHost = false;
            return false;
        }

        // Empty pin store. Trust-on-first-use is permitted only if the operator explicitly opted
        // in for this connect attempt. The PIN-based pairing handshake that follows provides
        // MITM protection in this window.
        if (_allowFirstTimeTrustForCurrentConnect)
        {
            _isPairedWithCurrentHost = false;
            _logger.LogInformation(
                "First-time pairing: accepting cert SPKI {Hash} for pairing handshake.",
                hashBase64);
            return true;
        }

        _logger.LogError(
            "Rejecting host certificate: SPKI {Hash} not pinned and first-time trust not granted.",
            hashBase64);
        _isPairedWithCurrentHost = false;
        return false;
    }

    /// <summary>
    /// Loads the current pinned-host snapshot synchronously usable by the TLS callback. Must be
    /// called before each <c>ClientWebSocket.ConnectAsync</c> invocation.
    /// </summary>
    private async Task LoadPinSnapshotAsync()
    {
        var store = App.Services.GetService<Remex.Desktop.Services.Security.PinnedCertStore>(); // optional service
        if (store is null)
        {
            // No pin store configured (e.g. a misconfigured DI container). Empty snapshot will
            // cause the validation callback to fail closed unless first-time-trust is granted.
            _pinSnapshot = new Dictionary<string, string>();
            return;
        }

        _pinSnapshot = await store.GetAllPinsAsync().ConfigureAwait(false);
    }

    private async Task<Uri> PrepareTlsValidationForConnectAsync(bool allowTrustOnFirstUseForEmptyStore)
    {
        var uri = new Uri(HostAddress);
        await LoadPinSnapshotAsync();
        _allowFirstTimeTrustForCurrentConnect =
            IsLoopbackHost(uri) ||
            (allowTrustOnFirstUseForEmptyStore && _pinSnapshot is not null && _pinSnapshot.Count == 0);
        return uri;
    }

    private ClientWebSocket CreateConfiguredWebSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.RemoteCertificateValidationCallback = AcceptSelfSignedCertificate;
        return socket;
    }

    private async Task<string?> PromptForPinAsync()
    {
        string? result = null;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new Remex.Desktop.Views.PairingDialog
            {
                DataContext = new Remex.Desktop.ViewModels.PairingDialogViewModel()
            };

            var shell = App.Services.GetService<Remex.Desktop.ViewModels.ShellViewModel>(); // optional service
            if (shell != null && App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow != null)
                {
                    result = await dialog.ShowDialog<string?>(desktop.MainWindow);
                    return;
                }
            }
            // Fallback when no owner window is available — a single-view (non-window) lifetime, or
            // a null ShellViewModel / MainWindow. Not reachable in practice: this UI only runs as
            // the PC's classic desktop app, where a MainWindow is always present by the time a PIN
            // is requested. If it ever were taken, the method returns a null PIN.
        });
        return result;
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        CancelAndDispose(ref _receiveCts);
        CancelAndDispose(ref _reconnectCts);
        StopStandalonePairingPinPolling();
        DisposeWebSocket(ref _webSocket);
    }
}
