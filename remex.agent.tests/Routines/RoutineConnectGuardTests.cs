using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// RemEx-pp4cm C4: a routine source's D-Bus connect that times out or fails must dispose the connection
/// it abandons. Before, a hung activatable service left a live socket behind on every poll that retried.
/// </summary>
public sealed class RoutineConnectGuardTests
{
    private sealed class FakeConnection : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task AConnectThatTimesOutIsDisposed()
    {
        var connection = new FakeConnection();
        var never = new TaskCompletionSource();

        var result = await RoutineConnectGuard.ConnectOrDisposeAsync(
            connection, _ => never.Task, TimeSpan.FromMilliseconds(20), NullLogger.Instance);

        Assert.Null(result);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task AConnectThatFailsIsDisposed()
    {
        var connection = new FakeConnection();

        var result = await RoutineConnectGuard.ConnectOrDisposeAsync(
            connection, _ => Task.FromException(new IOException("no bus")), TimeSpan.FromSeconds(2), NullLogger.Instance);

        Assert.Null(result);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task AConnectThatSucceedsIsReturnedOpen()
    {
        var connection = new FakeConnection();

        var result = await RoutineConnectGuard.ConnectOrDisposeAsync(
            connection, _ => Task.CompletedTask, TimeSpan.FromSeconds(2), NullLogger.Instance);

        Assert.Same(connection, result);
        Assert.False(connection.Disposed);
    }
}
