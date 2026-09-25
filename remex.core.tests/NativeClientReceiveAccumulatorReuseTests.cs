using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Native;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Perf audit P3-2: <c>RemexNativeClient.ReceiveLoopAsync</c>'s multi-fragment accumulator used to be
/// a fresh <see cref="MemoryStream"/> per occurrence; it is now one instance reused (reset, not
/// recreated) for the life of the receive loop. <c>RemexNativeClient</c> is a process singleton and
/// this assembly runs serially (see <see cref="NativeClientHostCloseTests"/>), so this drives the
/// real receive loop over a genuine <c>wss://</c> socket the same way that test does.
/// </summary>
public sealed class NativeClientReceiveAccumulatorReuseTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    private static TelemetryPayload BigTelemetryPayload(string marker, int sensorCount = 2000) => new()
    {
        UptimeText = marker,
        // Padded well past the 32KB receive buffer (RemexNativeClient.ReceiveLoopAsync) so a single
        // WebSocket message genuinely spans more than one ReceiveAsync call on the client side,
        // regardless of how the server chooses to send it - that's what exercises the accumulator.
        Sensors = Enumerable.Range(0, sensorCount)
            .Select(i => new SensorReading { Name = $"sensor-{i}", Value = i, Unit = "C", Category = "Other" })
            .ToList(),
    };

    [Fact]
    public async Task AMultiFragmentMessage_ThenAnotherOne_BothArriveIntactWithNoCrossContamination()
    {
        // THE CASE THIS ROW'S FIX COULD GET WRONG: reusing one accumulator across iterations means a
        // stale byte left over from message A's buffer could leak into message B if the reset/length
        // bookkeeping is off by even one call. Two large (multi-fragment), DIFFERENT-content messages
        // sent back to back is the direct test of that - not just "one message still works".
        var first = BigTelemetryPayload("first-message");
        var second = BigTelemetryPayload("second-message");

        var received = new List<TelemetryPayload>();
        var gotBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await DesktopWssServerFixture.StartAsync(async (socket, ct) =>
        {
            var firstBytes = MessageSerializer.Serialize(new RemexMessage { Type = MessageTypes.Telemetry, Telemetry = first });
            var secondBytes = MessageSerializer.Serialize(new RemexMessage { Type = MessageTypes.Telemetry, Telemetry = second });

            await socket.SendAsync(firstBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, ct);
            await socket.SendAsync(secondBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, ct);

            // Hold the connection open until the client is done reading both messages.
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open)
                    await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { }
        }, path: RemexConstants.WebSocketPath);

        var client = RemexNativeClient.Current;
        void OnTelemetry(TelemetryPayload payload)
        {
            lock (received)
            {
                received.Add(payload);
                if (received.Count >= 2) gotBoth.TrySetResult();
            }
        }

        client.TelemetryReceived += OnTelemetry;
        try
        {
            await client.ConnectAsync("127.0.0.1", server.Port, spkiHash: server.SpkiHashBase64).WaitAsync(TestBudget);

            await gotBoth.Task.WaitAsync(TestBudget);

            lock (received)
            {
                Assert.Equal(2, received.Count);
                Assert.Equal("first-message", received[0].UptimeText);
                Assert.Equal(2000, received[0].Sensors.Count);
                Assert.Equal("second-message", received[1].UptimeText);
                Assert.Equal(2000, received[1].Sensors.Count);

                // Content-level proof against cross-contamination between the two reused-accumulator
                // passes, not just "both had the right marker and count".
                Assert.All(received[0].Sensors, s => Assert.StartsWith("sensor-", s.Name));
                Assert.All(received[1].Sensors, s => Assert.StartsWith("sensor-", s.Name));
            }
        }
        finally
        {
            client.TelemetryReceived -= OnTelemetry;
            await client.DisconnectAsync().WaitAsync(TestBudget);
        }
    }

    /// <summary>
    /// Review round 1 MEDIUM: an unusually large message (media artwork, a big launcher sync) used
    /// to leave the reused accumulator's full capacity resident for the rest of the connection.
    /// <c>ReceiveLoopAsync</c> now drops and disposes it once its capacity exceeds 1MB, rebuilding a
    /// fresh one the next time a multi-fragment message needs it. Proven indirectly, the same way as
    /// the test above: a message past the 1MB threshold (triggers the drop-and-rebuild) followed by
    /// ANOTHER multi-fragment message would fail to parse if the rebuilt accumulator were broken.
    /// </summary>
    [Fact]
    public async Task AnOversizedMessage_TriggersTheCapacityDrop_AndTheNextMultiFragmentMessageStillWorks()
    {
        var oversized = BigTelemetryPayload("oversized-message", sensorCount: 20_000); // well past 1MB serialized
        var afterDrop = BigTelemetryPayload("after-drop-message", sensorCount: 2000);

        var received = new List<TelemetryPayload>();
        var gotBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await DesktopWssServerFixture.StartAsync(async (socket, ct) =>
        {
            var oversizedBytes = MessageSerializer.Serialize(new RemexMessage { Type = MessageTypes.Telemetry, Telemetry = oversized });
            var afterDropBytes = MessageSerializer.Serialize(new RemexMessage { Type = MessageTypes.Telemetry, Telemetry = afterDrop });

            await socket.SendAsync(oversizedBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, ct);
            await socket.SendAsync(afterDropBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, ct);

            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open)
                    await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { }
        }, path: RemexConstants.WebSocketPath);

        var client = RemexNativeClient.Current;
        void OnTelemetry(TelemetryPayload payload)
        {
            lock (received)
            {
                received.Add(payload);
                if (received.Count >= 2) gotBoth.TrySetResult();
            }
        }

        client.TelemetryReceived += OnTelemetry;
        try
        {
            await client.ConnectAsync("127.0.0.1", server.Port, spkiHash: server.SpkiHashBase64).WaitAsync(TestBudget);

            await gotBoth.Task.WaitAsync(TestBudget);

            lock (received)
            {
                Assert.Equal(2, received.Count);
                Assert.Equal("oversized-message", received[0].UptimeText);
                Assert.Equal(20_000, received[0].Sensors.Count);
                Assert.Equal("after-drop-message", received[1].UptimeText);
                Assert.Equal(2000, received[1].Sensors.Count);
            }
        }
        finally
        {
            client.TelemetryReceived -= OnTelemetry;
            await client.DisconnectAsync().WaitAsync(TestBudget);
        }
    }
}
