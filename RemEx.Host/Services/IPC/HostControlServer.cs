using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.IPC;

/// <summary>
/// Single-port handoff coordinator for the headless command agent.
///
/// The agent owns the canonical-port web host while no interactive GUI host is running. When the
/// GUI host launches it connects to the <c>RemExHostControl</c> named pipe; that connection is the
/// takeover signal — the agent <b>yields</b> (stops its web host, freeing the port and releasing the
/// RemExLocalIPC mutex/pipe) so the GUI can bind it and serve the full host (commands + streaming).
/// The GUI keeps the connection open for its whole lifetime; when it exits or crashes the connection
/// drops and the agent <b>reclaims</b> (restarts its web host). Connection-presence makes the handoff
/// crash-safe: no explicit "reclaim" message is needed.
///
/// This pipe is intentionally independent of the web host's own <see cref="LocalIpcServerService"/>
/// pipe, so the control channel survives while the web host is stopped.
/// </summary>
public sealed class HostControlServer : IAsyncDisposable
{
    public const string PipeName = "RemExHostControl";

    private readonly ILogger _logger;
    private readonly Func<Task> _onYield;
    private readonly Func<Task> _onReclaim;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="onYield">Stop the web host (free the canonical port). Awaited before the ack.</param>
    /// <param name="onReclaim">Restart the web host on the canonical port after the GUI disconnects.</param>
    /// <param name="pipeName">Override the control pipe name (tests use a unique name for isolation).</param>
    public HostControlServer(ILogger logger, Func<Task> onYield, Func<Task> onReclaim, string? pipeName = null)
    {
        _logger = logger;
        _onYield = onYield;
        _onReclaim = onReclaim;
        _pipeName = pipeName ?? PipeName;
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                _logger.LogInformation("GUI host takeover requested; yielding the canonical-port listener.");
                await _onYield();

                // Ack so the GUI knows the port is free and it can bind.
                try
                {
                    await pipe.WriteAsync(new byte[] { 1 }, ct);
                    await pipe.FlushAsync(ct);
                }
                catch { /* GUI may have gone away; the disconnect path below reclaims. */ }

                // Block until the GUI disconnects (clean exit or crash), then reclaim.
                await WaitForDisconnectAsync(pipe, ct);

                _logger.LogInformation("GUI host control connection closed; reclaiming the canonical-port listener.");
                await _onReclaim();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host control loop error; retrying shortly.");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task WaitForDisconnectAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buffer = new byte[1];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            int read;
            try
            {
                // Returns 0 on a clean client disconnect; throws IOException on an abrupt drop.
                read = await pipe.ReadAsync(buffer, ct);
            }
            catch
            {
                return;
            }

            if (read == 0)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* best-effort */ }
        }
        _cts.Dispose();
    }
}
