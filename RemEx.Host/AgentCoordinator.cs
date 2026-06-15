using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core;

namespace Remex.Host;

/// <summary>
/// Hosted service that runs the headless command agent and coordinates the single-port handoff.
///
/// It is hosted inside a generic <see cref="IHost"/> so process lifetime is handled idiomatically on
/// every platform: a Windows Service gets a clean SCM stop (via <c>AddWindowsService</c>), and on
/// Linux/console the default lifetime handles SIGTERM (systemd stop) and SIGINT (Ctrl+C). The agent
/// owns the canonical-port web host; an interactive GUI host signals a takeover over the control pipe,
/// at which point the agent yields its web host and reclaims it when the GUI disconnects.
/// </summary>
internal sealed class AgentCoordinator : IHostedService, IAsyncDisposable
{
    private readonly string[] _args;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private Services.IPC.HostControlServer? _control;

    public AgentCoordinator(string[] args, ILogger logger)
    {
        _args = args;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartWebHostAsync();
        _control = new Services.IPC.HostControlServer(_logger, onYield: StopWebHostAsync, onReclaim: StartWebHostAsync);
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
                _app = HostBootstrapper.CreateApplication(_args, RemexConstants.DefaultPort, HostMode.CommandAgent);
                await _app.StartAsync();
                _logger.LogInformation("Command agent listening on the canonical port.");
            }
        }
        finally
        {
            _gate.Release();
        }
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
