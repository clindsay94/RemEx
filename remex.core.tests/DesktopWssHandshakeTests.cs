using System.Net.WebSockets;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// The post-connect handshake paths, driven over a real <c>wss://</c> socket (RemEx-u5q0).
/// </summary>
/// <remarks>
/// These are the first tests in this assembly that get past <c>ConnectAsync</c> at all. Every other
/// one points at TEST-NET-1 and fails at connect, one step before the proof exchange, the receive
/// loop and the live-socket half of certificate pinning — all security-adjacent code. See
/// <see cref="DesktopWssServerFixture"/> for why a real endpoint is unavoidable and why it is Kestrel.
/// </remarks>
public sealed class DesktopWssHandshakeTests
{
    private const string Host = "127.0.0.1";

    [Fact]
    public async Task AHostThatUpgradesThenGoesQuietIsAHandshakeTimeoutNotACancellation()
    {
        // THE REGRESSION GUARD RemEx-nl0z SHIPPED WITHOUT. The proof exchange has its own deadline
        // linked into the caller's token, so its expiry arrives as a bare OperationCanceledException —
        // indistinguishable from the user deliberately stopping. Deleting the catch that reclassifies
        // it leaves every other test in the repo green, because nothing else could reach this line.
        //
        // The distinction is not cosmetic: one is "you cancelled" and the other is "your PC accepted
        // the connection and then stopped answering", and they carry different advice to the user.
        await using var server = await DesktopWssServerFixture.StartAsync();

        var client = new RemexDesktopClient();
        RemexDesktopClient.ProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(
                Host, server.Port, clientId: "test-client", spkiHash: server.SpkiHashBase64,
                reconnectSecretBase64: Convert.ToBase64String(new byte[32])));

            // Asserted against the exact text rather than a substring, because the sibling catch one
            // call earlier ALSO throws TimeoutException — for a host that never answered at all. A
            // test that only checked the type would pass on either, including on the one where the
            // socket never opened, which is the opposite of what this covers.
            Assert.Equal(RemexDesktopClient.DescribeHandshakeTimeout(Host, server.Port), failure.Message);

            // And prove the socket really did open, so the failure is the proof deadline rather than
            // anything earlier.
            Assert.True(server.Accepted.Task.IsCompletedSuccessfully);
        }
        finally
        {
            RemexDesktopClient.ProofTimeoutOverrideForTests = null;
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task AMismatchedCertificatePinIsRejectedOnTheLiveSocket()
    {
        // Until now only the PRE-socket guard was covered — EnsurePinnedOrThrow, which rejects an
        // absent pin before any I/O. The callback that hashes the certificate actually presented had
        // no coverage, and it is the half that matters: it is the sole authority for TLS trust on
        // this connection (VULN-5), deliberately bypassing the OS trust manager.
        await using var server = await DesktopWssServerFixture.StartAsync();

        var client = new RemexDesktopClient();
        try
        {
            // A syntactically valid pin for a different key.
            var wrongPin = Convert.ToBase64String(new byte[32]);

            await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(
                Host, server.Port, clientId: "test-client", spkiHash: wrongPin,
                reconnectSecretBase64: Convert.ToBase64String(new byte[32])));

            // THE LOAD-BEARING ASSERTION. Not merely "it threw" — that would also pass if the pin were
            // ignored and something later went wrong. The server never saw an upgraded socket, so the
            // rejection happened during TLS, before the WebSocket handshake completed.
            Assert.False(server.Accepted.Task.IsCompleted);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task AMatchingPinReachesTheProofExchange()
    {
        // The control for the test above: with the correct pin the same client gets through TLS and
        // the server DOES see an upgraded socket. Without this, "the pin rejected it" and "the fixture
        // never works" look identical.
        await using var server = await DesktopWssServerFixture.StartAsync(
            onAccepted: async (socket, ct) =>
            {
                // Close immediately: CompleteReconnectProofAsync treats a Close frame as "nothing to
                // prove" and returns, so connect succeeds without needing a real challenge.
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
            });

        var client = new RemexDesktopClient();
        try
        {
            await client.ConnectAsync(
                Host, server.Port, clientId: "test-client", spkiHash: server.SpkiHashBase64,
                reconnectSecretBase64: Convert.ToBase64String(new byte[32]));

            Assert.True(server.Accepted.Task.IsCompletedSuccessfully);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }
}
