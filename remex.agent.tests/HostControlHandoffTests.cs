using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.IPC;

namespace Remex.Agent.Tests;

/// <summary>
/// Verifies the single-port handoff control protocol: a GUI takeover connection makes the agent yield,
/// and dropping that connection makes the agent reclaim — independent of any real web host.
/// </summary>
public class HostControlHandoffTests
{
    [Fact]
    public async Task Takeover_YieldsOnConnect_ReclaimsOnDisconnect()
    {
        var pipeName = "RemExHostControl-test-" + Guid.NewGuid().ToString("N");
        var yielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new HostControlServer(
            NullLogger.Instance,
            onYield: () => { yielded.TrySetResult(); return Task.CompletedTask; },
            onReclaim: () => { reclaimed.TrySetResult(); return Task.CompletedTask; },
            pipeName: pipeName);
        server.Start();

        var client = new HostControlClient();
        var acked = await client.RequestTakeoverAsync(TimeSpan.FromSeconds(5), pipeName);

        Assert.True(acked, "agent should ack the yield");
        await yielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Drop the connection -> the agent reclaims.
        await client.DisposeAsync();
        await reclaimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestTakeover_NoAgent_ReturnsFalse()
    {
        var pipeName = "RemExHostControl-absent-" + Guid.NewGuid().ToString("N");
        var client = new HostControlClient();
        var acked = await client.RequestTakeoverAsync(TimeSpan.FromMilliseconds(500), pipeName);
        Assert.False(acked, "no agent listening -> nothing to yield");
        await client.DisposeAsync();
    }
}
