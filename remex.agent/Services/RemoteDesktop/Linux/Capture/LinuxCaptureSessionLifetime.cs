using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Services;
using Remex.Agent.Services.RemoteDesktop.Linux.Portal;
using Remex.Agent.Services.ScreenCapture;

namespace Remex.Agent.Services.RemoteDesktop.Linux.Capture;

/// <summary>
/// Singleton that owns the xdg-desktop-portal ScreenCast session and
/// <see cref="LinuxCaptureSessionCoordinator"/> for the duration of at least
/// one active <c>/ws/desktop</c> connection.
///
/// Reference-counted: the first <see cref="AcquireAsync"/> call opens the
/// portal session; the last <see cref="ReleaseAsync"/> tears it down.
/// Concurrent acquires after the first all await the same start task.
///
/// On portal session loss, the coordinator is nulled out of the screen capture
/// service so the legacy shell-tool path takes over until the next connection.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCaptureSessionLifetime : IAsyncDisposable
{
    private readonly ILogger<LinuxCaptureSessionLifetime> _logger;
    private readonly IScreenCaptureService _screenCapture;
    private readonly ILoggerFactory _loggerFactory;

    private readonly object _gate = new();
    private int _refcount;
    private Task<bool>? _startTask;
    private LinuxPortalRemoteDesktopSessionService? _portal;
    private LinuxCaptureSessionCoordinator? _coordinator;

    // Once opened, the portal session (and its PipeWire stream) stays warm for the PROCESS
    // lifetime instead of being closed when the last client disconnects. Closing a KDE
    // ScreenCast session and restoring it shortly afterwards — which is exactly what the
    // Android client's disconnect→reconnect and monitor-switch flows do — reliably yields a
    // stream that never produces frames on Plasma: KWin reports a stream id and
    // pw_stream_connect succeeds, but no buffers ever arrive, and the broken window lasts
    // minutes after a client-initiated Close (RemEx-lq6h; verified live — only restores after
    // process death worked reliably). Keeping the session warm sidesteps the race entirely and
    // makes reconnects near-instant. Side effect: the compositor's screen-sharing indicator
    // stays on while the agent runs, which honestly reflects that the machine remains
    // remotely controllable.

    public LinuxCaptureSessionLifetime(
        ILogger<LinuxCaptureSessionLifetime> logger,
        IScreenCaptureService screenCapture,
        ILoggerFactory loggerFactory)
    {
        _logger = Guard.NotNull(logger);
        _screenCapture = Guard.NotNull(screenCapture);
        _loggerFactory = Guard.NotNull(loggerFactory);
    }

    /// <summary>
    /// Increments the refcount and, if this is the first caller, opens the
    /// portal session and wires the coordinator into <see cref="LinuxScreenCaptureService"/>.
    ///
    /// Returns <c>true</c> if PipeWire capture is available; <c>false</c> if the
    /// portal session failed (legacy path will be used). Callers MUST call
    /// <see cref="ReleaseAsync"/> if and only if this returns <c>true</c>.
    /// </summary>
    public Task<bool> AcquireAsync(CancellationToken ct)
    {
        Task<bool> startTask;
        lock (_gate)
        {
            _refcount++;

            // Only adopt a warm session that is actually healthy; a completed start task whose
            // coordinator has since died (portal session lost while idle) must cold-start.
            if (_startTask is { IsCompleted: true } && _coordinator is not { IsRunning: true })
            {
                _startTask = null;
            }

            _startTask ??= StartInternalAsync(ct);
            startTask = _startTask;
        }
        return startTask;
    }

    /// <summary>
    /// Injects an absolute pointer position (virtual-desktop coordinates) through the active
    /// unified RemoteDesktop + ScreenCast portal session. Absolute injection is drift-free — the
    /// compositor clamps the position to the stream surface natively — unlike the relative-delta
    /// emulation used when only an input-only portal session exists. Returns false when no
    /// session is active or the stream geometry is unusable; the caller then falls back to
    /// relative motion. (RemEx-lq6h)
    /// </summary>
    public bool TryInjectPointerMotionAbsolute(double desktopX, double desktopY)
    {
        var portal = _portal;
        var session = portal?.CurrentSession;
        if (portal is null || session is null || session.Streams.Count == 0)
        {
            return false;
        }

        // Pick the stream whose compositor-space rect contains the point. The common case is a
        // single full-workspace stream whose rect covers the whole virtual desktop.
        PortalStreamInfo? target = null;
        foreach (var stream in session.Streams)
        {
            if (stream.Width > 0 && stream.Height > 0 &&
                desktopX >= stream.X && desktopX < stream.X + stream.Width &&
                desktopY >= stream.Y && desktopY < stream.Y + stream.Height)
            {
                target = stream;
                break;
            }
        }
        target ??= session.Streams[0];

        double streamX, streamY;
        if (target.Width > 0 && target.Height > 0)
        {
            streamX = Math.Clamp(desktopX - target.X, 0, target.Width - 1);
            streamY = Math.Clamp(desktopY - target.Y, 0, target.Height - 1);
        }
        else if (_screenCapture is LinuxScreenCaptureService svc)
        {
            // Portal omitted the stream geometry: map against the detected virtual-desktop
            // bounding box instead.
            var (left, top, width, height) = svc.GetVirtualDesktopBounds();
            if (width <= 0 || height <= 0)
            {
                return false;
            }
            streamX = Math.Clamp(desktopX - left, 0, width - 1);
            streamY = Math.Clamp(desktopY - top, 0, height - 1);
        }
        else
        {
            return false;
        }

        return portal.TryNotifyPointerMotionAbsolute(target.NodeId, streamX, streamY);
    }

    /// <summary>
    /// Decrements the refcount. When it reaches zero, tears down the coordinator
    /// and portal session. Must only be called if <see cref="AcquireAsync"/>
    /// returned <c>true</c>.
    /// </summary>
    public Task ReleaseAsync()
    {
        lock (_gate)
        {
            if (_refcount <= 0)
            {
                Debug.Assert(false, "LinuxCaptureSessionLifetime: refcount underflow");
                _logger.LogError(
                    "LinuxCaptureSessionLifetime: ReleaseAsync called more times than AcquireAsync.");
                _refcount = 0;
                return Task.CompletedTask;
            }
            _refcount--;
        }

        // Deliberately NO teardown at refcount 0: the session stays warm for the process
        // lifetime so the next connect adopts it (see the class comment above — restoring a
        // freshly closed KDE session yields a permanently silent stream). Teardown happens
        // only on portal session loss or process shutdown (DisposeAsync).
        return Task.CompletedTask;
    }

    private async Task<bool> StartInternalAsync(CancellationToken ct)
    {
        try
        {
            _portal = new LinuxPortalRemoteDesktopSessionService(
                appId: "com.clindsay94.RemEx",
                logger: _loggerFactory.CreateLogger<LinuxPortalRemoteDesktopSessionService>());

            // Subscribe before starting so we don't miss an early SessionLost event.
            _portal.SessionLost += OnPortalSessionLost;

            _coordinator = new LinuxCaptureSessionCoordinator(
                _portal,
                logger: _loggerFactory.CreateLogger<LinuxCaptureSessionCoordinator>());

            var captureReady = await _coordinator.StartAsync(ct);
            if (!captureReady)
            {
                _logger.LogError(
                    "LinuxCaptureSessionLifetime: portal session opened but the PipeWire native bridge could not produce frames. " +
                    "Keeping the capture coordinator detached so the legacy path is used without the extra PipeWire wait.");

                _portal.SessionLost -= OnPortalSessionLost;
                await _coordinator.DisposeAsync();
                _coordinator = null;
                _portal = null;

                lock (_gate)
                {
                    _refcount = 0;
                    _startTask = null;
                }

                return false;
            }

            // Verify the stream actually produces before wiring it in. KWin pushes the current
            // frame to a fresh ScreenCast stream immediately, so a healthy session delivers
            // within a couple of seconds. A session created shortly after the previous one
            // closed can come up permanently silent (the KDE close→restore race documented on
            // IdleTeardownGrace) — recreate the portal session once before accepting it.
            if (!await WaitForFirstFrameAsync(_coordinator, ct))
            {
                _logger.LogWarning(
                    "PipeWire stream produced no frames within the verification window; recreating the portal session once.");

                _portal.SessionLost -= OnPortalSessionLost;
                await _coordinator.DisposeAsync(); // also disposes the portal session

                _portal = new LinuxPortalRemoteDesktopSessionService(
                    appId: "com.clindsay94.RemEx",
                    logger: _loggerFactory.CreateLogger<LinuxPortalRemoteDesktopSessionService>());
                _portal.SessionLost += OnPortalSessionLost;

                _coordinator = new LinuxCaptureSessionCoordinator(
                    _portal,
                    logger: _loggerFactory.CreateLogger<LinuxCaptureSessionCoordinator>());

                var retryReady = await _coordinator.StartAsync(ct);
                if (!retryReady)
                {
                    _logger.LogError(
                        "LinuxCaptureSessionLifetime: retry portal session failed to start; PipeWire capture unavailable.");

                    _portal.SessionLost -= OnPortalSessionLost;
                    await _coordinator.DisposeAsync();
                    _coordinator = null;
                    _portal = null;

                    lock (_gate)
                    {
                        _refcount = 0;
                        _startTask = null;
                    }

                    return false;
                }

                if (!await WaitForFirstFrameAsync(_coordinator, ct))
                {
                    _logger.LogWarning(
                        "PipeWire stream still produced no frames after a fresh portal session; wiring it anyway (it may recover).");
                }
            }

            if (_screenCapture is LinuxScreenCaptureService svc)
                svc.SetCaptureCoordinator(_coordinator);

            _logger.LogInformation(
                "PipeWire capture lifetime active (coordinator wired into LinuxScreenCaptureService).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "LinuxCaptureSessionLifetime: portal session creation failed; PipeWire capture unavailable.");

            // Clean up partially-created objects.
            if (_portal is not null)
                _portal.SessionLost -= OnPortalSessionLost;
            _coordinator = null;
            _portal = null;

            // Decrement the refcount we already incremented in AcquireAsync so the
            // next client can attempt a fresh portal open (option-a contract: caller
            // skips ReleaseAsync when we return false).
            // Reset refcount to 0 unconditionally: all concurrent callers sharing
            // this failed _startTask will skip ReleaseAsync (option-a contract), so
            // the count must be zeroed to allow a fresh portal open on the next Acquire.
            lock (_gate)
            {
                _refcount = 0;
                _startTask = null; // allow retry on next AcquireAsync
            }

            return false;
        }
    }

    private async Task StopInternalAsync()
    {
        var coordinator = _coordinator;
        var portal = _portal;

        _coordinator = null;
        _portal = null;

        // Only clear _startTask when refcount is still 0.  If a new AcquireAsync
        // raced in and already incremented the refcount (setting _refcount == 1 and
        // overwriting _startTask with a fresh task), leave the new task alone.
        lock (_gate)
        {
            if (_refcount == 0)
                _startTask = null;
        }

        // Detach the coordinator first so the screen capture service reverts to legacy.
        if (_screenCapture is LinuxScreenCaptureService svc)
            svc.SetCaptureCoordinator(null);

        if (coordinator is not null)
        {
            try { await coordinator.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LinuxCaptureSessionLifetime: coordinator dispose threw.");
            }
        }

        // The coordinator's DisposeAsync already disposes the portal internally
        // (see LinuxCaptureSessionCoordinator.DisposeAsync → _portal.DisposeAsync).
        // Only dispose the portal here if the coordinator never started or was null.
        if (coordinator is null && portal is not null)
        {
            portal.SessionLost -= OnPortalSessionLost;
            try { await portal.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LinuxCaptureSessionLifetime: portal dispose threw.");
            }
        }
        else if (portal is not null)
        {
            portal.SessionLost -= OnPortalSessionLost;
        }
    }

    /// <summary>
    /// Waits briefly for the coordinator's PipeWire stream to deliver its first frame. Consumes
    /// (and returns to the pool) the frame it sees; the capture loop keeps pumping afterwards.
    /// </summary>
    private static async Task<bool> WaitForFirstFrameAsync(
        LinuxCaptureSessionCoordinator coordinator, CancellationToken ct)
    {
        var frame = await coordinator.WaitForNextFrameAsync(timeoutMs: 3000, ct: ct);
        if (frame is null)
            return false;

        if (frame.Data is not null)
            System.Buffers.ArrayPool<byte>.Shared.Return(frame.Data);
        return true;
    }

    private void OnPortalSessionLost()
    {
        if (_screenCapture is LinuxScreenCaptureService svc)
            svc.SetCaptureCoordinator(null);

        // The session is dead: the next AcquireAsync must cold-start instead of adopting it,
        // and the remains are torn down in the background (safe with clients still connected —
        // they fall back to legacy capture until they reconnect).
        lock (_gate)
        {
            _startTask = null;
        }
        _ = Task.Run(async () =>
        {
            try { await StopInternalAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Teardown after portal session loss failed."); }
        });

        _logger.LogWarning(
            "LinuxCaptureSessionLifetime: portal session lost; " +
            "falling back to legacy capture until next acquire.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync();
    }
}
