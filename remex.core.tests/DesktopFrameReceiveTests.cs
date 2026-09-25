using System.Net.WebSockets;
using Remex.Core.Messages;
using Remex.Core.Native;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Perf audit P3-1: <c>ReceiveLoopAsync</c> used to accumulate every message - single fragment or
/// not - through a fresh, uncapped <see cref="MemoryStream"/> before <c>ToArray()</c>. Proven over a
/// real <c>wss://</c> socket, like <see cref="DesktopWssHandshakeTests"/> - <see cref="RemexDesktopClient.FrameReceived"/>
/// only fires from inside the receive loop, so there is no seam to drive it without one.
/// </summary>
public sealed class DesktopFrameReceiveTests
{
    private const string Host = "127.0.0.1";

    /// <summary>
    /// Sent as the connection's first frame in every test here, before any real desktop-stream frame.
    /// These tests pass no reconnect secret, so <c>RemexDesktopClient.CompleteReconnectProofAsync</c>
    /// only actually reads a frame if a PRIOR test in this process left one set on the static
    /// <c>RemexNativeClient.Current</c> singleton - safe either way: if it does read one, this valid-
    /// but-not-a-reconnect_challenge message is treated as "the host did not challenge" and the proof
    /// exchange returns without touching the socket again; if it doesn't, this message instead reaches
    /// <c>ReceiveLoopAsync</c> directly, whose Text-branch dispatch has no case for it and silently
    /// ignores it. Either way, the frame sent after this one is what the receive loop picks up next.
    /// </summary>
    private static async Task SkipProofExchangeAsync(WebSocket socket, CancellationToken ct)
    {
        // Must be VALID JSON (just not a reconnect_challenge), not arbitrary bytes: a malformed
        // payload throws inside RemexJson.Deserialize, which propagates out of the proof exchange
        // and kills the whole receive loop before it ever reaches the frame this test actually cares
        // about - a real failure mode this helper exists to avoid tripping over.
        var bytes = MessageSerializer.Serialize(new RemexMessage { Type = "noop" });
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    [Fact]
    public async Task ASingleFragmentBinaryFrame_ArrivesByteForByteAtFrameReceived()
    {
        // The common case this row is about: one WebSocket fragment, well under the 256KB receive
        // buffer, must reach FrameReceived unchanged after the MemoryStream-skipping fast path.
        var frame = new byte[50_000];
        Random.Shared.NextBytes(frame);
        // Never let a random fill coincide with the "RDXC" cursor-binary magic.
        frame[0] = 0x00; frame[1] = 0x00; frame[2] = 0x00; frame[3] = 0x01;

        var frameReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await DesktopWssServerFixture.StartAsync(onAccepted: async (socket, ct) =>
        {
            await SkipProofExchangeAsync(socket, ct);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        });

        var client = new RemexDesktopClient();
        client.FrameReceived += bytes => frameReceived.TrySetResult(bytes);
        try
        {
            await client.ConnectAsync(Host, server.Port, clientId: "test-client", spkiHash: server.SpkiHashBase64);

            var received = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(frame, received);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task AMultiFragmentBinaryFrame_IsStillReassembledCorrectly()
    {
        // The path this row's fix must NOT break: a message spanning more than one WebSocket
        // fragment still has to reassemble byte-for-byte through the sized-accumulator path.
        var frame = new byte[600_000]; // several times the 256KB receive buffer -> forces fragmentation
        Random.Shared.NextBytes(frame);
        frame[0] = 0x00; frame[1] = 0x00; frame[2] = 0x00; frame[3] = 0x01;

        var frameReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await DesktopWssServerFixture.StartAsync(onAccepted: async (socket, ct) =>
        {
            await SkipProofExchangeAsync(socket, ct);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        });

        var client = new RemexDesktopClient();
        client.FrameReceived += bytes => frameReceived.TrySetResult(bytes);
        try
        {
            await client.ConnectAsync(Host, server.Port, clientId: "test-client", spkiHash: server.SpkiHashBase64);

            var received = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(frame, received);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ACursorBinaryPacket_IsRoutedAwayFromFrameReceived()
    {
        // Demux still has to work on the fast (single-fragment) path: an "RDXC"-prefixed packet
        // must reach CursorBinaryReceived, never FrameReceived.
        var packet = new byte[32];
        "RDXC"u8.CopyTo(packet);

        var cursorReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameReceivedUnexpectedly = false;

        await using var server = await DesktopWssServerFixture.StartAsync(onAccepted: async (socket, ct) =>
        {
            await SkipProofExchangeAsync(socket, ct);
            await socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, ct);
        });

        var client = new RemexDesktopClient();
        client.CursorBinaryReceived += bytes => cursorReceived.TrySetResult(bytes);
        client.FrameReceived += _ => frameReceivedUnexpectedly = true;
        try
        {
            await client.ConnectAsync(Host, server.Port, clientId: "test-client", spkiHash: server.SpkiHashBase64);

            var received = await cursorReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(packet, received);
            Assert.False(frameReceivedUnexpectedly, "an RDXC-tagged packet must never also fire FrameReceived");
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }
}
