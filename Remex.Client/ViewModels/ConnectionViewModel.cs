using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core;
using Remex.Core.Messages;

namespace Remex.Client.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private const int MaxLatencyPoints = 30;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;

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

    /// <summary>Rolling window of latency samples (ms) for charting.</summary>
    public ObservableCollection<double> LatencyHistory { get; } = new();

    [ObservableProperty]
    private double _averageLatency;

    [ObservableProperty]
    private double _maxLatency;

    private bool CanConnect() => !IsConnected;
    private bool CanDisconnect() => IsConnected;
    private bool CanSendPing() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        try
        {
            StatusText = "Connecting…";
            _webSocket = new ClientWebSocket();
            _receiveCts = new CancellationTokenSource();

            await _webSocket.ConnectAsync(new Uri(HostAddress), _receiveCts.Token);

            IsConnected = true;
            StatusText = "Connected";
            LatencyText = "—";

            // Start background receive loop.
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Cleanup();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
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
        StatusText = "Disconnected";
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
                        LatencyText = $"{ms:F1} ms";
                        StatusText = $"Pong! {ms:F1} ms";
                        PushLatency(ms);
                        break;

                    case MessageTypes.Pong:
                        LatencyText = "Pong (no timestamp)";
                        StatusText = "Pong!";
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

        // If we exited the loop because the server closed, update UI state.
        if (IsConnected)
        {
            Cleanup();
            StatusText = "Disconnected (server closed)";
            LatencyText = "—";
        }
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
