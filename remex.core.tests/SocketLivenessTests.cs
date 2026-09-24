using System.Net.WebSockets;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the bounded send (perf audit P0-12): a write that cannot complete aborts the socket and says
/// so, while the caller's own cancellation stays a cancellation.
/// </summary>
/// <remarks>
/// <para>
/// A half-open link does not fail a send, it parks it: the kernel buffer fills and the write waits for
/// acknowledgements that never come. <see cref="StallingStream"/> is that link, under a real
/// <see cref="WebSocket"/> from <see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/>,
/// so the framework's own send path (and its own cancel-aborts behaviour, see
/// <see cref="CancelledReceiveKillsTheSocketTests"/>) is what is exercised, not a mock of it.
/// </para>
/// <para>
/// THE SECOND TEST IS THE DISCRIMINATING ONE. The deadline is a token linked to the caller's, so both
/// arrive at the same catch. Without the filter that separates them, a caller that gives up on a send
/// (a command's own budget) would be told the link is dead, and the reconnect path would fire on a
/// healthy connection.
/// </para>
/// <para>
/// <see cref="SocketLiveness.SendTimeoutOverrideForTests"/> is process-wide; the assembly runs
/// serially (AssemblyInfo.cs) and <see cref="Dispose"/> restores it.
/// </para>
/// </remarks>
public sealed class SocketLivenessTests : IDisposable
{
    /// <summary>Fail-fast ceiling on every await, so a regression fails instead of hanging the suite.</summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    public void Dispose() => SocketLiveness.SendTimeoutOverrideForTests = null;

    [Fact]
    public async Task ASendThatCannotBeWrittenTimesOutAndAbortsTheSocket()
    {
        SocketLiveness.SendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(200);
        await using var stream = new StallingStream();
        using var socket = WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null, Timeout.InfiniteTimeSpan);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            SocketLiveness.SendOrAbortAsync(
                socket, new ArraySegment<byte>([1, 2, 3]), WebSocketMessageType.Text, CancellationToken.None)
            .WaitAsync(TestBudget));

        // The abort is the point: it is what fails the parked receive loop and hands the drop to the
        // existing reconnect path.
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    [Fact]
    public async Task CancellingTheCallersTokenIsACancellationNotATimeout()
    {
        // Our deadline cannot fire inside this test; only the caller's token can.
        SocketLiveness.SendTimeoutOverrideForTests = TimeSpan.FromMinutes(5);
        await using var stream = new StallingStream();
        using var socket = WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null, Timeout.InfiniteTimeSpan);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ex = await Record.ExceptionAsync(() =>
            SocketLiveness.SendOrAbortAsync(
                socket, new ArraySegment<byte>([1, 2, 3]), WebSocketMessageType.Text, caller.Token)
            .WaitAsync(TestBudget));

        Assert.NotNull(ex);
        Assert.IsNotType<TimeoutException>(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>
    /// A transport whose writes never complete on their own — a peer that has stopped acknowledging.
    /// Like a real socket stream, a stalled write is released by cancellation or by disposal (which is
    /// what <see cref="WebSocket.Abort"/> does to it), and fails when released.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken);
            throw new ObjectDisposedException(nameof(StallingStream));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
