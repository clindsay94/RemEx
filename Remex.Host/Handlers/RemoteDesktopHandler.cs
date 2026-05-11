using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Host.Services;
using Remex.Host;
using Remex.Host.Services.Input;

namespace Remex.Host.Handlers;

public sealed class RemoteDesktopHandler : IDisposable
{
    private readonly ILogger<RemoteDesktopHandler> _logger;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IInputSimulationService _inputSimulation;
    private readonly IDesktopWindowControlService _windowControl;
    private readonly IHostCapabilitiesProvider _hostCapabilitiesProvider;
    private readonly BlockingCollection<InputEvent> _inputQueue = new(1000);
    private readonly Task _inputProcessingTask;

    private static readonly TimeSpan FrameSendTimeout = TimeSpan.FromSeconds(5);

    private int _quality = 50;
    private double _scale = 0.6;
    private int _targetFps = 30;

    private int _desktopLeft = 0;
    private int _desktopTop = 0;


    public RemoteDesktopHandler(
        ILogger<RemoteDesktopHandler> logger,
        IScreenCaptureService screenCapture,
        IInputSimulationService inputSimulation,
        IDesktopWindowControlService windowControl,
        IHostCapabilitiesProvider hostCapabilitiesProvider)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _inputSimulation = inputSimulation;
        _windowControl = windowControl;
        _hostCapabilitiesProvider = hostCapabilitiesProvider;

