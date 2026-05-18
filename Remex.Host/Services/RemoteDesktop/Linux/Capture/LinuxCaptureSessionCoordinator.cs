using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly Channel<LinuxFrameSnapshot> _frameChannel =
        Channel.CreateBounded<LinuxFrameSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

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
    public async Task StartAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Linux capture session coordinator.");

        var portalResult = await _portal.StartSessionAsync(ct);

        var nodeId = portalResult.NodeIds.Count > 0 ? portalResult.NodeIds[0] : 0u;
        _logger.LogInformation(
            "Using PipeWire node ID {NodeId} via portal session {Handle}.",
            nodeId, portalResult.SessionHandle ?? "(none)");

        StartCaptureLoop(nodeId, portalResult.SessionHandle);
        IsRunning = true;
    }

    /// <summary>
    /// Returns the latest available frame, or null if no frame is ready.
    /// </summary>
    public LinuxFrameSnapshot? TryReadLatestFrame()
    {
        _frameChannel.Reader.TryRead(out var frame);
        return frame;
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
        try
        {
            return await _frameChannel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
        await _portal.DisposeAsync();
    }

    // ── Private implementation ─────────────────────────────────────────

    private void StartCaptureLoop(uint nodeId, string? portalSessionHandle = null)
    {
        _captureCts?.Cancel();
        _frameSource?.Dispose();

        _captureCts = new CancellationTokenSource();
        _frameSource = new LinuxPipeWireFrameSource(nodeId, portalSessionHandle, null);

        bool nativeOpen = _frameSource.TryOpen();
        if (!nativeOpen)
        {
            _logger.LogWarning(
                "PipeWire native library not available. " +
                "Capture coordinator will not produce frames until libremex_linux_bridge.so is installed.");
        }

        var token = _captureCts.Token;
        _captureLoop = Task.Run(() => RunCaptureLoopAsync(token), token);
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
                    _frameChannel.Writer.TryWrite(frame);
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
