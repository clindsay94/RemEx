using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
using Remex.Host.Services.ScreenCapture;

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
    private DesktopClientCapabilities _clientCapabilities = new();

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
        using var sendLock = new SemaphoreSlim(1, 1);
        var hostCapabilities = _hostCapabilitiesProvider.GetCurrent();

        if (!hostCapabilities.SupportsRemoteDesktop)
        {
            await SendDesktopError(
                webSocket,
                hostCapabilities.RemoteDesktopUnavailableReason
                    ?? "Remote desktop is unavailable in the current host runtime.",
                sendLock,
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
                sendLock,
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
                    case MessageTypes.DesktopDisplayQuery:
                        await SendDisplayCatalogAsync(webSocket, sendLock, ct);
                        break;

                    case MessageTypes.DesktopStart:
                        if (message.DesktopConfig is not null)
                        {
                            if (!TryApplyCaptureTarget(message.DesktopConfig, out var targetError))
                            {
                                await SendDesktopError(webSocket, targetError ?? "The requested desktop target is unavailable.", sendLock, ct);
                                break;
                            }

                            ApplyConfig(message.DesktopConfig);
                        }

                        _logger.LogInformation("Desktop streaming started (quality={Q}, scale={S}, fps={F}).", _quality, _scale, _targetFps);

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

                        var sessionState = new DesktopSessionState(_clientCapabilities);
                        var frameBuffer = new FrameBuffer();
                        await SendCurrentStreamBootstrapAsync(webSocket, sessionState, sendLock, ct);

                        // Run stream + input receive + cursor update concurrently
                        using (var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            var streamTask = StreamFramesAsync(webSocket, frameBuffer, sessionState, sendLock, streamCts.Token);
                            var receiveTask = ReceiveInputLoopAsync(webSocket, frameBuffer, sessionState, sendLock, streamCts, ct);
                            var cursorTask = StreamCursorPositionAsync(webSocket, sessionState, sendLock, streamCts.Token);

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

    private async Task StreamFramesAsync(
        WebSocket webSocket,
        FrameBuffer frameBuffer,
        DesktopSessionState sessionState,
        SemaphoreSlim sendLock,
        CancellationToken ct)
    {
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
        long encoderSerial = -1;

        // Start capture/encode loop in a separate task
        var captureTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                captureStopwatch.Restart();
                try
                {
                    var captureSerial = sessionState.StreamSerial;
                    byte[]? frameBytes = null;
                    DesktopCodecKind frameCodec = _activeCodec;
                    var frameFlags = DesktopFrameFlags.None;
                    if (_activeCodec == DesktopCodecKind.H264 && encoderSerial != captureSerial)
                    {
                        h264Encoder?.Dispose();
                        h264Encoder = TryCreateH264Encoder();
                        encoderSerial = captureSerial;
                        if (h264Encoder is null)
                        {
                            frameCodec = DesktopCodecKind.Mjpeg;
                            _activeCodec = DesktopCodecKind.Mjpeg;
                        }
                        else
                        {
                            frameCodec = DesktopCodecKind.H264;
                            frameFlags = DesktopFrameFlags.KeyFrame;
                        }
                    }

                    if (_activeCodec == DesktopCodecKind.H264 && h264Encoder is not null)
                    {
                        bool forceKeyframe = frameFlags.HasFlag(DesktopFrameFlags.KeyFrame) || (totalFramesCaptured % 60 == 0);
                        var rawPixels = await _screenCapture.CaptureRawScreenAsync(_scale, _drawCursor, ct);
                        if (captureSerial != sessionState.StreamSerial)
                            continue;

                        if (rawPixels is { Length: > 0 })
                        {
                            // Self-heal: if the captured buffer size no longer matches the encoder's
                            // fixed input size (capture backend/DPI/geometry drifted since the encoder
                            // was created), feeding it would desync the rawvideo pipe and produce 0
                            // frames. Reinitialize the encoder to the new size instead and skip this frame.
                            if (rawPixels.Length != h264Encoder.ExpectedInputByteCount)
                            {
                                _logger.LogWarning(
                                    "H.264 raw frame size {Actual} != encoder input size {Expected}; reinitializing encoder.",
                                    rawPixels.Length, h264Encoder.ExpectedInputByteCount);
                                encoderSerial = -1; // force recreate on next iteration
                                consecutiveFailures++;
                            }
                            else
                            {
                                frameBytes = h264Encoder.EncodeFrame(rawPixels, forceKeyframe);
                                frameFlags = forceKeyframe ? DesktopFrameFlags.KeyFrame : DesktopFrameFlags.None;
                            }
                        }
                    }
                    else
                    {
                        frameBytes = await _screenCapture.CaptureScreenAsync(_quality, _scale, _drawCursor, ct);
                        if (captureSerial != sessionState.StreamSerial)
                            continue;
                    }

                    if (frameBytes is { Length: > 0 })
                    {
                        consecutiveFailures = 0;
                        errorReported = false;

                        var captureDuration = captureStopwatch.Elapsed.TotalMilliseconds;
                        totalCaptureMs += captureDuration;
                        totalFramesCaptured++;

                        // Store the latest frame with thread-safe overwrite (latest-frame semantics)
                        var capturedFrame = new CapturedFrame
                        {
                            Bytes = frameBytes,
                            StreamSerial = captureSerial,
                            Sequence = sessionState.NextFrameSequence(),
                            Flags = frameFlags,
                            Codec = frameCodec,
                        };
                        var oldFrame = Interlocked.Exchange(ref frameBuffer.Frame, capturedFrame);
                        if (oldFrame != null && !ReferenceEquals(oldFrame, capturedFrame))
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

                    await SendDesktopError(webSocket, errorText, sendLock, ct);
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
            CapturedFrame? lastSentFrame = null;
            var sendStopwatch = new Stopwatch();

            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Wait for a frame to be available
                try { await frameAvailable.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }

                // Retrieve the latest frame (and clear it from the buffer)
                var currentFrame = Interlocked.Exchange(ref frameBuffer.Frame, null);
                if (currentFrame?.Bytes is not { Length: > 0 })
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
                    var payload = sessionState.UseFrameEnvelope
                        ? DesktopFrameEnvelope.Wrap(
                            currentFrame.Bytes,
                            currentFrame.StreamSerial,
                            currentFrame.Sequence,
                            currentFrame.Codec,
                            currentFrame.Flags)
                        : currentFrame.Bytes;

                    await SendBinaryAsync(webSocket, payload, sendLock, ct);

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

    private async Task SendDesktopError(WebSocket webSocket, string text, SemaphoreSlim sendLock, CancellationToken ct)
    {
        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.DesktopError,
                ErrorText = text,
            };
            await SendMessageAsync(webSocket, msg, sendLock, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DesktopError to client.");
        }
    }

    private async Task SendDisplayCatalogAsync(WebSocket webSocket, SemaphoreSlim sendLock, CancellationToken ct)
    {
        try
        {
            var message = new RemexMessage
            {
                Type = MessageTypes.DesktopDisplayList,
                DesktopDisplayCatalog = _screenCapture.GetDisplayCatalog(),
            };
            await SendMessageAsync(webSocket, message, sendLock, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DesktopDisplayList to client.");
        }
    }

    /// <summary>
    /// Periodically sends cursor position updates to the client (for trackpad mode).
    /// Updates every 100ms to provide smooth cursor tracking without excessive overhead.
    /// </summary>
    private async Task StreamCursorPositionAsync(WebSocket webSocket, DesktopSessionState sessionState, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var (lastX, lastY) = _inputSimulation.GetCursorPosition();
        var lastShapeSerial = sessionState.GetCurrentCursorShape()?.ShapeSerial ?? 0;

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (cursorX, cursorY) = _inputSimulation.GetCursorPosition();
                var currentShape = sessionState.GetCurrentCursorShape();
                if (sessionState.UseCursorShape && OperatingSystem.IsWindows())
                {
                    currentShape = await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: false, ct);
                }

                // Only send update if cursor moved (reduce network traffic)
                var currentShapeSerial = currentShape?.ShapeSerial ?? 0;
                if (cursorX != lastX || cursorY != lastY || currentShapeSerial != lastShapeSerial)
                {
                    try
                    {
                        await SendCursorStateAsync(webSocket, sessionState, cursorX, cursorY, currentShape, sendLock, ct);
                        lastX = cursorX;
                        lastY = cursorY;
                        lastShapeSerial = currentShapeSerial;
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

    private async Task ReceiveInputLoopAsync(
        WebSocket webSocket,
        FrameBuffer frameBuffer,
        DesktopSessionState sessionState,
        SemaphoreSlim sendLock,
        CancellationTokenSource streamCts,
        CancellationToken ct)
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

                    case MessageTypes.DesktopTargetSwitch when message.DesktopTargetSwitch is not null:
                        await HandleTargetSwitchAsync(webSocket, frameBuffer, sessionState, message.DesktopTargetSwitch, sendLock, ct);
                        break;

                    case MessageTypes.DesktopWindowQuery when message.DesktopWindowQuery is not null:
                        await SendDesktopWindowResult(webSocket, _windowControl.QueryWindows(message.DesktopWindowQuery), message.CorrelationId, sendLock, ct);
                        break;

                    case MessageTypes.DesktopWindowAction when message.DesktopWindowAction is not null:
                        await SendDesktopWindowResult(webSocket, _windowControl.ExecuteAction(message.DesktopWindowAction), message.CorrelationId, sendLock, ct);
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

    private async Task SendDesktopWindowResult(WebSocket webSocket, DesktopWindowResult result, string? correlationId, SemaphoreSlim sendLock, CancellationToken ct)
    {
        try
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.DesktopWindowResult,
                CorrelationId = correlationId,
                DesktopWindowResult = result,
            };

            await SendMessageAsync(webSocket, msg, sendLock, ct);
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
        if (config.ClientCapabilities is not null)
        {
            _clientCapabilities = config.ClientCapabilities;
        }

        _quality = Math.Clamp(config.Quality, 1, 100);
        _scale = Math.Clamp(config.Scale, 0.25, 1.0);
        _targetFps = Math.Clamp(config.TargetFps, 1, 360);
        _drawCursor = config.DrawCursor && !(_clientCapabilities.SupportsCursorState || _clientCapabilities.SupportsCursorShape);
        _negotiatedCodec = config.Codec;

        _logger.LogDebug("Desktop config updated: quality={Q}, scale={S}, fps={F}, drawCursor={D}, codec={C}", _quality, _scale, _targetFps, _drawCursor, _negotiatedCodec);
    }

    private bool TryApplyCaptureTarget(DesktopConfig config, out string? error)
    {
        var requestedExplicitTarget =
            config.CaptureMode.HasValue ||
            !string.IsNullOrWhiteSpace(config.DisplayId) ||
            config.DisplayListVersion.HasValue;

        if (config.ClientCapabilities?.SupportsDisplaySelection == true &&
            config.DesktopProtocolVersion.GetValueOrDefault() >= 1 &&
            !requestedExplicitTarget)
        {
            error = "Select a desktop target before starting the stream.";
            return false;
        }

        if (!requestedExplicitTarget)
        {
            error = null;
            return true;
        }

        if (!config.CaptureMode.HasValue)
        {
            error = "The requested desktop target did not specify a capture mode.";
            return false;
        }

        var displayCatalog = _screenCapture.GetDisplayCatalog();
        if (config.DisplayListVersion.HasValue &&
            config.DisplayListVersion.Value != displayCatalog.DisplayListVersion)
        {
            error = "The available display list changed before the stream started. Refresh the display list and try again.";
            return false;
        }

        if (!displayCatalog.SupportedCaptureModes.Contains(config.CaptureMode.Value))
        {
            error = $"The host does not support '{config.CaptureMode.Value}' capture mode.";
            return false;
        }

        return TryApplyCaptureTarget(new DesktopCaptureTarget
        {
            CaptureMode = config.CaptureMode.Value,
            DisplayId = config.DisplayId,
        }, config.DisplayListVersion, out error);
    }

    private bool TryApplyCaptureTarget(DesktopCaptureTarget target, int? displayListVersion, out string? error)
    {
        var displayCatalog = _screenCapture.GetDisplayCatalog();
        if (displayListVersion.HasValue &&
            displayListVersion.Value != displayCatalog.DisplayListVersion)
        {
            error = "The available display list changed before the stream started. Refresh the display list and try again.";
            return false;
        }

        if (!displayCatalog.SupportedCaptureModes.Contains(target.CaptureMode))
        {
            error = $"The host does not support '{target.CaptureMode}' capture mode.";
            return false;
        }

        return _screenCapture.TrySetCaptureTarget(target, out error);
    }

    private async Task HandleTargetSwitchAsync(
        WebSocket webSocket,
        FrameBuffer frameBuffer,
        DesktopSessionState sessionState,
        DesktopTargetSwitchRequest request,
        SemaphoreSlim sendLock,
        CancellationToken ct)
    {
        if (!sessionState.SupportsTargetSwitch)
        {
            await SendDesktopError(webSocket, "This client session does not support in-session display switching.", sendLock, ct);
            return;
        }

        if (!TryApplyCaptureTarget(request.Target, request.DisplayListVersion, out var error))
        {
            await SendDesktopError(webSocket, error ?? "The requested desktop target is unavailable.", sendLock, ct);
            return;
        }

        Interlocked.Exchange(ref frameBuffer.Frame, null);
        sessionState.ResetStream();
        await SendCurrentStreamBootstrapAsync(webSocket, sessionState, sendLock, ct);
    }

    private async Task SendCurrentStreamBootstrapAsync(
        WebSocket webSocket,
        DesktopSessionState sessionState,
        SemaphoreSlim sendLock,
        CancellationToken ct)
    {
        var (screenWidth, screenHeight, desktopLeft, desktopTop) = _screenCapture.GetScreenSize();
        _desktopLeft = desktopLeft;
        _desktopTop = desktopTop;
        var (cursorX, cursorY) = _inputSimulation.GetCursorPosition();

        await SendMessageAsync(webSocket, new RemexMessage
        {
            Type = MessageTypes.DesktopMeta,
            DesktopMeta = new DesktopMeta
            {
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                DesktopLeft = desktopLeft,
                DesktopTop = desktopTop,
                HostInstanceId = HostBootstrapper.InstanceId,
                CursorX = cursorX,
                CursorY = cursorY,
                CaptureBackend = _screenCapture.BackendName,
                InputBackend = _inputSimulation.BackendName,
                StreamMappingId = sessionState.StreamMappingId,
                LogicalWidth = screenWidth,
                LogicalHeight = screenHeight,
                PixelWidth = screenWidth,
                PixelHeight = screenHeight,
                StreamSerial = sessionState.StreamSerial,
                CursorMode = "absolute",
                CodecInfo = new DesktopCodecInfo
                {
                    Codec = _activeCodec,
                    TargetFps = _targetFps,
                    EncoderBackend = _activeCodec == DesktopCodecKind.H264 ? DesktopEncoderBackend.Software : null
                },
                StylusCapabilities = DesktopStylusCapabilities.None,
            }
        }, sendLock, ct);

        await SendMessageAsync(webSocket, new RemexMessage
        {
            Type = MessageTypes.DesktopStreamDescriptor,
            DesktopStreamDescriptor = new DesktopStreamDescriptor
            {
                StreamMappingId = sessionState.StreamMappingId,
                StreamSerial = sessionState.StreamSerial,
                LogicalWidth = screenWidth,
                LogicalHeight = screenHeight,
                PixelWidth = screenWidth,
                PixelHeight = screenHeight,
            }
        }, sendLock, ct);

        if (sessionState.UseCursorState)
        {
            var currentShape = await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: true, ct);
            await SendCursorStateAsync(webSocket, sessionState, cursorX, cursorY, currentShape, sendLock, ct);
        }
    }

    private async Task SendCursorUpdateAsync(
        WebSocket webSocket,
        DesktopSessionState sessionState,
        int cursorX,
        int cursorY,
        SemaphoreSlim sendLock,
        CancellationToken ct)
    {
        if (sessionState.UseCursorState)
        {
            var currentShape = await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: false, ct);
            await SendCursorStateAsync(webSocket, sessionState, cursorX, cursorY, currentShape, sendLock, ct);
            return;
        }

        var (screenWidth, screenHeight, desktopLeft, desktopTop) = _screenCapture.GetScreenSize();
        _desktopLeft = desktopLeft;
        _desktopTop = desktopTop;

        await SendMessageAsync(webSocket, new RemexMessage
        {
            Type = MessageTypes.DesktopMeta,
            DesktopMeta = new DesktopMeta
            {
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                DesktopLeft = desktopLeft,
                DesktopTop = desktopTop,
                HostInstanceId = HostBootstrapper.InstanceId,
                CursorX = cursorX,
                CursorY = cursorY,
                StreamSerial = sessionState.StreamSerial,
            }
        }, sendLock, ct);
    }

    private async Task SendCursorStateAsync(
        WebSocket webSocket,
        DesktopSessionState sessionState,
        int cursorX,
        int cursorY,
        DesktopCursorShape? currentShape,
        SemaphoreSlim sendLock,
        CancellationToken ct)
    {
        var (screenWidth, screenHeight, desktopLeft, desktopTop) = _screenCapture.GetScreenSize();
        _desktopLeft = desktopLeft;
        _desktopTop = desktopTop;

        await SendMessageAsync(webSocket, new RemexMessage
        {
            Type = MessageTypes.DesktopCursorState,
            DesktopCursorState = new DesktopCursorState
            {
                CursorSerial = sessionState.NextCursorSerial(),
                StreamSerial = sessionState.StreamSerial,
                X = cursorX,
                Y = cursorY,
                Visible = cursorX >= desktopLeft &&
                          cursorY >= desktopTop &&
                          cursorX < desktopLeft + screenWidth &&
                          cursorY < desktopTop + screenHeight,
                ShapeSerial = currentShape?.ShapeSerial ?? 0,
                HotspotX = currentShape?.HotspotX ?? 0,
                HotspotY = currentShape?.HotspotY ?? 0,
            }
        }, sendLock, ct);
    }

    private async Task<DesktopCursorShape?> SyncCursorShapeAsync(
        WebSocket webSocket,
        DesktopSessionState sessionState,
        SemaphoreSlim sendLock,
        bool forceSend,
        CancellationToken ct)
    {
        if (!sessionState.UseCursorShape || !OperatingSystem.IsWindows())
        {
            return sessionState.GetCurrentCursorShape();
        }

        DesktopCursorShape? updatedShape = null;
        if (_screenCapture is WindowsScreenCaptureService windowsCapture &&
            windowsCapture.TryCaptureCurrentCursorShape(out var snapshot) &&
            snapshot is not null)
        {
            updatedShape = sessionState.UpdateCursorShape(snapshot);
        }

        var currentShape = updatedShape ?? sessionState.GetCurrentCursorShape();
        if (currentShape is not null && (forceSend || updatedShape is not null))
        {
            await SendMessageAsync(webSocket, new RemexMessage
            {
                Type = MessageTypes.DesktopCursorShape,
                DesktopCursorShape = currentShape,
            }, sendLock, ct);
        }

        return currentShape;
    }

    private async Task SendMessageAsync(WebSocket webSocket, RemexMessage message, SemaphoreSlim sendLock, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            await MessageSerializer.SendAsync(webSocket, message, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task SendBinaryAsync(WebSocket webSocket, byte[] payload, SemaphoreSlim sendLock, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            await webSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private IH264Encoder? TryCreateH264Encoder()
    {
        var (screenWidth, screenHeight, _, _) = _screenCapture.GetScreenSize();
        // Must use the same dimension function as the capture path (CaptureScaling.ScaledEven), or the
        // raw BGRA buffer size won't match the encoder's fixed -s WxH input and nvenc emits 0 frames.
        int targetWidth = Services.ScreenCapture.CaptureScaling.ScaledEven(screenWidth, _scale);
        int targetHeight = Services.ScreenCapture.CaptureScaling.ScaledEven(screenHeight, _scale);

        var encoder = new FFmpegH264Encoder(_logger);
        if (encoder.Initialize(targetWidth, targetHeight, _targetFps, 1500))
        {
            return encoder;
        }

        encoder.Dispose();
        _logger.LogWarning("Failed to initialize H.264 encoder for {Width}x{Height}. Falling back to MJPEG.", targetWidth, targetHeight);
        return null;
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
        public CapturedFrame? Frame;
    }

    private sealed class CapturedFrame
    {
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public long StreamSerial { get; init; }
        public long Sequence { get; init; }
        public DesktopFrameFlags Flags { get; init; }
        public DesktopCodecKind Codec { get; init; }
    }

    private sealed class DesktopSessionState
    {
        private readonly object _sync = new();
        private long _streamSerial;
        private long _frameSequence;
        private long _cursorSerial;
        private long _shapeSerial;
        private string _streamMappingId;
        private DesktopCursorShape? _currentCursorShape;

        public DesktopSessionState(DesktopClientCapabilities capabilities)
        {
            Capabilities = capabilities;
            _streamSerial = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _streamMappingId = Guid.NewGuid().ToString("N");
        }

        public DesktopClientCapabilities Capabilities { get; }

        public bool UseFrameEnvelope => Capabilities.SupportsFrameEnvelope;

        public bool SupportsTargetSwitch => Capabilities.SupportsTargetSwitch && Capabilities.SupportsFrameEnvelope;

        public bool UseCursorState => Capabilities.SupportsCursorState;

        public bool UseCursorShape => Capabilities.SupportsCursorShape;

        public long StreamSerial => Interlocked.Read(ref _streamSerial);

        public string StreamMappingId
        {
            get
            {
                lock (_sync)
                {
                    return _streamMappingId;
                }
            }
        }

        public long NextFrameSequence() => Interlocked.Increment(ref _frameSequence);

        public long NextCursorSerial() => Interlocked.Increment(ref _cursorSerial);

        public DesktopCursorShape? GetCurrentCursorShape()
        {
            lock (_sync)
            {
                return _currentCursorShape;
            }
        }

        public DesktopCursorShape? UpdateCursorShape(CursorShapeSnapshot snapshot)
        {
            lock (_sync)
            {
                if (_currentCursorShape is not null &&
                    _currentCursorShape.Width == snapshot.Width &&
                    _currentCursorShape.Height == snapshot.Height &&
                    _currentCursorShape.HotspotX == snapshot.HotspotX &&
                    _currentCursorShape.HotspotY == snapshot.HotspotY &&
                    _currentCursorShape.ShapeBytes.AsSpan().SequenceEqual(snapshot.ShapeBytes))
                {
                    return null;
                }

                _shapeSerial++;
                _currentCursorShape = new DesktopCursorShape
                {
                    ShapeSerial = _shapeSerial,
                    Width = snapshot.Width,
                    Height = snapshot.Height,
                    HotspotX = snapshot.HotspotX,
                    HotspotY = snapshot.HotspotY,
                    ShapeBytes = snapshot.ShapeBytes,
                };

                return _currentCursorShape;
            }
        }

        public void ResetStream()
        {
            lock (_sync)
            {
                var nextSerial = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nextSerial <= _streamSerial)
                {
                    nextSerial = _streamSerial + 1;
                }

                _streamSerial = nextSerial;
                _frameSequence = 0;
                _cursorSerial = 0;
                _streamMappingId = Guid.NewGuid().ToString("N");
            }
        }
    }
}
