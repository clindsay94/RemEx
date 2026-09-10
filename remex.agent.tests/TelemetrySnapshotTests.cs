using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Cover for serializing each telemetry sample once instead of once per connected client
/// (RemEx-0zbj).
/// </summary>
/// <remarks>
/// <para>
/// Every client stream used to build and serialize its own copy of the identical payload every
/// second. With HWiNFO running that envelope is 60-100 KB, so each connection cost another Large
/// Object Heap allocation per tick — and the PC's own dashboard is a client too, so a single phone
/// already meant two. The sampler now publishes the bytes alongside the sample and the streams send
/// those.
/// </para>
/// <para>
/// THE PROPERTY WORTH PROTECTING is that the bytes and the payload cannot drift apart. The sampler
/// writes on its own thread while every client stream reads on theirs, so publishing them as two
/// fields would let a reader pick up a new payload with the previous tick's bytes and send telemetry
/// that disagrees with itself — invisible, because the client would simply render slightly stale
/// numbers. They travel as one immutable object for that reason, and the round-trip assertions below
/// fail if a future change splits them.
/// </para>
/// </remarks>
public class TelemetrySnapshotTests
{
    /// <summary>Returns a distinct payload per call so successive snapshots are distinguishable.</summary>
    private sealed class CountingTelemetryService : ITelemetryService
    {
        private int _calls;

        public Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult(new TelemetryPayload
            {
                Sensors =
                [
                    new SensorReading
                    {
                        Name = "Total CPU Usage",
                        Value = n,
                        Unit = "%",
                        Category = "CPU",
                        Source = "Test",
                    },
                ],
            });
        }
    }

    /// <summary>Captures what was actually written to the socket.</summary>
    private sealed class CapturingWebSocket : WebSocket
    {
        /// <summary>Copies of what was sent, for content assertions.</summary>
        public List<byte[]> Sent { get; } = [];

        /// <summary>
        /// The underlying array INSTANCES, so a test can tell one shared buffer from two identical
        /// ones. Recording only copies is how a "we send the same buffer" test ends up proving
        /// nothing, since every implementation produces equal bytes.
        /// </summary>
        public List<byte[]?> SentArrays { get; } = [];

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            SentArrays.Add(buffer.Array);
            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException();
    }

    private static async Task<TelemetryBackgroundService.TelemetrySnapshot> FirstSnapshotAsync()
    {
        var service = new TelemetryBackgroundService(
            new CountingTelemetryService(), NullLogger<TelemetryBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentSnapshot is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.NotNull(service.CurrentSnapshot);
            return service.CurrentSnapshot!;
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheSamplerPublishesBytesAlongsideTheSample()
    {
        // The streams no longer serialize anything, so if the sampler does not produce a frame there
        // is nothing to send and telemetry silently stops.
        var snapshot = await FirstSnapshotAsync();

        Assert.NotNull(snapshot.Payload);
        Assert.False(snapshot.Frame.IsEmpty);
    }

    [Fact]
    public async Task ThePublishedBytesDecodeBackToThePublishedSample()
    {
        // NO TEARING. A frame that does not match the payload it shipped with would send the client
        // numbers from a different tick — no error, just quietly wrong readings.
        var snapshot = await FirstSnapshotAsync();

        var decoded = MessageSerializer.Deserialize(snapshot.Frame.Span);

        Assert.NotNull(decoded);
        Assert.Equal(MessageTypes.Telemetry, decoded!.Type);
        Assert.NotNull(decoded.Telemetry);
        Assert.Equal(
            snapshot.Payload.Sensors.Select(s => (s.Name, s.Value)),
            decoded.Telemetry!.Sensors.Select(s => (s.Name, s.Value)));
    }

    [Fact]
    public async Task SendRawTransmitsTheFrameByteForByte()
    {
        // The whole optimisation rests on the cached bytes going out unmodified. Anything that
        // re-encoded them here would put the per-client cost straight back.
        var snapshot = await FirstSnapshotAsync();
        var socket = new CapturingWebSocket();

        await MessageSerializer.SendRawAsync(socket, snapshot.Frame, CancellationToken.None);

        Assert.Equal(snapshot.Frame.ToArray(), Assert.Single(socket.Sent));
    }

    [Fact]
    public async Task TheSameFrameCanBeSentToSeveralSocketsWithoutBeingRebuilt()
    {
        // The reason this exists: ONE buffer, many recipients. Asserting equal bytes would prove
        // nothing — two independently built frames are equal too, which is exactly the state this
        // change replaced. So assert the ARRAY INSTANCE reaching each socket is the very one the
        // sampler published, which is only true if nothing along the way rebuilt or copied it.
        var snapshot = await FirstSnapshotAsync();
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(snapshot.Frame, out var published));

        var phone = new CapturingWebSocket();
        var dashboard = new CapturingWebSocket();

        await MessageSerializer.SendRawAsync(phone, snapshot.Frame, CancellationToken.None);
        await MessageSerializer.SendRawAsync(dashboard, snapshot.Frame, CancellationToken.None);

        Assert.Same(published.Array, Assert.Single(phone.SentArrays));
        Assert.Same(published.Array, Assert.Single(dashboard.SentArrays));
    }
}
