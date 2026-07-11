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
using Remex.Core.Validation;
using Remex.Agent.Services;
using Remex.Agent;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.RemoteDesktop;
using Remex.Agent.Services.RemoteDesktop.Windows;
using Remex.Agent.Services.ScreenCapture;

namespace Remex.Agent.Handlers;

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

    // Upper bound on the target frame rate, shared with clients via DesktopConfig.MaxTargetFps so the
    // UI's "Unlimited" option and this host clamp agree on one number. 360 sits at or above the highest
    // consumer display refresh, and the encoder is throughput-bound below it at real resolutions, so it
    // is effectively uncapped — while the frame pacer stays safe (1000/360 ≈ 2.78 ms per tick, so a
    // static screen can never busy-spin the capture loop).
    private const int MaxTargetFps = DesktopConfig.MaxTargetFps;

    private int _quality = 50;
    private double _scale = 0.6;
    private int _targetFps = 120;
    private bool _drawCursor = true;

    // Phase 5 (RemEx-eo0f): when true, the capture loop lets AdaptiveScaleController drive _scale
    // instead of holding it fixed at the client-configured value. Recomputed in ApplyConfig from the
    // client's explicit DesktopConfig.AdaptiveScale, falling back to a default rule when omitted.
    private bool _adaptiveScaleEnabled;

    private DesktopCodecKind _negotiatedCodec = DesktopCodecKind.Mjpeg;
    private DesktopCodecKind _activeCodec = DesktopCodecKind.Mjpeg;
    private DesktopClientCapabilities _clientCapabilities = new();

    // The H.264 encoder currently driving the stream, published by the capture loop so the input
    // receive loop can route an on-demand keyframe request to it. Null when streaming MJPEG or between
    // encoder rebuilds. (RemEx-bqc)
    private volatile IH264Encoder? _activeH264Encoder;

    // Bumped whenever an encoder-affecting setting (quality/fps/scale) changes, so the capture loop
    // knows to rebuild the H.264 encoder mid-stream.
    private int _encoderConfigVersion;

    // Encoded dimensions last advertised to the client via DesktopMeta/DesktopStreamDescriptor.
    // Compared after each encoder rebuild so a scale change (preset switch, Phase 5 adaptive step)
    // triggers a fresh bootstrap re-send — that's what re-keys the client's decode surface.
    private int lastAdvertisedEncodedWidth;
    private int lastAdvertisedEncodedHeight;

    // Cached FFmpeg availability — probed once at handler construction, not per-stream-start
    private static bool? _ffmpegAvailableCache;
    private static readonly object _ffmpegCacheLock = new();

    private readonly Remex.Agent.Services.Session.IInteractiveSessionGuard _sessionGuard;

    public RemoteDesktopHandler(
        ILogger<RemoteDesktopHandler> logger,
        IScreenCaptureService screenCapture,
        IInputSimulationService inputSimulation,
        IDesktopWindowControlService windowControl,
        IHostCapabilitiesProvider hostCapabilitiesProvider,
        Remex.Agent.Services.Session.IInteractiveSessionGuard sessionGuard)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _inputSimulation = inputSimulation;
        _windowControl = windowControl;
        _hostCapabilitiesProvider = hostCapabilitiesProvider;
        _sessionGuard = sessionGuard;

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
        var sessionGuardClientId = Guid.NewGuid().ToString("N");
        var hostCapabilities = _hostCapabilitiesProvider.GetCurrent();

        if (!hostCapabilities.SupportsRemoteDesktop)
        {
            await SendDesktopErrorCoded(
                webSocket,
                DesktopErrorCodes.RuntimeUnavailable,
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
                                await SendDesktopErrorCoded(webSocket, DesktopErrorCodes.TargetUnavailable, targetError ?? "The requested desktop target is unavailable.", sendLock, ct);
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
                        _screenCapture.WarmUpCapture();
                        await SendCurrentStreamBootstrapAsync(webSocket, sessionState, sendLock, ct);

                        // Keep the signed-in session usable (unlocked) for the life of this stream when
                        // the user has opted in (off by default). Engages only while actively streaming;
                        // the guard ref-counts across clients and re-locks when the last one disconnects.
                        bool sessionGuardEngaged = false;
                        if (Remex.Agent.Services.Session.SessionGuardSettings.IsKeepUnlockedEnabled())
                        {
                            _sessionGuard.EngageForRemoteControl(sessionGuardClientId);
                            sessionGuardEngaged = true;
                        }

                        try
                        {
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
                        }
                        finally
                        {
                            if (sessionGuardEngaged)
                            {
                                _sessionGuard.Disengage(sessionGuardClientId);
                            }
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
        // Absolute-timeline frame pacer (hybrid coarse-sleep + spin). See PrecisionPacer: a bare
        // Task.Delay rounds up to the ~15.6 ms OS timer floor and would cap the stream near 60 FPS.
        var pacer = new PrecisionPacer();
        // Monotonic clock for the keyframe-reinit cooldown timestamps below (independent of pacing).
        var rebuildClock = Stopwatch.StartNew();

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
        int encoderConfigVersion = -1;

        // Phase 5 adaptive capture scale (RemEx-eo0f): the controller's lifetime is tied to the
        // active H.264 encoder INSTANCE, not the stream — a rebuild (target switch, config change, or
        // an adaptive step itself) creates a fresh encoder whose Accepted/DroppedAccessUnit counters
        // start at 0, so re-seeding the controller and the sampling baselines together on every
        // instance swap keeps "achieved FPS" measured cleanly within one encoder's lifetime.
        AdaptiveScaleController? adaptiveController = null;
        IH264Encoder? adaptiveTrackedEncoder = null;
        var adaptiveWindowStopwatch = Stopwatch.StartNew();
        long adaptiveBaselineAccepted = 0;
        long adaptiveBaselineDropped = 0;
        const double adaptiveWindowMs = 2000;

        // On-demand keyframe reinit throttle — a RARE backstop, not the primary resync path. Honoring a
        // keyframe request means a full ~1s ffmpeg/nvenc restart (a rawvideo stdin pipe has no per-frame
        // IDR API). The encoder's GOP scales with fps (FFmpegH264Encoder.ComputeGop, clamped 30-120),
        // so a desynced decoder resyncs on the next natural GOP keyframe (≤~1s) WITHOUT any reinit. A
        // desynced decoder also floods keyframe requests (its 4-frame input backlog
        // overflows on each post-reinit catch-up burst); honoring them reinitializes the encoder
        // continuously, which is the very stall that keeps the stream from ever smoothing out (a few FPS,
        // black). So we honor at most one keyframe-driven reinit per long cooldown: one fresh IDR plus the
        // frequent GOP keyframes resync the decoder, and once delivery is smooth the flood stops on its
        // own. Legitimate rebuilds (target switch, quality/fps/scale change) are unaffected.
        const double keyframeReinitCooldownMs = 5000;
        double lastEncoderRebuildMs = double.NegativeInfinity;
        int throttledKeyframeRequests = 0;

        // Self-healing H.264: a failed encoder rebuild (transient GPU/driver/portal churn during a
        // monitor switch or reconnect) no longer demotes the session to MJPEG permanently. The loop
        // streams MJPEG-tagged frames while the encoder is down and retries H.264 on this cooldown.
        // (RemEx-lq6h)
        const double h264RetryCooldownMs = 3000;
        double nextH264RetryMs = 0;

        // Start capture/encode loop in a separate task
        var captureTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                // Display powered off (monitor sleep): pause capture entirely so we never poke a
                // powered-off Desktop Duplication output (RemEx-960, defense-in-depth for RemEx-crk).
                // This is NOT a capture failure — clear any in-flight failure state so a sleeping
                // monitor never surfaces a misleading "capture stopped" error, and just idle until the
                // display powers back on (the stream stays connected and resumes automatically).
                if (_screenCapture.IsDisplayPoweredOff)
                {
                    consecutiveFailures = 0;
                    errorReported = false;
                    pacer.Reset();
                    try { await Task.Delay(500, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                captureStopwatch.Restart();
                try
                {
                    var captureSerial = sessionState.StreamSerial;
                    var configVersion = Volatile.Read(ref _encoderConfigVersion);
                    byte[]? frameBytes = null;
                    DesktopCodecKind frameCodec = _activeCodec;
                    var frameFlags = DesktopFrameFlags.None;

                    // On-demand IDR: a client whose decoder desynced asks for a keyframe. With a
                    // rawvideo stdin pipe there is no per-frame ffmpeg keyframe API, so the real
                    // mechanism is to reinitialize the encoder (fresh SPS/PPS + forced IDR). Consume
                    // the request here and force the rebuild path below. (RemEx-bqc)
                    if (_activeCodec == DesktopCodecKind.H264 &&
                        h264Encoder is not null && h264Encoder.ConsumeKeyframeRequest())
                    {
                        // Throttle: only rebuild for a keyframe request if we have not just rebuilt. A
                        // request that arrives inside the cooldown is swallowed (the decoder re-requests
                        // if it is still desynced; the first request after the cooldown is honored). This
                        // breaks the reinit storm that otherwise pins the stream at ~2 FPS / black.
                        if (rebuildClock.Elapsed.TotalMilliseconds - lastEncoderRebuildMs >= keyframeReinitCooldownMs)
                        {
                            encoderSerial = -1;
                        }
                        else
                        {
                            throttledKeyframeRequests++;
                        }
                    }

                    // Rebuild the encoder on a stream-serial change (target switch) OR when an
                    // encoder-affecting setting (quality/fps/scale) changed mid-stream, OR when an
                    // on-demand keyframe was requested (encoderSerial forced to -1 above), OR when a
                    // previous rebuild failed and the retry cooldown elapsed (self-healing recovery).
                    if (_activeCodec == DesktopCodecKind.H264 &&
                        (encoderSerial != captureSerial || encoderConfigVersion != configVersion ||
                         (h264Encoder is null && rebuildClock.Elapsed.TotalMilliseconds >= nextH264RetryMs)))
                    {
                        // Any rebuild (target switch, config change, or on-demand keyframe) emits a fresh
                        // IDR, so it also satisfies the keyframe cooldown window above.
                        lastEncoderRebuildMs = rebuildClock.Elapsed.TotalMilliseconds;
                        // Name the trigger so a reinit storm is diagnosable from the log: encoderSerial==-1
                        // means an on-demand keyframe forced it; a differing real serial is a target switch;
                        // otherwise an encoder-affecting config change (quality/fps/scale).
                        _logger.LogInformation(
                            "Rebuilding H.264 encoder (reason={Reason}, encoderSerial={EncSerial}, captureSerial={CapSerial}, encoderCfg={EncCfg}, cfg={Cfg}).",
                            encoderSerial == -1 ? "keyframe" : encoderSerial != captureSerial ? "target-switch" : "config-change",
                            encoderSerial, captureSerial, encoderConfigVersion, configVersion);
                        _activeH264Encoder = null;
                        h264Encoder?.Dispose();
                        h264Encoder = TryCreateH264Encoder();
                        encoderSerial = captureSerial;
                        encoderConfigVersion = configVersion;
                        if (h264Encoder is null)
                        {
                            if (sessionState.UseFrameEnvelope)
                            {
                                // Do NOT demote _activeCodec: a single failed rebuild used to lock the
                                // whole session to MJPEG (12.5 FPS full-desktop CPU encode) with no way
                                // back. Keep the negotiated codec, serve MJPEG-tagged frames meanwhile,
                                // and retry after the cooldown. Safe because envelope-capable clients
                                // route every frame by its own codec tag. (RemEx-lq6h)
                                nextH264RetryMs = rebuildClock.Elapsed.TotalMilliseconds + h264RetryCooldownMs;
                                _logger.LogWarning(
                                    "H.264 encoder unavailable after rebuild; streaming MJPEG frames and retrying H.264 in {CooldownMs} ms.",
                                    (int)h264RetryCooldownMs);
                            }
                            else
                            {
                                // Envelope-less legacy client: it routes frames by the negotiated codec,
                                // so a mixed H.264/MJPEG stream would misroute. Keep the old permanent
                                // fallback for these sessions.
                                frameCodec = DesktopCodecKind.Mjpeg;
                                _activeCodec = DesktopCodecKind.Mjpeg;
                            }
                        }
                        else
                        {
                            // Publish the live encoder so the receive loop can request on-demand IDRs.
                            _activeH264Encoder = h264Encoder;
                            frameCodec = DesktopCodecKind.H264;
                            frameFlags = DesktopFrameFlags.KeyFrame;

                            // A scale change (preset switch mid-stream, Phase 5 adaptive step) alters the
                            // encoded output size without a stream-serial bump. Re-advertise so the client
                            // re-keys its decode surface at the new dimensions (Phase 2 frozen-surface fix).
                            var (curScreenWidth, curScreenHeight, _, _) = _screenCapture.GetScreenSize();
                            var (newEncodedWidth, newEncodedHeight) = GetEncodedFrameSize(curScreenWidth, curScreenHeight);
                            if (newEncodedWidth != lastAdvertisedEncodedWidth || newEncodedHeight != lastAdvertisedEncodedHeight)
                            {
                                _logger.LogInformation(
                                    "Encoded size changed {OldW}x{OldH} -> {NewW}x{NewH}; re-advertising stream bootstrap.",
                                    lastAdvertisedEncodedWidth, lastAdvertisedEncodedHeight, newEncodedWidth, newEncodedHeight);
                                await SendCurrentStreamBootstrapAsync(webSocket, sessionState, sendLock, ct);
                            }
                        }
                    }

                    // Phase 5 adaptive capture scale (RemEx-eo0f): evaluate roughly every 2s while H.264
                    // is active. A rung change here bumps _encoderConfigVersion, which the rebuild block
                    // above picks up on the NEXT iteration (not this one) — same path as a manual
                    // quality/fps/scale change from the client.
                    if (_adaptiveScaleEnabled && _activeCodec == DesktopCodecKind.H264 && h264Encoder is not null)
                    {
                        if (!ReferenceEquals(h264Encoder, adaptiveTrackedEncoder))
                        {
                            // New encoder instance (rebuild for any reason): its counters start at 0, so
                            // re-seed the controller from the scale that produced this instance and reset
                            // the sampling baselines to match.
                            adaptiveTrackedEncoder = h264Encoder;
                            adaptiveController = new AdaptiveScaleController(_scale);
                            adaptiveBaselineAccepted = h264Encoder.AcceptedInputFrameCount;
                            adaptiveBaselineDropped = h264Encoder.DroppedAccessUnitCount;
                            adaptiveWindowStopwatch.Restart();
                        }
                        else if (adaptiveController is not null &&
                                 adaptiveWindowStopwatch.Elapsed.TotalMilliseconds >= adaptiveWindowMs)
                        {
                            var elapsedSec = adaptiveWindowStopwatch.Elapsed.TotalSeconds;
                            var acceptedNow = h264Encoder.AcceptedInputFrameCount;
                            var droppedNow = h264Encoder.DroppedAccessUnitCount;
                            var achievedFps = (acceptedNow - adaptiveBaselineAccepted) / elapsedSec;
                            var outputOverflowed = droppedNow > adaptiveBaselineDropped;
                            adaptiveBaselineAccepted = acceptedNow;
                            adaptiveBaselineDropped = droppedNow;
                            adaptiveWindowStopwatch.Restart();

                            var decision = adaptiveController.Report(_targetFps, achievedFps, outputOverflowed, DateTime.UtcNow);
                            if (decision is not null)
                            {
                                _logger.LogInformation(
                                    "Adaptive scale: {Old:F4} -> {New:F4} ({Reason}); achieved {Fps:F0}/{Target} fps.",
                                    _scale, decision.Scale, decision.Reason, achievedFps, _targetFps);
                                _scale = decision.Scale;
                                Interlocked.Increment(ref _encoderConfigVersion);
                            }
                        }
                    }
                    else if (adaptiveTrackedEncoder is not null)
                    {
                        // Adaptive scale turned off or codec left H.264: drop tracking so a later
                        // re-enable starts a fresh controller instead of resuming stale state.
                        adaptiveController = null;
                        adaptiveTrackedEncoder = null;
                    }

                    // Tracks whether the SCREEN CAPTURE step produced pixels this iteration. This is
                    // distinct from whether we got an encodable frame: the H.264 encoder pipelines and
                    // returns nothing for its first ~0.5–1s (warmup) even though capture is healthy.
                    // Counting that warmup as a capture failure previously tripped the consecutive-failure
                    // backoff, which throttled the encoder feed and permanently starved it (black screen).
                    bool captureSucceeded = false;

                    if (_activeCodec == DesktopCodecKind.H264 && h264Encoder is not null)
                    {
                        bool forceKeyframe = frameFlags.HasFlag(DesktopFrameFlags.KeyFrame) || (totalFramesCaptured % 60 == 0);
                        // Capture at the encoder's (possibly hardware-capped) scale so the raw buffer
                        // size matches the encoder's fixed -s WxH input exactly.
                        var rawResult = await _screenCapture.CaptureRawScreenLiveAsync(_h264CaptureScale, _drawCursor, ct);
                        if (captureSerial != sessionState.StreamSerial)
                            continue;

                        var rawPixels = rawResult.Pixels;
                        // A stale cached replay (IsLive == false) means the capture output froze
                        // (DXGI_ERROR_ACCESS_LOST / session disconnect). Skip it so the freeze advances
                        // consecutiveFailures and trips the coded desktop_error, instead of silently
                        // re-encoding and re-sending the last good frame forever (RemEx-ltd).
                        if (rawPixels is { Length: > 0 } && rawResult.IsLive)
                        {
                            captureSucceeded = true;

                            // Self-heal: if the captured buffer size no longer matches the encoder's
                            // fixed input size (capture backend/DPI/geometry drifted since the encoder
                            // was created), feeding it would desync the rawvideo pipe. Reinit instead.
                            if (rawPixels.Length != h264Encoder.ExpectedInputByteCount)
                            {
                                _logger.LogWarning(
                                    "H.264 raw frame size {Actual} != encoder input size {Expected}; reinitializing encoder.",
                                    rawPixels.Length, h264Encoder.ExpectedInputByteCount);
                                encoderSerial = -1; // force recreate on next iteration
                            }
                            else
                            {
                                frameBytes = h264Encoder.EncodeFrame(rawPixels, forceKeyframe);
                                if (frameBytes is { Length: > 0 })
                                    frameFlags = forceKeyframe ? DesktopFrameFlags.KeyFrame : DesktopFrameFlags.None;
                                // else: encoder warmup/pipelining — frameBytes stays null. NOT a failure.
                            }
                        }
                    }
                    else
                    {
                        // This branch also serves an H.264 session whose encoder is temporarily down
                        // (self-healing recovery window). Tag these frames as MJPEG so the client's
                        // per-frame codec routing feeds them to the JPEG decoder, not MediaCodec.
                        frameCodec = DesktopCodecKind.Mjpeg;
                        var jpegResult = await _screenCapture.CaptureScreenLiveAsync(_quality, _scale, _drawCursor, ct);
                        if (captureSerial != sessionState.StreamSerial)
                            continue;
                        if (jpegResult.IsLive)
                        {
                            frameBytes = jpegResult.Pixels;
                            captureSucceeded = frameBytes is { Length: > 0 };
                        }
                        // else: stale cached replay (IsLive == false) — leave frameBytes null and
                        // captureSucceeded false so a sustained freeze advances consecutiveFailures and
                        // trips the coded desktop_error instead of re-sending the last good frame (RemEx-ltd).
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
                    else if (!captureSucceeded)
                    {
                        // Genuine capture failure (DXGI/GDI returned nothing). Encoder-warmup nulls are
                        // excluded so they never trip the capture-failure backoff that starves the feed.
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
                    var windowsReport = _hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport();
                    if (windowsReport is not null)
                    {
                        // Rich, host-specific Windows diagnostic — keep as plain text (untranslated).
                        await SendDesktopError(webSocket, BuildWindowsCaptureFailureMessage(windowsReport, totalFramesSent), sendLock, ct);
                    }
                    else if (totalFramesSent == 0)
                    {
                        await SendDesktopErrorCoded(webSocket, DesktopErrorCodes.CaptureUnavailable,
                            "Screen capture is not working on the host.", sendLock, ct);
                    }
                    else
                    {
                        await SendDesktopErrorCoded(webSocket, DesktopErrorCodes.CaptureStopped,
                            $"Screen capture stopped working after {totalFramesSent} frames. The host desktop may have been locked or the session disconnected.",
                            sendLock, ct, arg: totalFramesSent.ToString());
                    }
                }

                // Throttle. While capture is failing (locked desktop, lost session, DXGI recovering),
                // back off well below the target FPS so we don't spin the CPU or hammer the failing
                // capture path 30×/sec. Recovers immediately: consecutiveFailures resets to 0 on the
                // first successful frame, restoring the normal FPS cadence.
                if (consecutiveFailures >= 5)
                {
                    // Reset the absolute timeline so the loop doesn't burst through a backlog
                    // of "missed" ticks when capture recovers.
                    pacer.Reset();
                    try { await Task.Delay(500, ct); }
                    catch (OperationCanceledException) { break; }
                }
                else
                {
                    // Hybrid precision wait — see PrecisionPacer (do NOT replace with a bare
                    // Task.Delay; that caps the stream near 60 FPS via the ~15.6 ms OS timer floor).
                    if (!await pacer.WaitForNextTickAsync(1000.0 / _targetFps, ct)) break;
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

                // Drop stale frames left over from a previous stream target. The capture loop can
                // Interlocked.Exchange an old-serial frame into the buffer just after a target switch
                // clears it; the host is authoritative on StreamSerial, so never ship a frame that no
                // longer matches the current stream (otherwise a client without frame envelopes — which
                // cannot fence on the serial itself — briefly renders the old target). (RemEx-gim)
                if (currentFrame.StreamSerial != sessionState.StreamSerial)
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
                        "Dropped {Drop} frames. Avg Capture+Encode: {AvgCap:F1}ms, Avg Send: {AvgSend:F1}ms. " +
                        "Throttled keyframe reinits: {Throttled}.",
                        totalFramesSent, effectiveFps, totalFramesCaptured, totalFramesDropped, avgCaptureMs, avgSendMs,
                        throttledKeyframeRequests);

                    totalFramesSent = 0;
                    totalFramesCaptured = 0;
                    totalFramesDropped = 0;
                    totalCaptureMs = 0;
                    totalSendMs = 0;
                    throttledKeyframeRequests = 0;
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
            _activeH264Encoder = null;
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

    /// <summary>
    /// Sends a desktop error tagged with a stable <see cref="DesktopErrorCodes"/> code so the client
    /// can render a localized message, while <paramref name="englishFallback"/> remains the readable
    /// text for clients that do not parse codes. <paramref name="arg"/> carries an optional parameter
    /// (e.g. a frame count) for the client's localized template. (RemEx-728)
    /// </summary>
    private Task SendDesktopErrorCoded(
        WebSocket webSocket,
        string code,
        string englishFallback,
        SemaphoreSlim sendLock,
        CancellationToken ct,
        string? arg = null)
        => SendDesktopError(webSocket, DesktopErrorCodes.Format(code, englishFallback, arg), sendLock, ct);

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
        var lastCursorHandle = IntPtr.Zero;
        var tick = 0;
        // ~90 Hz cursor pacing via the shared precision pacer. Task.Delay(11) rounds up to the
        // ~15.6 ms OS timer floor (~64 Hz, missing the 90 Hz target) — see PrecisionPacer. (RD-A)
        var pacer = new PrecisionPacer();
        const double cursorIntervalMs = 1000.0 / 90.0;

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (cursorX, cursorY) = _inputSimulation.GetCursorPosition();

                // Cursor POSITION streams at ~90 Hz for smooth on-screen tracking; the ClipCursor
                // confinement changes rarely, so re-apply that only at ~10 Hz (every 9th tick) to
                // avoid hammering the OS at the higher position rate.
                var slowTick = tick % 9 == 0;

                if (slowTick)
                {
                    // Keep the cursor confined to the streamed display so it can't roam onto a monitor
                    // the remote user can't see. Re-applied periodically because Windows releases the
                    // clip on display/desktop/foreground changes. No-op when streaming the full desktop.
                    var (clipW, clipH, clipLeft, clipTop) = _screenCapture.GetScreenSize();
                    _inputSimulation.ConfineCursorToRegion(clipLeft, clipTop, clipW, clipH);
                }

                var currentShape = sessionState.GetCurrentCursorShape();
                if (sessionState.UseCursorShape && OperatingSystem.IsWindows())
                {
                    // Cheap hCursor poll on every 90Hz tick: a shape CHANGE (e.g. arrow -> I-beam) is
                    // detected in <=11ms instead of waiting for the 10Hz slow tick (<=111ms). The full
                    // capture (SyncCursorShapeAsync — bitmap render + byte-compare dedupe) still only
                    // runs when the handle actually differs, so this doesn't add per-tick capture cost.
                    // The slow tick below still runs unconditionally: animated cursors mutate pixels
                    // without changing hCursor, and UpdateCursorShape's own byte-compare dedupe (not
                    // this handle check) is what suppresses redundant sends for those. (Phase 4)
                    var handleChanged = WindowsCursorShapeCapture.TryGetCursorHandle(out var cursorHandle) &&
                        cursorHandle != lastCursorHandle;
                    if (handleChanged || slowTick)
                    {
                        currentShape = await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: false, ct);
                        lastCursorHandle = cursorHandle;
                    }
                }

                // Only send update if cursor moved (reduce network traffic)
                var currentShapeSerial = currentShape?.ShapeSerial ?? 0;
                if (cursorX != lastX || cursorY != lastY || currentShapeSerial != lastShapeSerial)
                {
                    try
                    {
                        // Route through SendCursorUpdateAsync so it honors client capabilities:
                        // cursor-state clients get a desktop_cursor_state message; legacy clients
                        // (e.g. Android, which doesn't consume cursor_state) get a DesktopMeta carrying
                        // cursorX/cursorY so their cursor overlay actually tracks the host pointer.
                        await SendCursorUpdateAsync(webSocket, sessionState, cursorX, cursorY, sendLock, ct);
                        lastX = cursorX;
                        lastY = cursorY;
                        lastShapeSerial = currentShapeSerial;
                    }
                    catch (WebSocketException)
                    {
                        break;
                    }
                }

                tick++;
                // ~90 Hz cursor position for smooth tracking (shape + clip throttled to ~10 Hz above).
                // Precise pacing (not Task.Delay) — Task.Delay(11) oversleeps to ~15.6 ms / ~64 Hz.
                if (!await pacer.WaitForNextTickAsync(cursorIntervalMs, ct)) break;
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cursor position streaming error.");
        }
        finally
        {
            // Always release the cursor clip when streaming stops, cancels, or the client disconnects.
            _inputSimulation.ReleaseCursorConfinement();
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

                    // On-demand keyframe request from a client whose H.264 decoder desynced (dropped
                    // frames / output-format change). Additive + backward-compatible: legacy hosts
                    // simply don't recognize the type and ignore it. Uses the shared typed Core
                    // constant so host and client agree on the wire string. (RemEx-bqc / RemEx-kjq)
                    case MessageTypes.DesktopKeyframeRequest:
                        _activeH264Encoder?.RequestKeyframe();
                        break;

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
                // Absolute moves/clicks: the Android client already maps touch/stylus points into
                // absolute virtual-desktop coordinates (mapLocalToHost adds the host-reported
                // desktopLeft/Top before sending). Do NOT re-add the desktop offset here — that
                // double-applies it and, on a secondary monitor with a non-zero origin,
                // drives the cursor off-screen so SendInput clamps it to the desktop edge. On the
                // primary monitor the origin is (0,0), which is why the bug was invisible there.
                case InputEventTypes.MouseMove when input.X.HasValue && input.Y.HasValue:
                {
                    var (mx, my) = ClampToActiveBounds(input.X.Value, input.Y.Value);
                    _inputSimulation.MoveMouse(mx, my);
                    break;
                }
                case InputEventTypes.MouseMove when input.DeltaX.HasValue || input.DeltaY.HasValue:
                    _inputSimulation.MouseMoveRelative(input.DeltaX ?? 0, input.DeltaY ?? 0);
                    break;
                case InputEventTypes.MouseDown when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                    {
                        var (dx, dy) = ClampToActiveBounds(input.X.Value, input.Y.Value);
                        _inputSimulation.MoveMouse(dx, dy);
                    }
                    _inputSimulation.MouseDown(input.Button.Value);
                    break;
                case InputEventTypes.MouseUp when input.Button.HasValue:
                    _inputSimulation.MouseUp(input.Button.Value);
                    break;
                case InputEventTypes.MouseClick when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                    {
                        var (cx, cy) = ClampToActiveBounds(input.X.Value, input.Y.Value);
                        _inputSimulation.MoveMouse(cx, cy);
                    }
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

    /// <summary>
    /// Clamps an absolute pointer target into the bounds of the active capture region, so a client
    /// coordinate that overshoots the streamed display (fast flicks, rounding at the edges) can
    /// never drive the host cursor onto a monitor the remote user is not viewing — and, on Linux,
    /// can never desync the portal's relative-motion tracker. (RemEx-lq6h)
    /// </summary>
    private (int X, int Y) ClampToActiveBounds(int x, int y)
    {
        var (width, height, left, top) = _screenCapture.GetScreenSize();
        if (width <= 0 || height <= 0)
        {
            return (x, y);
        }

        return (Math.Clamp(x, left, left + width - 1), Math.Clamp(y, top, top + height - 1));
    }

    private void ApplyConfig(DesktopConfig config)
    {
        if (config.ClientCapabilities is not null)
        {
            _clientCapabilities = config.ClientCapabilities;
        }

        int oldQuality = _quality, oldFps = _targetFps;
        double oldScale = _scale;

        _quality = Math.Clamp(config.Quality, 1, 100);
        _scale = Math.Clamp(config.Scale, 0.25, 1.0);
        _targetFps = Math.Clamp(config.TargetFps, 1, MaxTargetFps);
        // Cursor compositing strategy. When the client advertises SupportsCursorShape it renders the
        // real native cursor itself from the streamed desktop_cursor_shape (pixel bitmap) + the live
        // desktop_cursor_state (position), so the host must NOT also paint the cursor into the frame.
        // Host compositing freezes the cursor on mouse-only frames (the capture only re-encodes when
        // the desktop pixels change) and would double the cursor on top of the client's own draw.
        // Host-side compositing remains the fallback for legacy clients that cannot render the shape.
        _drawCursor = config.DrawCursor && !_clientCapabilities.SupportsCursorShape;
        _negotiatedCodec = config.Codec;

        // Explicit client opt-in/out always wins. Otherwise default on for H.264 at high frame rates,
        // where a fixed preset scale most likely leaves sharpness on the table on capable hardware.
        _adaptiveScaleEnabled = config.AdaptiveScale ?? (_targetFps >= 90 && _negotiatedCodec == DesktopCodecKind.H264);

        // If a setting that affects the H.264 encoder changed mid-stream, bump the config version so
        // the capture loop rebuilds the encoder (the encoder is built once with fixed qp/fps/size and
        // otherwise never picks up slider changes — which is why the quality slider appeared dead).
        if (_quality != oldQuality || _targetFps != oldFps || Math.Abs(_scale - oldScale) > 0.0001)
        {
            Interlocked.Increment(ref _encoderConfigVersion);
        }

        _logger.LogDebug("Desktop config updated: quality={Q}, scale={S}, fps={F}, drawCursor={D}, codec={C}, adaptiveScale={A}", _quality, _scale, _targetFps, _drawCursor, _negotiatedCodec, _adaptiveScaleEnabled);
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
            await SendDesktopErrorCoded(webSocket, DesktopErrorCodes.TargetSwitchUnsupported, "This client session does not support in-session display switching.", sendLock, ct);
            return;
        }

        if (!TryApplyCaptureTarget(request.Target, request.DisplayListVersion, out var error))
        {
            await SendDesktopErrorCoded(webSocket, DesktopErrorCodes.TargetUnavailable, error ?? "The requested desktop target is unavailable.", sendLock, ct);
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
        var (cursorX, cursorY) = _inputSimulation.GetCursorPosition();
        var (encodedWidth, encodedHeight) = GetEncodedFrameSize(screenWidth, screenHeight);
        lastAdvertisedEncodedWidth = encodedWidth;
        lastAdvertisedEncodedHeight = encodedHeight;

        // Bootstrap dimensions now come from the backend that actually serves the target, primed
        // before this point (RemEx-4k4). Log them so a mis-framed first connect can be diagnosed
        // from the host log alone (expected: the selected monitor's real size + correct backend).
        _logger.LogInformation(
            "RD bootstrap: streaming {Width}x{Height} @ ({Left},{Top}) via {Backend}; encoded={EncW}x{EncH}.",
            screenWidth, screenHeight, desktopLeft, desktopTop, _screenCapture.BackendName, encodedWidth, encodedHeight);

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
                CursorVisible = !_drawCursor && IsCursorInRegion(cursorX, cursorY, screenWidth, screenHeight, desktopLeft, desktopTop),
                CaptureBackend = _screenCapture.BackendName,
                InputBackend = _inputSimulation.BackendName,
                StreamMappingId = sessionState.StreamMappingId,
                LogicalWidth = screenWidth,
                LogicalHeight = screenHeight,
                PixelWidth = screenWidth,
                PixelHeight = screenHeight,
                EncodedWidth = encodedWidth,
                EncodedHeight = encodedHeight,
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
                EncodedWidth = encodedWidth,
                EncodedHeight = encodedHeight,
            }
        }, sendLock, ct);

        if (sessionState.UseBinaryCursor)
        {
            var bootShape = await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: true, ct);
            await SendCursorBinaryAsync(webSocket, sessionState, cursorX, cursorY, bootShape, sendLock, ct);
        }
        else if (sessionState.UseCursorState)
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
        if (sessionState.UseBinaryCursor)
        {
            // RD-E hot path: pack the position into a 32-byte binary packet (no JSON/GC). Shape stays JSON.
            var binShape = sessionState.UseCursorShape
                ? await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: false, ct)
                : null;
            await SendCursorBinaryAsync(webSocket, sessionState, cursorX, cursorY, binShape, sendLock, ct);
            return;
        }

        if (sessionState.UseCursorState)
        {
            var currentShape = await SyncCursorShapeAsync(webSocket, sessionState, sendLock, forceSend: false, ct);
            await SendCursorStateAsync(webSocket, sessionState, cursorX, cursorY, currentShape, sendLock, ct);
            return;
        }

        var (screenWidth, screenHeight, desktopLeft, desktopTop) = _screenCapture.GetScreenSize();
        var (encodedWidth, encodedHeight) = GetEncodedFrameSize(screenWidth, screenHeight);

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
                CursorVisible = !_drawCursor && IsCursorInRegion(cursorX, cursorY, screenWidth, screenHeight, desktopLeft, desktopTop),
                // Carry the real resolution on lightweight cursor-position updates too. These ints are
                // non-nullable and otherwise serialize as 0; a client that keys its decoder on
                // PixelWidth/PixelHeight (e.g. the Android H.264 view) would rebuild with a 0x0 surface.
                LogicalWidth = screenWidth,
                LogicalHeight = screenHeight,
                PixelWidth = screenWidth,
                PixelHeight = screenHeight,
                EncodedWidth = encodedWidth,
                EncodedHeight = encodedHeight,
                StreamSerial = sessionState.StreamSerial,
            }
        }, sendLock, ct);
    }

    private async Task SendCursorBinaryAsync(
        WebSocket webSocket,
        DesktopSessionState sessionState,
        int cursorX,
        int cursorY,
        DesktopCursorShape? currentShape,
        SemaphoreSlim sendLock,
        CancellationToken ct)
    {
        var (screenWidth, screenHeight, desktopLeft, desktopTop) = _screenCapture.GetScreenSize();
        bool visible = IsCursorInRegion(cursorX, cursorY, screenWidth, screenHeight, desktopLeft, desktopTop);
        // RD-E: 32-byte binary position packet over the desktop binary channel (no JSON/GC). The shape
        // itself is still delivered as JSON; we only carry its serial so the client shows the right cached
        // bitmap. Demuxed from H.264 frames by the "RDXC" magic. See DesktopCursorBinaryEnvelope.
        var payload = DesktopCursorBinaryEnvelope.Write(
            cursorX, cursorY, visible, currentShape?.ShapeSerial ?? 0, sessionState.StreamSerial);
        await SendBinaryAsync(webSocket, payload, sendLock, ct);
    }

    /// <summary>
    /// True when the absolute cursor position lies within the active capture surface bounds.
    /// Used so the client can hide its cursor overlay when the pointer is on a monitor that
    /// isn't part of the current capture target (single-monitor capture).
    /// </summary>
    private static bool IsCursorInRegion(int cursorX, int cursorY, int screenWidth, int screenHeight, int desktopLeft, int desktopTop)
        => cursorX >= desktopLeft && cursorX < desktopLeft + screenWidth &&
           cursorY >= desktopTop && cursorY < desktopTop + screenHeight;

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
        // forceRefresh:true so a cursor-shape change is detected even over a static screen (the main
        // frame loop, which otherwise refreshes the DXGI pointer shape, doesn't run when nothing on
        // screen changes). This is the ~10Hz cursor-sync tick, so the extra refresh is cheap. (RemEx-aae)
        if (_screenCapture is WindowsScreenCaptureService windowsCapture &&
            windowsCapture.TryCaptureCurrentCursorShape(out var snapshot, forceRefresh: true) &&
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

    // Hardware H.264 encoders cap at 4096x4096 — NVENC enforces this on EVERY NVIDIA GPU (it's an
    // H.264-codec limit, not a generational one; HEVC/AV1 go higher but H.264 never does), and most
    // QSV/AMF parts are similar. A combined multi-monitor capture surface easily exceeds 4096 wide,
    // which makes the encoder fail to open. We downscale the encoded surface to fit and reuse the
    // exact same scale for capture so the raw BGRA buffer still matches the encoder's -s WxH input.
    private const int MaxH264EncodeDimension = 4096;
    private double _h264CaptureScale = 1.0;

    private static double ClampScaleForH264(int screenWidth, int screenHeight, double requestedScale)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return requestedScale;
        var cap = Math.Min(
            (double)MaxH264EncodeDimension / screenWidth,
            (double)MaxH264EncodeDimension / screenHeight);
        return cap < requestedScale ? cap : requestedScale;
    }

    /// <summary>
    /// Computes the actual encoded frame size for the active codec: H.264 clamps to the hardware
    /// 4096px limit via <see cref="ClampScaleForH264"/> before even-alignment; MJPEG uses the raw
    /// configured scale. Used to populate DesktopMeta/DesktopStreamDescriptor.Encoded{Width,Height}
    /// so the client can size its decode surface at the true output resolution instead of the
    /// unscaled screen size.
    /// </summary>
    private (int Width, int Height) GetEncodedFrameSize(int screenWidth, int screenHeight)
    {
        var scale = _activeCodec == DesktopCodecKind.H264
            ? ClampScaleForH264(screenWidth, screenHeight, _scale)
            : _scale;
        return (CaptureScaling.ScaledEven(screenWidth, scale), CaptureScaling.ScaledEven(screenHeight, scale));
    }

    private IH264Encoder? TryCreateH264Encoder()
    {
        var (screenWidth, screenHeight, _, _) = _screenCapture.GetScreenSize();

        // Fit the encoded surface within the hardware H.264 limit, then capture at that same scale so
        // the raw buffer size matches the encoder's fixed -s WxH input (CaptureScaling.ScaledEven on
        // the identical scale guarantees this; a 1-pixel mismatch makes nvenc emit 0 frames).
        var scale = ClampScaleForH264(screenWidth, screenHeight, _scale);
        _h264CaptureScale = scale;
        if (scale < _scale)
        {
            _logger.LogInformation(
                "Capture surface {W}x{H} exceeds the {Max}px H.264 encoder limit; downscaling to scale {Scale:F4} (from {Requested:F4}) for hardware encoding.",
                screenWidth, screenHeight, MaxH264EncodeDimension, scale, _scale);
        }

        int targetWidth = CaptureScaling.ScaledEven(screenWidth, scale);
        int targetHeight = CaptureScaling.ScaledEven(screenHeight, scale);

        var encoder = new FFmpegH264Encoder(_logger);
        if (encoder.Initialize(targetWidth, targetHeight, _targetFps, QualityToQp(_quality)))
        {
            return encoder;
        }

        encoder.Dispose();
        _logger.LogWarning("Failed to initialize H.264 encoder for {Width}x{Height}. Falling back to MJPEG.", targetWidth, targetHeight);
        return null;
    }

    /// <summary>
    /// Maps the 1–100 quality slider to an H.264 constant-QP value. Higher quality → lower QP
    /// (better picture, more bandwidth). quality 100 → QP 16, quality 1 → QP 38.
    /// </summary>
    private static int QualityToQp(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        return (int)Math.Round(38 - (quality - 1) / 99.0 * 22);
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

        // The sample's coordinates/deltas are untrusted floats from the network client and can be
        // NaN/±Infinity or wildly out of range. Clamp against the active stream pixel bounds before
        // any (int) cast so a hostile sample cannot wrap into an arbitrary MoveMouse coordinate
        // (RD-8 / RemEx-q6u). GetScreenSize() reads cached DXGI dimensions, so this is cheap.
        // RD-D: capture desktopLeft/desktopTop too — a monitor left of/above primary has a NEGATIVE
        // origin, so clamp to [origin, origin+size), not [0, size). Flooring at 0 stranded the cursor
        // at the left edge and made the second (left) display unreachable via S-Pen hover.
        var (screenWidth, screenHeight, desktopLeft, desktopTop) = _screenCapture.GetScreenSize();

        switch (sample.Phase)
        {
            case PointerPhase.HoverMove:
            case PointerPhase.ContactMove:
                // Prefer an explicit relative delta when present (trackpad/relative mode); otherwise
                // treat the sample as absolute and use the logical host coordinates directly —
                // INCLUDING a legitimate (0,0) top-left position. The previous
                // `LogicalX != 0 || LogicalY != 0` guard treated absolute (0,0) as "no data" and fell
                // through, so a finger drag that mapped to the top-left corner (or any sample whose
                // host coords rounded to 0,0) silently failed to move the cursor. (RemEx-ubm)
                if (sample.Dx != 0 || sample.Dy != 0)
                {
                    input = new InputEvent
                    {
                        EventType = InputEventTypes.MouseMove,
                        DeltaX = CoordinateValidation.ClampDelta(sample.Dx, screenWidth),
                        DeltaY = CoordinateValidation.ClampDelta(sample.Dy, screenHeight),
                    };
                }
                else
                {
                    input = new InputEvent
                    {
                        EventType = InputEventTypes.MouseMove,
                        X = CoordinateValidation.ClampToRange(sample.LogicalX, desktopLeft, desktopLeft + screenWidth),
                        Y = CoordinateValidation.ClampToRange(sample.LogicalY, desktopTop, desktopTop + screenHeight),
                    };
                }
                break;

            case PointerPhase.ContactStart:
                // Contact samples from the client are absolute; anchor the press at the logical
                // position (including 0,0) instead of dropping the coordinate to "current cursor
                // position", which mis-placed presses near the top-left edge. (RemEx-ubm)
                input = new InputEvent
                {
                    EventType = InputEventTypes.MouseDown,
                    Button = 0,
                    X = CoordinateValidation.ClampToRange(sample.LogicalX, desktopLeft, desktopLeft + screenWidth),
                    Y = CoordinateValidation.ClampToRange(sample.LogicalY, desktopTop, desktopTop + screenHeight),
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

        // RD-E: binary cursor-position packets ("RDXC") instead of JSON desktop_cursor_state.
        public bool UseBinaryCursor => Capabilities.SupportsBinaryCursor;

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