        // Start dedicated input processing thread
        _inputProcessingTask = Task.Factory.StartNew(ProcessInputQueue, TaskCreationOptions.LongRunning);
    }

    private void ProcessInputQueue()
    {
        try
        {
            foreach (var input in _inputQueue.GetConsumingEnumerable())
            {
                DispatchInput(input);
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Input processing thread faulted (collection was modified).");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Input processing thread cancelled gracefully.");
        }
    }

    public async Task HandleAsync(WebSocket webSocket, CancellationToken ct)
    {
        var hostCapabilities = _hostCapabilitiesProvider.GetCurrent();

        if (!hostCapabilities.SupportsRemoteDesktop)
        {
            await SendDesktopError(
                webSocket,
                hostCapabilities.RemoteDesktopUnavailableReason
                    ?? "Remote desktop is unavailable in the current host runtime.",
                ct);

            try
            {
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "interactive desktop unavailable", ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Best-effort WebSocket close failed.");
            }

            return;
        }

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

                        _logger.LogInformation("Desktop streaming started (quality={Q}, scale={S}, fps={F}).", _quality, _scale, _targetFps);

                        // Send screen metadata
                        var (sw, sh, sl, st) = _screenCapture.GetScreenSize();
                        _desktopLeft = sl;
                        _desktopTop = st;

                        var (cursorX, cursorY) = _inputSimulation.GetCursorPosition();
                        var metaMsg = new RemexMessage
                        {
                            Type = MessageTypes.DesktopMeta,
                            DesktopMeta = new DesktopMeta
                            {
                                ScreenWidth = sw,
                                ScreenHeight = sh,
                                DesktopLeft = sl,
                                DesktopTop = st,
                                HostInstanceId = HostBootstrapper.InstanceId,
                                CursorX = cursorX,
                                CursorY = cursorY,
                            }
                        };
                        await MessageSerializer.SendAsync(webSocket, metaMsg, ct);

                        // Run stream + input receive + cursor update concurrently
                        using (var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            var streamTask = StreamFramesAsync(webSocket, streamCts.Token);
                            var receiveTask = ReceiveInputLoopAsync(webSocket, streamCts, ct);
                            var cursorTask = StreamCursorPositionAsync(webSocket, streamCts.Token);

                            // When either finishes, cancel the others
                            await Task.WhenAny(streamTask, receiveTask, cursorTask);
                            await streamCts.CancelAsync();

                            try { await streamTask; } catch (OperationCanceledException) { }
                            try { await receiveTask; } catch (OperationCanceledException) { }
                            try { await cursorTask; } catch (OperationCanceledException) { }
                        }

                        // Close the WebSocket gracefully
                        try
                        {
                            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stream ended", ct);
                        }
                        catch (Exception ex) { _logger.LogTrace(ex, "Best-effort WebSocket close failed."); }

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
            _inputQueue.CompleteAdding();
            _logger.LogInformation("Remote desktop client disconnected.");
        }
    }

    private async Task StreamFramesAsync(WebSocket webSocket, CancellationToken ct)
    {
        // ── Main frame loop ──
        var stopwatch = Stopwatch.StartNew();
        int consecutiveFailures = 0;
        bool errorReported = false;
        int totalFramesSent = 0;

        while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            stopwatch.Restart();

            try
            {
                byte[] jpegBytes;
                try
                {
                    jpegBytes = await _screenCapture.CaptureScreenAsync(_quality, _scale, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    continue;
                }
                if (jpegBytes.Length > 0)
                {
                    consecutiveFailures = 0;
                    errorReported = false;

                    await webSocket.SendAsync(
                        new ArraySegment<byte>(jpegBytes),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        ct);

                    totalFramesSent++;
                }
                else
                {
                    consecutiveFailures++;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (System.ComponentModel.Win32Exception ex)
            {
                consecutiveFailures++;
                _logger.LogWarning(ex, "Frame capture/send error (Win32 error, consecutive failures: {Count}).", consecutiveFailures);
            }
            catch (InvalidOperationException ex)
            {
                consecutiveFailures++;
                _logger.LogWarning(ex, "Frame capture/send error (invalid operation, consecutive failures: {Count}).", consecutiveFailures);
            }
            catch (OutOfMemoryException ex)
            {
                _logger.LogError(ex, "Out of memory during frame capture - aborting stream.");
                break;
            }

            // After 5 consecutive failures (~0.5s at 10fps), alert the client once
            if (consecutiveFailures >= 5 && !errorReported)
            {
                errorReported = true;
                _logger.LogWarning("Screen capture failing consistently ({Count} consecutive). Sent {Total} frames total so far.", consecutiveFailures, totalFramesSent);
                await SendDesktopError(webSocket,
                    totalFramesSent == 0
                        ? "Screen capture is not working on the host."
                        : $"Screen capture stopped working after {totalFramesSent} frames. The host desktop may have been locked or the session disconnected.",
                    ct);
            }

            // Throttle to target FPS using high-resolution Stopwatch
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var targetDelayMs = 1000.0 / _targetFps;
            var sleepMs = (int)(targetDelayMs - elapsedMs);
            if (sleepMs > 1)
            {
                try { await Task.Delay(sleepMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SendDesktopError(WebSocket webSocket, string text, CancellationToken ct)
    {
        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.DesktopError,
                ErrorText = text,
            };
            await MessageSerializer.SendAsync(webSocket, msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DesktopError to client.");
        }
    }

    /// <summary>
    /// Periodically sends cursor position updates to the client (for trackpad mode).
    /// Updates every 100ms to provide smooth cursor tracking without excessive overhead.
    /// </summary>
    private async Task StreamCursorPositionAsync(WebSocket webSocket, CancellationToken ct)
    {
        var lastX = 0;
        var lastY = 0;

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (cursorX, cursorY) = _inputSimulation.GetCursorPosition();

                // Only send update if cursor moved (reduce network traffic)
                if (cursorX != lastX || cursorY != lastY)
                {
                    var (sw, sh, _, _) = _screenCapture.GetScreenSize();
                    var metaMsg = new RemexMessage
                    {
                        Type = MessageTypes.DesktopMeta,
                        DesktopMeta = new DesktopMeta
                        {
                            ScreenWidth = sw,
                            ScreenHeight = sh,
                            HostInstanceId = HostBootstrapper.InstanceId,
                            CursorX = cursorX,
                            CursorY = cursorY,
                        }
                    };

                    try
                    {
                        await MessageSerializer.SendAsync(webSocket, metaMsg, ct);
                        lastX = cursorX;
                        lastY = cursorY;
                    }
                    catch (WebSocketException)
                    {
                        break;
                    }
                }

                // Update cursor position every 100ms (10Hz)
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cursor position streaming error.");
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
                    await streamCts.CancelAsync();
                    break;
                }

                switch (message.Type)
                {
                    case MessageTypes.DesktopInput when message.InputEvent is not null:
                        if (!_inputQueue.TryAdd(message.InputEvent))
                        {
                            _logger.LogWarning("Input queue full ({Capacity} items) — dropping {Type} event.",
                                _inputQueue.BoundedCapacity, message.InputEvent.EventType);
                        }
                        break;

                    case MessageTypes.DesktopConfig when message.DesktopConfig is not null:
                        ApplyConfig(message.DesktopConfig);
                        break;

                    case MessageTypes.DesktopWindowQuery when message.DesktopWindowQuery is not null:
                        await SendDesktopWindowResult(webSocket, _windowControl.QueryWindows(message.DesktopWindowQuery), message.CorrelationId, ct);
                        break;

                    case MessageTypes.DesktopWindowAction when message.DesktopWindowAction is not null:
                        await SendDesktopWindowResult(webSocket, _windowControl.ExecuteAction(message.DesktopWindowAction), message.CorrelationId, ct);
                        break;

                    case MessageTypes.DesktopStop:
                        await streamCts.CancelAsync();
                        return;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (WebSocketException) { }
    }

    private async Task SendDesktopWindowResult(WebSocket webSocket, DesktopWindowResult result, string? correlationId, CancellationToken ct)
    {
        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.DesktopWindowResult,
                CorrelationId = correlationId,
                DesktopWindowResult = result,
            };

            await MessageSerializer.SendAsync(webSocket, msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DesktopWindowResult to client.");
        }
    }

    private void DispatchInput(InputEvent input)
    {
        try
        {
            switch (input.EventType)
            {
                case InputEventTypes.MouseMove when input.X.HasValue && input.Y.HasValue:
                    _inputSimulation.MoveMouse(input.X.Value + _desktopLeft, input.Y.Value + _desktopTop);
                    break;
                case InputEventTypes.MouseMove when input.DeltaX.HasValue || input.DeltaY.HasValue:
                    _inputSimulation.MouseMoveRelative(input.DeltaX ?? 0, input.DeltaY ?? 0);
                    break;
                case InputEventTypes.MouseDown when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                        _inputSimulation.MoveMouse(input.X.Value + _desktopLeft, input.Y.Value + _desktopTop);
                    _inputSimulation.MouseDown(input.Button.Value);
                    break;
                case InputEventTypes.MouseUp when input.Button.HasValue:
                    _inputSimulation.MouseUp(input.Button.Value);
                    break;
                case InputEventTypes.MouseClick when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                        _inputSimulation.MoveMouse(input.X.Value + _desktopLeft, input.Y.Value + _desktopTop);
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
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch input (Win32 error): {Type}", input.EventType);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch input (invalid operation): {Type}", input.EventType);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch input (invalid argument): {Type}", input.EventType);
        }
    }

    private void ApplyConfig(DesktopConfig config)
    {
        _quality = Math.Clamp(config.Quality, 1, 100);
        _scale = Math.Clamp(config.Scale, 0.25, 1.0);
        _targetFps = Math.Clamp(config.TargetFps, 1, 360);

        _logger.LogDebug("Desktop config updated: quality={Q}, scale={S}, fps={F}", _quality, _scale, _targetFps);
    }

    public void Dispose()
    {
        _inputQueue.CompleteAdding();
        try
        {
            _inputProcessingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Expected if the task faulted or was already completed.
        }
        _inputQueue.Dispose();
    }
}
