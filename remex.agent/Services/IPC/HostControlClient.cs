using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Agent.Services.IPC;

/// <summary>
/// GUI-side half of the single-port handoff. On launch the interactive host connects to the agent's
/// <see cref="HostControlServer"/> pipe and waits for the agent to yield the canonical port; it then
/// keeps the connection open for its whole lifetime. Disposing it (process exit) drops the connection,
/// which the agent observes and reclaims the port.
///
/// If no agent is running (pipe absent), <see cref="RequestTakeoverAsync"/> returns quickly and the
/// GUI simply binds the port itself.
/// </summary>
public sealed class HostControlClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;

    /// <summary>
    /// Asks a running agent to yield the canonical port. Returns when the agent has acked the yield
    /// (port free) or when there is no agent to yield. The returned client keeps the connection open;
    /// dispose it on process exit so the agent reclaims the port.
    /// </summary>
    public async Task<bool> RequestTakeoverAsync(TimeSpan timeout, string? pipeName = null)
    {
        var deadline = (int)Math.Max(250, timeout.TotalMilliseconds);
        try
        {
            var pipe = new NamedPipeClientStream(
                ".",
                pipeName ?? HostControlServer.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using (var connectCts = new CancellationTokenSource(deadline))
            {
                await pipe.ConnectAsync(connectCts.Token);
            }

            _pipe = pipe;

            // Wait for the agent's yield ack so we don't race it for the port.
            var ack = new byte[1];
            using var ackCts = new CancellationTokenSource(deadline);
            var n = await _pipe.ReadAsync(ack, ackCts.Token);
            return n == 1;
        }
        catch (OperationCanceledException)
        {
            // No agent answered in time — nothing to yield; proceed to bind the port directly.
            await DisposeAsync();
            return false;
        }
        catch (Exception)
        {
            // Pipe missing or errored (no agent / unsupported) — proceed; StalePortReclaimer is the
            // fallback if some stale instance is still holding the port.
            await DisposeAsync();
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null)
        {
            try { await _pipe.DisposeAsync(); } catch { /* best-effort */ }
            _pipe = null;
        }
    }
}
