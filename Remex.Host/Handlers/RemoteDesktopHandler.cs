using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Remex.Host.Services.RemoteDesktop;
using Remex.Host.Services.RemoteDesktop.Windows;

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
    private bool _drawCursor = true;

    private int _desktopLeft = 0;
    private int _desktopTop = 0;
    private DesktopCodecKind _negotiatedCodec = DesktopCodecKind.Mjpeg;
    private DesktopCodecKind _activeCodec = DesktopCodecKind.Mjpeg;

    // Cached FFmpeg availability — probed once at handler construction, not per-stream-start
    private static bool? _ffmpegAvailableCache;
    private static readonly object _ffmpegCacheLock = new();


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

        var windowsDiagnostics = _hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport();
        if (windowsDiagnostics is { SupportsRemoteDesktopSession: true, CurrentDesktopReady: false })
        {
            await SendDesktopError(
                webSocket,
                BuildWindowsDesktopUnavailableMessage(windowsDiagnostics),
                ct);

            try
            {
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "windows desktop unavailable", ct);
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
                        var streamMappingId = Guid.NewGuid().ToString("N");
                        var streamSerial = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        // Negotiate codec selection based on client preference and host availability
                        _activeCodec = DesktopCodecKind.Mjpeg;
                        if (_negotiatedCodec == DesktopCodecKind.H264)
                        {
                            bool ffmpegAvailable;
                            lock (_ffmpegCacheLock)
                            {
                                if (_ffmpegAvailableCache == null)
                                {
                                    using var probeEncoder = new FFmpegH264Encoder(_logger);
                                    _ffmpegAvailableCache = probeEncoder.IsAvailable;
                                }
                                ffmpegAvailable = _ffmpegAvailableCache.Value;
                            }

                            if (ffmpegAvailable)
                            {
                                _activeCodec = DesktopCodecKind.H264;
                            }
                            else
                            {
                                _logger.LogWarning("Client requested H.264 but FFmpeg encoder is unavailable on host. Falling back to MJPEG.");
                            }
                        }

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
                                // Stage 3 additions
                                CaptureBackend = _screenCapture.BackendName,
                                InputBackend = _inputSimulation.BackendName,
                                StreamMappingId = streamMappingId,
                                LogicalWidth = sw,
                                LogicalHeight = sh,
                                PixelWidth = sw,
                                PixelHeight = sh,
                                StreamSerial = streamSerial,
                                CursorMode = "absolute",
                                CodecInfo = new DesktopCodecInfo
                                {
                                    Codec = _activeCodec,
                                    TargetFps = _targetFps,
                                    EncoderBackend = _activeCodec == DesktopCodecKind.H264 ? DesktopEncoderBackend.Software : null
                                },
                                StylusCapabilities = DesktopStylusCapabilities.None,
                            }
                        };
                        await MessageSerializer.SendAsync(webSocket, metaMsg, ct);

                        // Stage 3: send stream descriptor so client has mapping context
                        var descriptorMsg = new RemexMessage
                        {
                            Type = MessageTypes.DesktopStreamDescriptor,
                            DesktopStreamDescriptor = new DesktopStreamDescriptor
                            {
                                StreamMappingId = streamMappingId,
                                StreamSerial = streamSerial,
                                LogicalWidth = sw,
                                LogicalHeight = sh,
                                PixelWidth = sw,
                                PixelHeight = sh,
                            }
                        };
                        await MessageSerializer.SendAsync(webSocket, descriptorMsg, ct);

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
        var frameBuffer = new FrameBuffer();
        var frameAvailable = new SemaphoreSlim(0);
        var captureStopwatch = new Stopwatch();

        // Track consecutive failures to report capture failure to client
        int consecutiveFailures = 0;
        bool errorReported = false;

        // Timing metrics
        double totalCaptureMs = 0;
        double totalSendMs = 0;
        int totalFramesCaptured = 0;
        int totalFramesSent = 0;
        int totalFramesDropped = 0;
        var metricsStopwatch = Stopwatch.StartNew();

        IH264Encoder? h264Encoder = null;
        if (_activeCodec == DesktopCodecKind.H264)
        {
            var (sw, sh, _, _) = _screenCapture.GetScreenSize();
            int targetW = (int)(sw * _scale);
            int targetH = (int)(sh * _scale);
            // Ensure width and height are even (required by most H.264 encoders)
            targetW = (targetW / 2) * 2;
            targetH = (targetH / 2) * 2;

            var encoder = new FFmpegH264Encoder(_logger);
            if (encoder.Initialize(targetW, targetH, _targetFps, 1500))
            {
                h264Encoder = encoder;
            }
            else
            {
                encoder.Dispose();
                _logger.LogWarning("Failed to initialize H.264 encoder. Falling back to MJPEG.");
                _activeCodec = DesktopCodecKind.Mjpeg;
            }
        }

        // Start capture/encode loop in a separate task
        var captureTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                captureStopwatch.Restart();
                try
                {
                    byte[]? frameBytes = null;
                    if (_activeCodec == DesktopCodecKind.H264 && h264Encoder is not null)
                    {
                        bool forceKeyframe = (totalFramesCaptured % 60 == 0);
                        var rawPixels = await _screenCapture.CaptureRawScreenAsync(_scale, _drawCursor, ct);
                        if (rawPixels is { Length: > 0 })
                        {
                            frameBytes = h264Encoder.EncodeFrame(rawPixels, forceKeyframe);
                        }
                    }
                    else
                    {
                        frameBytes = await _screenCapture.CaptureScreenAsync(_quality, _scale, _drawCursor, ct);
                    }

                    if (frameBytes is { Length: > 0 })
                    {
                        consecutiveFailures = 0;
                        errorReported = false;

                        var captureDuration = captureStopwatch.Elapsed.TotalMilliseconds;
                        totalCaptureMs += captureDuration;
                        totalFramesCaptured++;

                        // Store the latest frame with thread-safe overwrite (latest-frame semantics)
                        var oldFrame = Interlocked.Exchange(ref frameBuffer.Bytes, frameBytes);
                        if (oldFrame != null && !ReferenceEquals(oldFrame, frameBytes))
                        {
                            totalFramesDropped++;
                        }

                        // Notify sender loop
                        try { frameAvailable.Release(); } catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        consecutiveFailures++;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (OutOfMemoryException ex)
                {
                    _logger.LogError(ex, "Out of memory during frame capture - aborting stream.");
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    _logger.LogWarning(ex, "Background capture thread encountered an error.");
                }

                // After 5 consecutive failures (~0.5s at 10fps), alert the client once
                if (consecutiveFailures >= 5 && !errorReported)
                {
                    errorReported = true;
                    _logger.LogWarning("Screen capture failing consistently ({Count} consecutive). Sent {Total} frames total so far.", consecutiveFailures, totalFramesSent);
                    var errorText = totalFramesSent == 0
                        ? "Screen capture is not working on the host."
                        : $"Screen capture stopped working after {totalFramesSent} frames. The host desktop may have been locked or the session disconnected.";

                    var windowsReport = _hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport();
                    if (windowsReport is not null)
                    {
                        errorText = BuildWindowsCaptureFailureMessage(windowsReport, totalFramesSent);
                    }

                    await SendDesktopError(webSocket, errorText, ct);
                }

                // Throttle. While capture is failing (locked desktop, lost session, DXGI recovering),
                // back off well below the target FPS so we don't spin the CPU or hammer the failing
                // capture path 30×/sec. Recovers immediately: consecutiveFailures resets to 0 on the
                // first successful frame, restoring the normal FPS cadence.
                int sleep;
                if (consecutiveFailures >= 5)
                {
                    sleep = 500;
                }
                else
                {
                    var elapsed = captureStopwatch.Elapsed.TotalMilliseconds;
                    var targetDelay = 1000.0 / _targetFps;
                    sleep = (int)(targetDelay - elapsed);
                }
                if (sleep > 1)
                {
                    try { await Task.Delay(sleep, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);

        // Send loop (runs on StreamFramesAsync thread)
        try
        {
            byte[]? lastSentFrame = null;
            var sendStopwatch = new Stopwatch();

            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Wait for a frame to be available
                try { await frameAvailable.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }

                // Retrieve the latest frame (and clear it from the buffer)
                var currentFrame = Interlocked.Exchange(ref frameBuffer.Bytes, null);
                if (currentFrame is not { Length: > 0 })
                {
                    continue;
                }

                // Early exit: skip sending if it is the exact same frame as last sent
                if (ReferenceEquals(currentFrame, lastSentFrame))
                {
                    continue;
                }

                sendStopwatch.Restart();
                try
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(currentFrame),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        ct);

                    totalSendMs += sendStopwatch.Elapsed.TotalMilliseconds;
                    totalFramesSent++;
                    lastSentFrame = currentFrame;
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error sending frame over WebSocket.");
                    break;
                }

                // Periodically log metrics every 5 seconds
                if (metricsStopwatch.ElapsedMilliseconds >= 5000)
                {
                    var elapsedSec = metricsStopwatch.Elapsed.TotalSeconds;
                    metricsStopwatch.Restart();

                    double avgCaptureMs = totalFramesCaptured > 0 ? totalCaptureMs / totalFramesCaptured : 0;
                    double avgSendMs = totalFramesSent > 0 ? totalSendMs / totalFramesSent : 0;
                    double effectiveFps = totalFramesSent / elapsedSec;

                    _logger.LogInformation(
                        "Stream Metrics: Sent {Sent} frames ({FPS:F1} FPS), Captured {Cap} frames, " +
                        "Dropped {Drop} frames. Avg Capture+Encode: {AvgCap:F1}ms, Avg Send: {AvgSend:F1}ms.",
                        totalFramesSent, effectiveFps, totalFramesCaptured, totalFramesDropped, avgCaptureMs, avgSendMs);

                    totalFramesSent = 0;
                    totalFramesCaptured = 0;
                    totalFramesDropped = 0;
                    totalCaptureMs = 0;
                    totalSendMs = 0;
                }
            }
        }
        finally
        {
            // Wait for capture task to exit
            try
            {
                await captureTask;
            }
            catch { }
            h264Encoder?.Dispose();
            frameAvailable.Dispose();
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

                    case MessageTypes.DesktopPointerBatch when message.DesktopPointerBatch is not null:
                        // Stage 3: high-rate stylus/pointer batches from Android.
                        // Enqueue each sample individually so the input processor can handle them.
                        // The input backend (Stage 5) will translate these to the appropriate OS events.
                        foreach (var sample in message.DesktopPointerBatch.Samples)
                        {
                            // Convert coalesced history first (oldest samples), then the primary sample.
                            if (sample.CoalescedHistory is { Count: > 0 })
                            {
                                foreach (var historic in sample.CoalescedHistory)
                                {
                                    EnqueuePointerSampleAsInputEvent(historic);
                                }
                            }

                            EnqueuePointerSampleAsInputEvent(sample);
                        }
                        break;
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
            _logger.LogWarning(ex, "Failed to dispatch input (Win32 error): {Type}. {Hint}", input.EventType, GetWindowsInputFailureHint());
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch input (invalid operation): {Type}. {Hint}", input.EventType, GetWindowsInputFailureHint());
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
        _drawCursor = config.DrawCursor;
        _negotiatedCodec = config.Codec;

        _logger.LogDebug("Desktop config updated: quality={Q}, scale={S}, fps={F}, drawCursor={D}, codec={C}", _quality, _scale, _targetFps, _drawCursor, _negotiatedCodec);
    }

    /// <summary>
    /// Stage 3 bridge: maps a DesktopPointerSample onto a legacy InputEvent so the existing
    /// DispatchInput path can handle it until Stage 5 introduces a dedicated stylus backend.
    /// Non-stylus samples are mapped to mouse events. Stylus-specific data (pressure, tilt,
    /// hover) is silently dropped here — it will be preserved end-to-end once Stage 5 lands.
    /// </summary>
    private void EnqueuePointerSampleAsInputEvent(DesktopPointerSample sample)
    {
        InputEvent? input = null;

        switch (sample.Phase)
        {
            case PointerPhase.HoverMove:
            case PointerPhase.ContactMove:
                if (sample.LogicalX != 0 || sample.LogicalY != 0)
                {
                    input = new InputEvent
                    {
                        EventType = InputEventTypes.MouseMove,
                        X = (int)sample.LogicalX,
                        Y = (int)sample.LogicalY,
                    };
                }
                else if (sample.Dx != 0 || sample.Dy != 0)
                {
                    input = new InputEvent
                    {
                        EventType = InputEventTypes.MouseMove,
                        DeltaX = (int)sample.Dx,
                        DeltaY = (int)sample.Dy,
                    };
                }
                break;

            case PointerPhase.ContactStart:
                input = new InputEvent
                {
                    EventType = InputEventTypes.MouseDown,
                    Button = 0,
                    X = sample.LogicalX != 0 ? (int?)sample.LogicalX : null,
                    Y = sample.LogicalY != 0 ? (int?)sample.LogicalY : null,
                };
                break;

            case PointerPhase.ContactEnd:
                input = new InputEvent
                {
                    EventType = InputEventTypes.MouseUp,
                    Button = 0,
                };
                break;

            // Hover start/end, button press/release — no-op in the legacy path.
            default:
                return;
        }

        if (input is not null && !_inputQueue.TryAdd(input))
        {
            _logger.LogDebug("Input queue full — dropping pointer sample phase={Phase}.", sample.Phase);
        }
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

    private string BuildWindowsDesktopUnavailableMessage(WindowsRemoteDesktopDiagnosticReport report)
    {
        var lines = new List<string>();
        AppendUniqueLine(lines, report.CurrentDesktopUnavailableReason);
        AppendUniqueLine(lines, report.RemoteDesktopUnavailableReason);
        AppendUniqueLine(lines, report.LastCaptureFailureReason);
        AppendUniqueLine(lines, report.LastInputFailureReason);
        if (!string.IsNullOrWhiteSpace(report.InputDesktopName))
        {
            AppendUniqueLine(lines, $"Active desktop: {report.InputDesktopName}.");
        }

        return lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : "Windows remote desktop is currently unavailable.";
    }

    private string BuildWindowsCaptureFailureMessage(WindowsRemoteDesktopDiagnosticReport report, int totalFramesSent)
    {
        var lines = new List<string>
        {
            totalFramesSent == 0
                ? "Screen capture failed before the first frame."
                : $"Screen capture stopped working after {totalFramesSent} frames."
        };

        AppendUniqueLine(lines, report.CurrentDesktopUnavailableReason);
        AppendUniqueLine(lines, report.LastCaptureFailureReason);
        AppendUniqueLine(lines, report.CaptureBackendDegradedReason);
        if (!string.IsNullOrWhiteSpace(report.InputDesktopName))
        {
            AppendUniqueLine(lines, $"Active desktop: {report.InputDesktopName}.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string GetWindowsInputFailureHint()
    {
        var report = _hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport();
        if (report is null)
        {
            return "Input injection requires Remex to run in the active logged-in desktop session.";
        }

        var hints = new List<string>();
        AppendUniqueLine(hints, report.CurrentDesktopUnavailableReason);
        AppendUniqueLine(hints, report.RemoteDesktopUnavailableReason);
        AppendUniqueLine(hints, report.LastInputFailureReason);
        if (!string.IsNullOrWhiteSpace(report.InputDesktopName))
        {
            AppendUniqueLine(hints, $"Active desktop: {report.InputDesktopName}.");
        }

        return hints.Count > 0
            ? string.Join(" ", hints)
            : "Input injection requires Remex to run in the active logged-in desktop session.";
    }

    private static void AppendUniqueLine(List<string> lines, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !lines.Contains(value))
        {
            lines.Add(value);
        }
    }

    private sealed class FrameBuffer
    {
        public byte[]? Bytes;
    }
}
