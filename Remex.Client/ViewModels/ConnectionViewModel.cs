using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core;
using Remex.Core.Messages;

namespace Remex.Client.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private const int MaxLatencyPoints = 30;
    private const int MaxReconnectDelaySeconds = 30;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _reconnectCts;
    private bool _userDisconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendPingCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _hostAddress = $"ws://localhost:{RemexConstants.DefaultPort}{RemexConstants.WebSocketPath}";

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _latencyText = "—";

    [ObservableProperty]
    private bool _isAutoReconnecting;

    [ObservableProperty]
    private bool _isConnecting;

    /// <summary>Rolling window of latency samples (ms) for charting.</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    private readonly Remex.Client.Services.Network.MdnsDiscoveryService? _discoveryService;
    private readonly Remex.Client.Services.DashboardLayoutService? _layoutService;

    public ConnectionViewModel() : this(null, null) { }

    public ConnectionViewModel(
        Remex.Client.Services.Network.MdnsDiscoveryService? discoveryService,
        Remex.Client.Services.DashboardLayoutService? layoutService)
    {
        _discoveryService = discoveryService;
        _layoutService = layoutService;
    }

    [RelayCommand]
    private async Task DiscoverHostsAsync()
    {
        if (_discoveryService == null)
        {
            StatusText = "Discovery service not available.";
            return;
        }

        StatusText = "Searching for hosts…";
        var foundHosts = await _discoveryService.DiscoverHostsAsync(TimeSpan.FromSeconds(5));

        if (foundHosts.Any())
        {
            var firstHost = foundHosts.First();
            // Only overwrite if current address is default or empty
            var defaultAddress = $"ws://localhost:{RemexConstants.DefaultPort}{RemexConstants.WebSocketPath}";
            if (string.IsNullOrWhiteSpace(HostAddress) || HostAddress == defaultAddress)
            {
                HostAddress = firstHost;
                StatusText = $"Found host: {firstHost}";
            }
            else
            {
                StatusText = $"Found {foundHosts.Count} hosts. (Selectable discovery not yet implemented)";
            }
        }
        else
        {
            StatusText = "No hosts found via mDNS.";
        }
    }

    public event Action<System.Collections.Generic.List<Remex.Core.Models.AppEntry>>? LauncherEntriesReceived;
    public event Action<TelemetryPayload>? TelemetryReceived;
    public event Action<Remex.Core.Models.DashboardProfile>? LayoutProfileReceived;
    public event Action<System.Collections.Generic.List<Remex.Core.Models.ProcessInfo>>? ProcessListReceived;

    public async Task RequestProcessListAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        var msg = new RemexMessage { Type = MessageTypes.ProcessListRequest };
        await MessageSerializer.SendAsync(_webSocket, msg);
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
        var tcs = new TaskCompletionSource<RemexMessage>();
        _pendingCommandResponse = tcs;
        try
        {
            await MessageSerializer.SendAsync(_webSocket, msg);
            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new Remex.Core.Models.IPC.CommandResponse(response.CommandSuccess ?? false, response.CommandMessage ?? "", null);
        }
        catch
        {
            return new Remex.Core.Models.IPC.CommandResponse(false, "Timeout waiting for server response", null);
        }
        finally
        {
            _pendingCommandResponse = null;
        }
    }

    [ObservableProperty]
    private double _averageLatency;

    [ObservableProperty]
    private double _maxLatency;

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

    private bool CanConnect() => !IsConnected && !IsConnecting;
    private bool CanDisconnect() => IsConnected || IsConnecting;

    private System.Threading.Tasks.TaskCompletionSource<RemexMessage>? _pendingCommandResponse;

    public System.Net.WebSockets.WebSocket? GetWebSocket() => _webSocket;
    public async Task<(bool Success, string Message)> SendCommandAsync(string action, System.Collections.Generic.Dictionary<string, string>? parameters = null)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return (false, "Not connected.");

        var tcs = new System.Threading.Tasks.TaskCompletionSource<RemexMessage>();
        _pendingCommandResponse = tcs;

        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.Command,
                CommandAction = action,
                CommandParameters = parameters,
                Timestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
            };
            await MessageSerializer.SendAsync(_webSocket, msg, System.Threading.CancellationToken.None);

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task;
            return (response.CommandSuccess ?? false, response.CommandMessage ?? "No message");
        }
        catch (OperationCanceledException)
        {
            return (false, "Command timed out.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _pendingCommandResponse = null;
        }
    }

    private bool CanSendPing() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _userDisconnected = false;
        IsConnecting = true;
        StopReconnecting();

        // Define CTS outside try to be accessible in catch
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _webSocket = new ClientWebSocket();
        _receiveCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts.Token, timeoutCts.Token);

        try
        {
            StatusText = "Connecting…";
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            
            await _webSocket.ConnectAsync(new Uri(HostAddress), linkedCts.Token);

            IsConnected = true;
            IsConnecting = false;
            StatusText = "Connected";
            LatencyText = "—";

            // Start background receive loop.
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = linkedCts.Token.IsCancellationRequested && !timeoutCts.Token.IsCancellationRequested 
                ? "Connection cancelled" 
                : "Connection timed out";
            Cleanup();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Cleanup();
        }
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
        catch
        {
            // Best-effort close.
        }

        Cleanup();
        StatusText = IsConnecting ? "Connection cancelled" : "Disconnected";
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
            StatusText = "Ping sent…";
        }
        catch (Exception ex)
        {
            StatusText = $"Send error: {ex.Message}";
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
            await MessageSerializer.SendAsync(_webSocket, msg);
        }
        catch (Exception ex)
        {
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
                            StatusText = $"Pong! {ms:F1} ms";
                            PushLatency(ms);
                        });
                        break;

                    case MessageTypes.Pong:
                        Dispatcher.UIThread.Post(() =>
                        {
                            LatencyText = "Pong (no timestamp)";
                            StatusText = "Pong!";
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
                        _pendingCommandResponse?.TrySetResult(message);
                        break;

                    case MessageTypes.LauncherSync when message.LauncherEntries is not null:
                        Dispatcher.UIThread.Post(() => LauncherEntriesReceived?.Invoke(message.LauncherEntries));
                        break;

                    case MessageTypes.ProcessListSync when message.ProcessList is not null:
                        Dispatcher.UIThread.Post(() => ProcessListReceived?.Invoke(message.ProcessList));
                        break;
                    case MessageTypes.LayoutSync when message.DashboardProfile is not null:
                        Dispatcher.UIThread.Post(() => LayoutProfileReceived?.Invoke(message.DashboardProfile));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect.
        }
        catch (WebSocketException)
        {
            // Connection lost.
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"Receive error: {ex.Message}";
            });
        }

        // If we exited the loop because the server closed, update UI state.
        if (IsConnected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Cleanup();
                StatusText = "Disconnected (server closed)";
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
                Dispatcher.UIThread.Post(() => StatusText = $"Reconnecting in {delay}s…");
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                if (ct.IsCancellationRequested) break;

                try
                {
                    Dispatcher.UIThread.Post(() => StatusText = "Connecting…");
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(new Uri(HostAddress), connectCts.Token);

                    // Success — adopt the new socket.
                    _webSocket = ws;
                    _receiveCts = new CancellationTokenSource();

                    Dispatcher.UIThread.Post(() =>
                    {
                        IsConnected = true;
                        IsAutoReconnecting = false;
                        StatusText = "Connected";
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

        IsConnected = false;
    }
}
