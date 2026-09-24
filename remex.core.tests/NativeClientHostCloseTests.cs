using System.Net.WebSockets;
using Remex.Core.Models;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that a host-initiated Close frame is reported as a drop and leaves the control client able to
/// connect again (perf audit P0-12).
/// </summary>
/// <remarks>
/// <para>
/// THE BUG THIS PINS. The receive loop answered a Close frame by awaiting <c>DisconnectAsync</c>, which
/// awaits the receive-loop task — the loop awaiting itself. It never completed:
/// <c>ConnectionStateChanged(false)</c> never fired, so the reconnect heartbeat never ran, and every
/// later <c>ConnectAsync</c> (which starts by disconnecting) hung on the same stuck task. Nothing threw
/// and nothing logged; the app just sat there.
/// </para>
/// <para>
/// Every await is bounded by <see cref="TestBudget"/>, so a regression fails in seconds instead of
/// hanging the suite. <c>RemexNativeClient</c> is a process singleton; the assembly runs serially
/// (AssemblyInfo.cs).
/// </para>
/// </remarks>
public sealed class NativeClientHostCloseTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AHostCloseFrameReportsTheDropAndDoesNotWedgeTheClient()
    {
        await using var server = await DesktopWssServerFixture.StartAsync(async (socket, ct) =>
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "host shutting down", ct);
            // Hold the connection until the client lets go of it; how that ends is not under test.
            try
            {
                while (socket.State == WebSocketState.CloseSent)
                    await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }, path: RemexConstants.WebSocketPath);

        var client = RemexNativeClient.Current;

        // ConnectAsync reports false once on its way in (it disconnects first), so only a false that
        // follows the true counts as the drop.
        var opened = 0;
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(bool connected)
        {
            if (connected) Volatile.Write(ref opened, 1);
            else if (Volatile.Read(ref opened) == 1) dropped.TrySetResult();
        }

        client.ConnectionStateChanged += OnState;
        try
        {
            await client.ConnectAsync("127.0.0.1", server.Port, spkiHash: server.SpkiHashBase64)
                .WaitAsync(TestBudget);

            await dropped.Task.WaitAsync(TestBudget);

            // The loop must actually have finished: a disconnect from outside awaits it, and that is
            // exactly what the next ConnectAsync does first.
            await client.DisconnectAsync().WaitAsync(TestBudget);
            Assert.False(client.IsConnected);
        }
        finally
        {
            client.ConnectionStateChanged -= OnState;
        }
    }
}
