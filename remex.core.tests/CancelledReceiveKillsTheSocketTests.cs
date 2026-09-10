using System.Net.WebSockets;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Settles what actually happens to a pairing socket when a read is cancelled (RemEx-d3z9).
/// </summary>
/// <remarks>
/// <para>
/// <c>FetchPairingPinNative</c> documents a NON-DESTRUCTIVE contract: whatever goes wrong
/// auto-fetching the PIN, the pairing session must stay usable so the user can type the PIN in by
/// hand. That fallback is the recovery path for the whole feature.
/// </para>
/// <para>
/// **THE CONTRACT WAS A FICTION, AND THIS IS THE PROOF RATHER THAN THE ARGUMENT.** The claim rests on
/// a framework behaviour — that <see cref="ClientWebSocket"/> registers <c>Abort()</c> on the token
/// passed to <c>ReceiveAsync</c> — which is easy to assert and easy to be wrong about. So it is
/// measured here against a real TLS WebSocket rather than reasoned about, because the whole bead
/// turns on it.
/// </para>
/// </remarks>
public sealed class CancelledReceiveKillsTheSocketTests
{
    [Fact]
    public async Task CancellingAReceiveAbortsTheSocketRatherThanJustEndingTheWait()
    {
        // A server that accepts and then says nothing — the exact shape of a host that never answers
        // a pairing_pin_request, which is what the 5s fetch budget exists for.
        var silence = new TaskCompletionSource();
        await using var server = await DesktopWssServerFixture.StartAsync(async (socket, ct) =>
        {
            // TrySetResult, not SetResult: the test completes this itself on the way out, and the
            // fixture then cancels ct on dispose — so the handler can fire on an already-completed
            // source, and SetResult would throw out of Cancel(). A flaky proof is a weak proof.
            await using var _ = ct.Register(() => silence.TrySetResult());
            await silence.Task;
        });

        using var client = new ClientWebSocket();
        client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await client.ConnectAsync(new Uri($"wss://127.0.0.1:{server.Port}/ws/desktop"), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, client.State);

        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var buffer = new byte[64];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReceiveAsync(new ArraySegment<byte>(buffer), budget.Token));

        // THE FINDING. Not Open, not CloseReceived — Aborted. The socket is gone, so anything the
        // caller does with it afterwards fails, and "the session stays valid so the user can type the
        // PIN in manually" cannot be true.
        Assert.Equal(WebSocketState.Aborted, client.State);

        silence.TrySetResult();
    }

    [Fact]
    public async Task AndTheDeadSocketRejectsTheVeryNextSend()
    {
        // Which is what the manual-PIN fallback would do: send a PairingComplete on the socket the
        // abandoned fetch left behind. It fails with a transport error, so the export's general
        // handler reports UNEXPECTED and the user is told "unknown error" instead of being told the
        // session is gone and to start again.
        var silence = new TaskCompletionSource();
        await using var server = await DesktopWssServerFixture.StartAsync(async (socket, ct) =>
        {
            await using var _ = ct.Register(() => silence.TrySetResult());
            await silence.Task;
        });

        using var client = new ClientWebSocket();
        client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await client.ConnectAsync(new Uri($"wss://127.0.0.1:{server.Port}/ws/desktop"), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, client.State);

        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), budget.Token));

        var send = await Record.ExceptionAsync(() => client.SendAsync(
            new ArraySegment<byte>([1, 2, 3]), WebSocketMessageType.Text, true, CancellationToken.None));

        Assert.NotNull(send);
        Assert.True(
            send is WebSocketException or ObjectDisposedException,
            $"expected a transport failure, got {send.GetType().Name}");

        silence.TrySetResult();
    }
}
