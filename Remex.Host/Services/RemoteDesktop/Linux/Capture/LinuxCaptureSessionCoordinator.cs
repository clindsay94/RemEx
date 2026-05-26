using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Host.Services.RemoteDesktop.Linux;
using Remex.Host.Services.RemoteDesktop.Linux.Portal;

namespace Remex.Host.Services.RemoteDesktop.Linux.Capture;

/// <summary>
/// Orchestrates the full Linux screen-capture pipeline:
///
///   xdg-desktop-portal ScreenCast session
///     → PipeWire node IDs
///       → <see cref="LinuxPipeWireFrameSource"/>
///         → Channel{LinuxFrameSnapshot} (latest-frame semantics)
///           → callers via <see cref="TryReadLatestFrame"/>
///
/// The coordinator owns the portal session lifecycle and handles automatic
/// restart on monitor hotplug or stream identity change events.
///
/// Thread safety: all public methods are safe to call from any thread.
///                The capture loop runs on a dedicated thread-pool task.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCaptureSessionCoordinator : IAsyncDisposable
{
    private readonly LinuxPortalRemoteDesktopSessionService _portal;
    private readonly ILogger<LinuxCaptureSessionCoordinator> _logger;

    private LinuxPipeWireFrameSource? _frameSource;
    private CancellationTokenSource? _captureCts;
    private Task? _captureLoop;

    // Single-slot channel: only the most recent frame is kept.
    // Older frames are dropped to prevent queue growth under encode lag.
    private readonly LinuxFrameChannel _frameChannel = new();

    public bool IsRunning { get; private set; }

    public LinuxCaptureSessionCoordinator(
        LinuxPortalRemoteDesktopSessionService portal,
        ILogger<LinuxCaptureSessionCoordinator>? logger = null)
    {
        _portal = portal;
        _logger = logger ?? NullLogger<LinuxCaptureSessionCoordinator>.Instance;

        _portal.SessionLost += OnPortalSessionLost;
    }

    /// <summary>
    /// Starts the portal session and begins the PipeWire capture loop.
    /// Must be called once before reading frames.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Linux capture session coordinator.");

        var portalResult = await _portal.StartSessionAsync(ct);

        var nodeId = portalResult.NodeIds.Count > 0 ? portalResult.NodeIds[0] : 0u;
        _logger.LogInformation(
            "Using PipeWire node ID {NodeId} via portal session {Handle}.",
            nodeId, portalResult.SessionHandle ?? "(none)");

        IsRunning = StartCaptureLoop(nodeId, portalResult.SessionHandle);
        return IsRunning;
    }

    /// <summary>
    /// Returns the latest available frame, or null if no frame is ready.
    /// </summary>
    public LinuxFrameSnapshot? TryReadLatestFrame()
    {
        return _frameChannel.TryRead();
    }

    /// <summary>
    /// Waits for the next frame up to <paramref name="timeoutMs"/> milliseconds.
    /// </summary>
    public async Task<LinuxFrameSnapshot?> WaitForNextFrameAsync(
        int timeoutMs = 100,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        return await _frameChannel.ReadAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        _portal.SessionLost -= OnPortalSessionLost;

        _captureCts?.Cancel();
        if (_captureLoop is not null)
        {
            try { await _captureLoop; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Capture loop exit error."); }
        }

        _frameSource?.Dispose();
        _captureCts?.Dispose();
        _frameChannel.Dispose();
        await _portal.DisposeAsync();
    }

    private sealed class LinuxFrameChannel : IDisposable
    {
        private readonly object _lock = new();
        private LinuxFrameSnapshot? _slot;
        private readonly SemaphoreSlim _semaphore = new(0);
        private bool _disposed;

        public void Write(LinuxFrameSnapshot newFrame)
        {
            LinuxFrameSnapshot? oldFrame = null;
            lock (_lock)
            {
                if (_disposed)
                {
                    if (newFrame.Data is not null)
                        System.Buffers.ArrayPool<byte>.Shared.Return(newFrame.Data);
                    return;
                }

                oldFrame = _slot;
                _slot = newFrame;

                if (oldFrame is null)
                {
                    _semaphore.Release();
                }
            }

            if (oldFrame?.Data is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(oldFrame.Data);
            }
        }

        public LinuxFrameSnapshot? TryRead()
        {
            lock (_lock)
            {
                if (_slot is null || _disposed)
                    return null;

                var frame = _slot;
                _slot = null;

                // Drain semaphore
                while (_semaphore.Wait(0)) { }

                return frame;
            }
        }

        public async Task<LinuxFrameSnapshot?> ReadAsync(CancellationToken ct)
        {
            try
            {
                await _semaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            lock (_lock)
            {
                if (_slot is null || _disposed)
                    return null;

                var frame = _slot;
                _slot = null;
                return frame;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                if (_slot?.Data is not null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(_slot.Data);
                    _slot = null;
                }
                _semaphore.Dispose();
            }
        }
    }

    // ── Private implementation ─────────────────────────────────────────

    private bool StartCaptureLoop(uint nodeId, string? portalSessionHandle = null)
    {
        _captureCts?.Cancel();
        _frameSource?.Dispose();

        _captureCts = new CancellationTokenSource();
        _frameSource = new LinuxPipeWireFrameSource(nodeId, portalSessionHandle, null);

        bool nativeOpen = _frameSource.TryOpen();
        if (!nativeOpen)
        {
            switch (_frameSource.LastOpenFailure)
            {
                case LinuxPipeWireOpenFailureKind.NativeLibraryNotFound:
                    _logger.LogWarning(
                        "PipeWire native bridge is not loadable from {ExpectedPath}. " +
                        "Leaving the coordinator inactive so the host can fall back cleanly.",
                        LinuxNativeBridgeLocator.GetExpectedPath());
                    break;
                case LinuxPipeWireOpenFailureKind.SessionCreateFailed:
                    _logger.LogWarning(
                        "PipeWire native session open failed for node {NodeId} with error {Code}. " +
                        "Leaving the coordinator inactive so the host can fall back cleanly.",
                        nodeId,
                        _frameSource.LastOpenErrorCode);
                    break;
                default:
                    _logger.LogWarning(
                        "PipeWire capture could not be initialized ({Reason}). Leaving the coordinator inactive so the host can fall back cleanly.",
                        _frameSource.LastOpenErrorMessage ?? "unknown error");
                    break;
            }

            _captureCts.Dispose();
            _captureCts = null;
            return false;
        }

        var token = _captureCts.Token;
        _captureLoop = Task.Run(() => RunCaptureLoopAsync(token), token);
        return true;
    }

    private async Task RunCaptureLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("PipeWire capture loop started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_frameSource is null || !_frameSource.IsNativeAvailable)
                {
                    await Task.Delay(500, ct);
                    continue;
                }

                var frame = await _frameSource.AcquireFrameAsync(timeoutMs: 50, ct: ct);
                if (frame is not null)
                {
                    // Publish to channel; DropOldest policy handles back-pressure automatically.
                    _frameChannel.Write(frame);
                    _frameSource.ReleaseFrame();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in capture loop; restarting after delay.");
                await Task.Delay(1000, ct);
            }
        }

        _logger.LogDebug("PipeWire capture loop exited.");
    }

    private void OnPortalSessionLost()
    {
        _logger.LogWarning("Portal session lost. Restarting capture coordinator.");
        IsRunning = false;
        _captureCts?.Cancel();
        // The portal will restart the session and call SessionStarted which
        // callers of this coordinator should use to call StartAsync again.
    }
}
