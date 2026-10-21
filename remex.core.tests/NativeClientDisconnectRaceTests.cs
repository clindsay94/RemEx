using Remex.Core.Models;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// A disconnect that was already under way when a new connect started must not close that new
/// connection (RemEx-172ba review R5).
/// </summary>
/// <remarks>
/// <c>DisconnectNative</c> runs <see cref="RemexNativeClient.DisconnectAsync"/> on the thread pool, so the
/// phone's next connect can land while it is still awaiting. It used to re-read the socket field after
/// that await and close whatever it found there. The seam parks the disconnect at that await while a
/// whole connect runs. <c>RemexNativeClient</c> is a process singleton; the assembly runs serially.
/// </remarks>
public sealed class NativeClientDisconnectRaceTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ADisconnectStartedBeforeAConnect_LeavesTheNewConnectionUp()
    {
        await using var server = await DesktopWssServerFixture.StartAsync(path: RemexConstants.WebSocketPath);
        var client = RemexNativeClient.Current;
        var states = new List<bool>();
        void OnState(bool connected) { lock (states) states.Add(connected); }

        try
        {
            await client.ConnectAsync("127.0.0.1", server.Port, spkiHash: server.SpkiHashBase64).WaitAsync(TestBudget);

            var reconnected = false;
            client.AfterReceiveLoopDrainedForTests = async () =>
            {
                if (reconnected) return;
                reconnected = true;
                await client.ConnectAsync("127.0.0.1", server.Port, spkiHash: server.SpkiHashBase64).WaitAsync(TestBudget);
                client.ConnectionStateChanged += OnState;
            };

            await client.DisconnectAsync().WaitAsync(TestBudget);

            Assert.True(reconnected, "precondition: the connect ran inside the disconnect");
            Assert.True(client.IsConnected, "the connection made after the disconnect began is still up");
            lock (states)
                Assert.DoesNotContain(false, states);
        }
        finally
        {
            client.AfterReceiveLoopDrainedForTests = null;
            client.ConnectionStateChanged -= OnState;
            await client.DisconnectAsync().WaitAsync(TestBudget);
        }
    }
}
