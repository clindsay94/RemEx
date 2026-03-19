using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Host;

namespace Remex.Host.Handlers;

public sealed class RemoteDesktopHandler
{
    private readonly ILogger<RemoteDesktopHandler> _logger;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IInputSimulationService _inputSimulation;

    private int _quality = 50;
    private double _scale = 0.5;
    private int _targetFps = 10;
    private bool _streaming;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RemoteDesktopHandler(
        ILogger<RemoteDesktopHandler> logger,
        IScreenCaptureService screenCapture,
        IInputSimulationService inputSimulation)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _inputSimulation = inputSimulation;
    }

    public async Task HandleAsync(WebSocket webSocket, CancellationToken ct)
    {
        _logger.LogInformation("Remote desktop client connected.");

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await MessageSerializer.ReceiveAsync(webSocket, ct);
                if (message is null) break;

                switch (message.Type)
                {
                    case MessageTypes.DesktopStart:
                        if (message.DesktopConfig is not null)
                            ApplyConfig(message.DesktopConfig);

                        _streaming = true;
                        _logger.LogInformation("Desktop streaming started (quality={Q}, scale={S}, fps={F}).", _quality, _scale, _targetFps);

                        // Send screen metadata
                        var (sw, sh) = _screenCapture.GetScreenSize();
                        var metaMsg = new RemexMessage
                        {
                            Type = MessageTypes.DesktopMeta,
                            DesktopMeta = new DesktopMeta
                            {
                                ScreenWidth = sw,
                                ScreenHeight = sh,
                                HostInstanceId = HostBootstrapper.InstanceId,
                            }
                        };
                        await MessageSerializer.SendAsync(webSocket, metaMsg, ct);

                        // Run stream + input receive concurrently
                        using (var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            var streamTask = StreamFramesAsync(webSocket, streamCts.Token);
                            var receiveTask = ReceiveInputLoopAsync(webSocket, streamCts, ct);

                            // When either finishes, cancel the other
                            await Task.WhenAny(streamTask, receiveTask);
                            await streamCts.CancelAsync();

                            try { await streamTask; } catch (OperationCanceledException) { }
                            try { await receiveTask; } catch (OperationCanceledException) { }
                        }

                        // Close the WebSocket gracefully
                        try
                        {
                            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stream ended", ct);
                        }
                        catch { /* best-effort close */ }

                        // If we exit the inner loop, break the outer loop too
                        return;

                    case MessageTypes.DesktopConfig when message.DesktopConfig is not null:
                        ApplyConfig(message.DesktopConfig);
                        break;

                    default:
                        _logger.LogDebug("Ignoring message type {Type} before streaming started.", message.Type);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Remote desktop WebSocket error.");
        }
        finally
        {
            _streaming = false;
            _logger.LogInformation("Remote desktop client disconnected.");
        }
    }

    private async Task StreamFramesAsync(WebSocket webSocket, CancellationToken ct)
    {
        while (_streaming && webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;

            try
            {
                var jpegBytes = await _screenCapture.CaptureScreenAsync(_quality, _scale, ct);
                if (jpegBytes.Length > 0)
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(jpegBytes),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Frame capture/send error.");
            }

            // Throttle to target FPS
            var elapsed = (DateTime.UtcNow - frameStart).TotalMilliseconds;
            var targetDelay = 1000.0 / _targetFps;
            var sleepMs = (int)(targetDelay - elapsed);
            if (sleepMs > 0)
            {
                try { await Task.Delay(sleepMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReceiveInputLoopAsync(WebSocket webSocket, CancellationTokenSource streamCts, CancellationToken ct)
    {
        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await MessageSerializer.ReceiveAsync(webSocket, ct);
                if (message is null)
                {
                    // Client disconnected
                    _streaming = false;
                    await streamCts.CancelAsync();
                    break;
                }

                switch (message.Type)
                {
                    case MessageTypes.DesktopInput when message.InputEvent is not null:
                        DispatchInput(message.InputEvent);
                        break;

                    case MessageTypes.DesktopConfig when message.DesktopConfig is not null:
                        ApplyConfig(message.DesktopConfig);
                        break;

                    case MessageTypes.DesktopStop:
                        _streaming = false;
                        await streamCts.CancelAsync();
                        return;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (WebSocketException) { _streaming = false; }
    }

    private void DispatchInput(InputEvent input)
    {
        try
        {
            switch (input.EventType)
            {
                case InputEventTypes.MouseMove when input.X.HasValue && input.Y.HasValue:
                    _inputSimulation.MoveMouse(input.X.Value, input.Y.Value);
                    break;
                case InputEventTypes.MouseMove when input.DeltaX.HasValue || input.DeltaY.HasValue:
                    _inputSimulation.MouseMoveRelative(input.DeltaX ?? 0, input.DeltaY ?? 0);
                    break;
                case InputEventTypes.MouseDown when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                        _inputSimulation.MoveMouse(input.X.Value, input.Y.Value);
                    _inputSimulation.MouseDown(input.Button.Value);
                    break;
                case InputEventTypes.MouseUp when input.Button.HasValue:
                    _inputSimulation.MouseUp(input.Button.Value);
                    break;
                case InputEventTypes.MouseClick when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                        _inputSimulation.MoveMouse(input.X.Value, input.Y.Value);
                    _inputSimulation.MouseClick(input.Button.Value);
                    break;
                case InputEventTypes.MouseScroll:
                    _inputSimulation.MouseScroll(input.DeltaX ?? 0, input.DeltaY ?? 0);
                    break;
                case InputEventTypes.KeyDown when input.KeyCode.HasValue:
                    _inputSimulation.KeyDown(input.KeyCode.Value);
                    break;
                case InputEventTypes.KeyUp when input.KeyCode.HasValue:
                    _inputSimulation.KeyUp(input.KeyCode.Value);
                    break;
                case InputEventTypes.TypeText when input.Text is not null:
                    _inputSimulation.TypeText(input.Text);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch input: {Type}", input.EventType);
        }
    }

    private void ApplyConfig(DesktopConfig config)
    {
        _quality = Math.Clamp(config.Quality, 1, 100);
        _scale = Math.Clamp(config.Scale, 0.25, 1.0);
        _targetFps = Math.Clamp(config.TargetFps, 1, 360);
        _logger.LogDebug("Desktop config updated: quality={Q}, scale={S}, fps={F}", _quality, _scale, _targetFps);
    }
}
