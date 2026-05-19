using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;
using Remex.Host.Services.RemoteDesktop.Linux.Portal;
using Remex.Host.Services.ScreenCapture;

namespace Remex.Host.Services.RemoteDesktop.Linux.Capture;

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

    public LinuxCaptureSessionLifetime(
        ILogger<LinuxCaptureSessionLifetime> logger,
        IScreenCaptureService screenCapture,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _loggerFactory = loggerFactory;
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
            if (_refcount == 1)
                _startTask = StartInternalAsync(ct);
            startTask = _startTask!;
        }
        return startTask;
    }

    /// <summary>
    /// Decrements the refcount. When it reaches zero, tears down the coordinator
    /// and portal session. Must only be called if <see cref="AcquireAsync"/>
    /// returned <c>true</c>.
    /// </summary>
    public async Task ReleaseAsync()
    {
        bool shouldStop;
        lock (_gate)
        {
            if (_refcount <= 0)
            {
                Debug.Assert(false, "LinuxCaptureSessionLifetime: refcount underflow");
                _logger.LogError(
                    "LinuxCaptureSessionLifetime: ReleaseAsync called more times than AcquireAsync.");
                _refcount = 0;
                return;
            }
            _refcount--;
            shouldStop = _refcount == 0;
        }

        if (shouldStop)
            await StopInternalAsync();
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

            await _coordinator.StartAsync(ct);

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

    private void OnPortalSessionLost()
    {
        if (_screenCapture is LinuxScreenCaptureService svc)
            svc.SetCaptureCoordinator(null);

        _logger.LogWarning(
            "LinuxCaptureSessionLifetime: portal session lost; " +
            "falling back to legacy capture until next acquire.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync();
    }
}
