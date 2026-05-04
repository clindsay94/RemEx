using System;
using System.Collections.Concurrent;
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
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;
using Remex.Client.Services;
using Remex.Core;
using Remex.Core.Exceptions;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Network;
using Remex.Core.Services.Security;
using Remex.Core.Validation;

namespace Remex.Client.ViewModels;

public partial class ConnectionViewModel : ObservableValidator, IDisposable
{
    private const int MaxLatencyPoints = 30;
    private const int MaxReconnectDelaySeconds = 30;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _reconnectCts;
    private bool _userDisconnected;
    private bool _isPairedWithCurrentHost;
    private string? _cachedLocalIpv4;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendPingCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Host address is required")]
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
    private DateTimeOffset? _activePairingExpiresAt;

    public bool HasActivePairingPin => !string.IsNullOrEmpty(ActivePairingPin);

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

    /// <summary>
    /// Subscribes to pairing-pin events on the in-process host's PairingService so the
    /// desktop UI can show the user the PIN their phone is asking for.
    /// </summary>
    public void AttachEmbeddedPairingService(IPairingService service)
    {
        Guard.NotNull(service);
        service.PinDisplayed += (pin, expires) =>
            Dispatcher.UIThread.Post(() =>
            {
                ActivePairingPin = pin;
                ActivePairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires);
            });
        service.PinCleared += () =>
            Dispatcher.UIThread.Post(() =>
            {
                ActivePairingPin = null;
                ActivePairingExpiresAt = null;
            });
    }

    /// <summary>Rolling window of latency samples (ms) for charting.</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    /// <summary>Hosts discovered via mDNS; populated after <see cref="DiscoverHostsCommand"/> completes.</summary>
    public ObservableCollection<string> DiscoveredHosts { get; } = new();

    /// <summary>Recently used connection addresses (most-recent first, max 10).</summary>
    public ObservableCollection<Remex.Core.Models.ConnectionProfile> ConnectionHistory { get; } = new();

    private readonly IMdnsDiscoveryService? _discoveryService;
    private readonly Remex.Client.Services.DashboardLayoutService? _layoutService;
    private readonly ILogger<ConnectionViewModel> _logger;

    public ConnectionViewModel() : this(null, null, null) { }

    public ConnectionViewModel(
        IMdnsDiscoveryService? discoveryService,
        Remex.Client.Services.DashboardLayoutService? layoutService,
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
        if (_webSocket?.State != WebSocketState.Open) return;
        var msg = new RemexMessage { Type = MessageTypes.ProcessListRequest };
        await MessageSerializer.SendAsync(_webSocket, msg);
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

    public string HostRuntimeSummary
    {
        get
        {
            if (HostCapabilities is null)
            {
                return IsConnected ? LocalizationService.Instance["Status_ConnectedToHost"] : LocalizationService.Instance["Status_HostNotConnected"];
            }

            var runtimeLabel = HostCapabilities.RuntimeMode switch
            {
                "interactive" => LocalizationService.Instance["Status_InteractiveHost"],
                "service" => LocalizationService.Instance["Status_ServiceHost"],
                "headless" => LocalizationService.Instance["Status_HeadlessHost"],
                _ => LocalizationService.Instance["Status_Host"]
            };

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
            await MessageSerializer.SendAsync(_webSocket!, msg with { CorrelationId = correlationId }, ct);
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
            StatusText = errors ?? "Invalid connection settings";
            return;
        }

        _userDisconnected = false;
        IsConnecting = true;
        HostCapabilities = null;
        StopReconnecting();

        // Define CTS outside try to be accessible in catch
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _webSocket = new ClientWebSocket();
        _receiveCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts.Token, timeoutCts.Token);

        try
        {
            StatusText = LocalizationService.Instance["Status_Connecting"];
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            // 2.0: Accept self-signed certificates (TLS pinning validated by PinnedCertStore)
            _webSocket.Options.RemoteCertificateValidationCallback = AcceptSelfSignedCertificate;

            var uri = new Uri(HostAddress);
            await _webSocket.ConnectAsync(uri, linkedCts.Token);

            // Loopback connections target the in-process embedded host on the same machine.
            // Pairing exists to bootstrap trust with a *remote* host, so it adds no security
            // here — and would prompt the user for a PIN their own desktop generated.
            if (IsLoopbackHost(uri))
                _isPairedWithCurrentHost = true;

            if (!_isPairedWithCurrentHost)
            {
                StatusText = LocalizationService.Instance["Status_Pairing"] ?? "Pairing with Host...";
                var certStore = App.Services.GetService<Remex.Client.Services.Security.PinnedCertStore>();
                var pairingClient = new Remex.Core.Native.PairingClient(_webSocket, null);

                var response = await pairingClient.StartPairingAsync(Environment.MachineName, "2.0.0", linkedCts.Token);
                if (response == null)
                {
                    StatusText = "Pairing Failed";
                    Cleanup();
                    return;
                }

                var pin = await PromptForPinAsync();
                if (string.IsNullOrEmpty(pin))
                {
                    StatusText = "Pairing Cancelled";
                    Cleanup();
                    return;
                }

                var success = await pairingClient.CompletePairingAsync(pin, response, linkedCts.Token);
                if (!success)
                {
                    StatusText = "Pairing Failed";
                    Cleanup();
                    return;
                }

                // Pairing successful, save the SPKI hash!
                await certStore!.SetPinAsync(response.HostId, response.CertificateSpkiHashBase64);
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
            await MessageSerializer.SendAsync(_webSocket, ping);
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

    public async Task SendAsync(RemexMessage message)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        await MessageSerializer.SendAsync(_webSocket, message);
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
            await MessageSerializer.SendAsync(_webSocket, msg);
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
                    case MessageTypes.FileTransferChunk:
                    case MessageTypes.FileTransferEnd:
                    case MessageTypes.FileTransferProgress:
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
                StatusText = "Error: Invalid message format from host";
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

                try
                {
                    Dispatcher.UIThread.Post(() => StatusText = LocalizationService.Instance["Status_Connecting"]);
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    ws.Options.RemoteCertificateValidationCallback = AcceptSelfSignedCertificate;

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(BuildWebSocketUri(HostAddress, string.Empty), connectCts.Token);

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
                    // Exponential backoff: 2, 4, 8, 16, 30, 30, ...
                    delay = Math.Min(delay * 2, MaxReconnectDelaySeconds);
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
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
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

    private void Cleanup()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        _webSocket?.Dispose();
        _webSocket = null;

        // Cancel all in-flight command awaiters so callers don't hang after disconnect
        foreach (var (_, pendingTcs) in _pendingCommands)
            pendingTcs.TrySetCanceled();
        _pendingCommands.Clear();

        IsConnected = false;
        HostCapabilities = null;
    }

    [RelayCommand]
    private void GenerateQrCode()
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

            var certService = App.Services.GetService<ICertificateService>();
            var spkiHash = certService?.GetSpkiSha256Base64() ?? "";

            var payload = JsonSerializer.Serialize(new
            {
                host,
                port,
                hostId = string.Empty,
                spkiHashBase64 = spkiHash
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
            StatusText = "Error: Invalid QR code data";
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
    /// Accepts self-signed certificates for the 2.0 TLS transport.
    /// Real pinning validation is performed at a higher level by PinnedCertStore.
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

        var store = App.Services.GetService<Remex.Client.Services.Security.PinnedCertStore>();
        if (store != null)
        {
            var pins = store.GetAllPinsAsync().GetAwaiter().GetResult();
            if (pins.Values.Contains(hashBase64))
            {
                _isPairedWithCurrentHost = true;
                return true;
            }

            _isPairedWithCurrentHost = false;
            _logger.LogWarning("Unknown host certificate SPKI {Hash}. Accepting for potential pairing handshake.", hashBase64);
        }

        return true;
    }

    private async Task<string?> PromptForPinAsync()
    {
        string? result = null;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new Remex.Client.Views.PairingDialog
            {
                DataContext = new Remex.Client.ViewModels.PairingDialogViewModel()
            };

            var shell = App.Services.GetService<Remex.Client.ViewModels.ShellViewModel>();
            if (shell != null && App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow != null)
                {
                    result = await dialog.ShowDialog<string?>(desktop.MainWindow);
                    return;
                }
            }
            // Fallback for single view / mobile (though this is track 1C desktop mostly)
            // Just show it. Note: ShowDialog requires a window, mobile needs different navigation.
            // But we're desktop client right now.
        });
        return result;
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        _receiveCts?.Dispose();
        _reconnectCts?.Dispose();
        _webSocket?.Dispose();
    }
}
