using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core;

namespace Remex.Agent;

/// <summary>
/// Hosted service that runs the headless command agent and coordinates the single-port handoff.
///
/// It is hosted inside a generic <see cref="IHost"/> so process lifetime is handled idiomatically on
/// every platform: a Windows Service gets a clean SCM stop (via <c>AddWindowsService</c>), and on
/// Linux/console the default lifetime handles SIGTERM (systemd stop) and SIGINT (Ctrl+C). The agent
/// owns the canonical-port web host; an interactive GUI host signals a takeover over the control pipe,
/// at which point the agent yields its web host and reclaims it when the GUI disconnects.
///
/// Reclaim reliability: after the GUI host exits its socket enters a brief OS cleanup window
/// (TIME_WAIT / socket linger). <see cref="StartWebHostAsync"/> polls for port availability before
/// calling <see cref="HostBootstrapper.CreateApplication"/> so that the port-fallback loop inside
/// <c>CreateApplication</c> never has a chance to drift onto a non-canonical port. If the port does
/// not free within <see cref="PortWaitMaxMs"/> we proceed and warn; this is preferable to hanging
/// indefinitely.
/// </summary>
internal sealed class AgentCoordinator : IHostedService, IAsyncDisposable
{
    // 30 s at 500 ms intervals gives ~60 poll attempts — well past a typical TIME_WAIT window.
    private const int PortWaitMaxMs = 30_000;
    private const int DefaultPortWaitIntervalMs = 500;

    private readonly ILogger _logger;
    private readonly Func<int, bool> _isPortFree;
    private readonly Func<WebApplication> _appFactory;
    private readonly int _portWaitIntervalMs;
    private readonly string? _controlPipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private Services.IPC.HostControlServer? _control;

    public AgentCoordinator(string[] args, ILogger logger)
        : this(logger, HostBootstrapper.IsPortFree,
              () => HostBootstrapper.CreateApplication(args, RemexConstants.DefaultPort, HostMode.CommandAgent))
    {
    }

    /// <summary>
    /// Test seam: inject a custom port-availability checker and/or web-application factory so
    /// the reclaim wait loop and post-bind verification can be exercised without real sockets.
    /// <paramref name="portWaitIntervalMs"/> overrides the poll interval so tests don't sleep 500ms per probe.
    /// <paramref name="controlPipeName"/> overrides the named-pipe name so tests don't collide with a
    /// running service or other parallel test instances.
    /// </summary>
    internal AgentCoordinator(
        ILogger logger,
        Func<int, bool> isPortFree,
        Func<WebApplication> appFactory,
        int portWaitIntervalMs = DefaultPortWaitIntervalMs,
        string? controlPipeName = null)
    {
        _logger = logger;
        _isPortFree = isPortFree;
        _appFactory = appFactory;
        _portWaitIntervalMs = portWaitIntervalMs;
        _controlPipeName = controlPipeName;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartWebHostAsync();
        _control = new Services.IPC.HostControlServer(_logger, onYield: StopWebHostAsync, onReclaim: StartWebHostAsync, pipeName: _controlPipeName);
        _control.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_control is not null)
        {
            await _control.DisposeAsync();
            _control = null;
        }

        await StopWebHostAsync();
    }

    private async Task StartWebHostAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null)
            {
                // Wait for the canonical port to clear before calling CreateApplication.
                // Without this, the port-fallback loop inside CreateApplication can silently
                // bind port+1 (e.g. 5006) when the GUI host's socket is still in TIME_WAIT,
                // making the agent unreachable to Android clients which always dial DefaultPort.
                await WaitForCanonicalPortAsync();

                _app = _appFactory();
                try
                {
                    await _app.StartAsync();
                }
                catch
                {
                    // Dispose the half-started app so the idempotency guard (_app is null) resets,
                    // allowing a subsequent reclaim attempt to create a fresh instance.
                    await _app.DisposeAsync();
                    _app = null;
                    throw;
                }

                // Belt-and-suspenders: warn if CreateApplication still drifted to a fallback.
                var boundPort = _app.Configuration["Host:Port"];
                if (boundPort != RemexConstants.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    _logger.LogWarning(
                        "Command agent bound port {BoundPort} instead of the canonical port {CanonicalPort}. " +
                        "Android clients dialing that port will not connect. " +
                        "Restart the service to retry binding the canonical port.",
                        boundPort, RemexConstants.DefaultPort);
                }
                else
                {
                    _logger.LogInformation("Command agent listening on the canonical port.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WaitForCanonicalPortAsync()
    {
        for (int elapsed = 0; elapsed < PortWaitMaxMs; elapsed += _portWaitIntervalMs)
        {
            if (_isPortFree(RemexConstants.DefaultPort))
                return;

            _logger.LogDebug(
                "Canonical port {Port} still in use; waiting {Interval}ms before reclaim ({Elapsed}ms elapsed).",
                RemexConstants.DefaultPort, _portWaitIntervalMs, elapsed);
            await Task.Delay(_portWaitIntervalMs);
        }

        _logger.LogWarning(
            "Canonical port {Port} still in use after {WaitMs}ms; proceeding — " +
            "the agent may bind a fallback port if the OS has not released it yet.",
            RemexConstants.DefaultPort, PortWaitMaxMs);
    }

    private async Task StopWebHostAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
                _logger.LogInformation("Command agent yielded the canonical port.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
