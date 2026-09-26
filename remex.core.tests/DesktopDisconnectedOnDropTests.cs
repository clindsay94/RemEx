using System.Net.WebSockets;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that <see cref="RemexDesktopClient.Disconnected"/> fires on EVERY exit of
/// <c>ReceiveLoopAsync</c> — a mid-session socket drop included — not only from an explicit
/// <see cref="RemexDesktopClient.DisconnectAsync"/> call (RemEx-8sf8m).
/// </summary>
/// <remarks>
/// Before this fix, <c>Disconnected</c> was raised only from <c>DisconnectAsync</c>. A drop the
/// receive loop noticed on its own — the exact case <c>SendInputAsync</c> / <c>SendPointerBatchAsync</c>
/// recover from by calling <c>StartStreamAsync</c> directly — never raised it, so nothing downstream
/// (the JNI-side <c>FrameDropKeyframeGate</c> reset in <c>AndroidNativeExports</c>) ever learned the
/// old session had ended. Driven over a real <c>wss://</c> socket, like
/// <see cref="DesktopFrameReceiveTests"/> — the receive loop is unreachable without one.
/// </remarks>
public sealed class DesktopDisconnectedOnDropTests
{
    private const string Host = "127.0.0.1";

    [Fact]
    public async Task DisconnectedFiresOnAMidSessionDropWithoutAnExplicitDisconnectCall()
    {
        var disconnectedCount = 0;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await DesktopWssServerFixture.StartAsync(onAccepted: async (socket, ct) =>
        {
            // Simulates a mid-session socket drop: the host closes the socket while the client never
            // calls DisconnectAsync itself.
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "simulated drop", ct);
        });

        var client = new RemexDesktopClient();
        client.Disconnected += () =>
        {
            Interlocked.Increment(ref disconnectedCount);
            disconnected.TrySetResult();
        };

        try
        {
            await client.ConnectAsync(Host, server.Port, clientId: "test-client", spkiHash: server.SpkiHashBase64);

            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(disconnectedCount >= 1,
                "the receive loop's own exit must raise Disconnected, not only an explicit DisconnectAsync call");
        }
        finally
        {
            // Idempotent from the subscriber's point of view (Reset() on the real FrameDropKeyframeGate
            // consumer) even though this raises Disconnected a second time.
            await client.DisconnectAsync();
        }
    }
}
