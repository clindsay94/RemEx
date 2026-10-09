using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Agent.Tests;

/// <summary>
/// VULN-2 (RemEx-s032.2): the challenge/response round trip of <see cref="ChannelReconnectAuth"/>.
/// The crypto core is covered in <see cref="ChannelReconnectAuthTests"/>; these pin the socket flow
/// around it, and that every way the client can fail to answer ends in "not authenticated".
/// </summary>
public sealed class ChannelReconnectChallengeTests
{
    private const string ClientId = "client-a";

    private sealed record Frame(byte[] Bytes, WebSocketMessageType Type = WebSocketMessageType.Text);

    private sealed class ScriptedSocket(Func<ScriptedSocket, CancellationToken, Task<Frame>> onReceive) : WebSocket
    {
        public List<RemexMessage> Sent { get; } = [];

        public byte[] ChallengeNonce =>
            Convert.FromBase64String(Sent[0].ReconnectChallenge!.NonceBase64);

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            Sent.Add(MessageSerializer.Deserialize(buffer.AsSpan())!);
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var frame = await onReceive(this, ct);
            if (frame.Type == WebSocketMessageType.Close)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            frame.Bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(frame.Bytes.Length, frame.Type, true);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
    }

    private static PairedClientRegistry NewRegistry(string clientId, byte[] secret)
    {
        var dir = Directory.CreateTempSubdirectory();
        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(dir.FullName, "paired_clients.json"));
        registry.RegisterClient(clientId, secret);
        return registry;
    }

    private static Frame ProofFrame(string? proofBase64, string bodyClientId = ClientId) =>
        new(MessageSerializer.Serialize(new RemexMessage
        {
            Type = MessageTypes.ReconnectProof,
            ReconnectProof = new ReconnectProof { ClientId = bodyClientId, ProofHmacBase64 = proofBase64! },
        }));

    private static string Hmac(byte[] secret, byte[] nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(secret, nonce));

    private static Task<bool> RunAsync(
        ScriptedSocket ws, PairedClientRegistry registry, CancellationToken ct = default) =>
        ChannelReconnectAuth.ChallengeAndVerifyAsync(ws, ClientId, registry, NullLogger.Instance, ct);

    [Fact]
    public async Task Challenge_ValidProofOverTheIssuedNonce_Authenticates()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secret);
        var ws = new ScriptedSocket((s, _) => Task.FromResult(ProofFrame(Hmac(secret, s.ChallengeNonce))));

        var ok = await RunAsync(ws, registry);

        Assert.True(ok);
        var challenge = Assert.Single(ws.Sent);
        Assert.Equal(MessageTypes.ReconnectChallenge, challenge.Type);
        Assert.Equal(32, ws.ChallengeNonce.Length);
    }

    [Fact]
    public async Task Challenge_EachCallIssuesAFreshNonce()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secret);
        var first = new ScriptedSocket((s, _) => Task.FromResult(ProofFrame(Hmac(secret, s.ChallengeNonce))));
        var second = new ScriptedSocket((s, _) => Task.FromResult(ProofFrame(Hmac(secret, s.ChallengeNonce))));

        await RunAsync(first, registry);
        await RunAsync(second, registry);

        Assert.NotEqual(first.ChallengeNonce, second.ChallengeNonce);
    }

    [Fact]
    public async Task Challenge_ProofReplayedFromAnEarlierChallenge_IsRejected()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secret);
        string? captured = null;
        var firstSession = new ScriptedSocket((s, _) =>
        {
            captured = Hmac(secret, s.ChallengeNonce);
            return Task.FromResult(ProofFrame(captured));
        });
        Assert.True(await RunAsync(firstSession, registry));

        var replaySession = new ScriptedSocket((_, _) => Task.FromResult(ProofFrame(captured)));

        Assert.False(await RunAsync(replaySession, registry));
    }

    [Fact]
    public async Task Challenge_ProofWithWrongSecret_IsRejected()
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var wrong = RandomNumberGenerator.GetBytes(32);
        var ws = new ScriptedSocket((s, _) => Task.FromResult(ProofFrame(Hmac(wrong, s.ChallengeNonce))));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public async Task Challenge_ProofOverADifferentNonce_IsRejected()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secret);
        var ws = new ScriptedSocket((_, _) =>
            Task.FromResult(ProofFrame(Hmac(secret, RandomNumberGenerator.GetBytes(32)))));

        Assert.False(await RunAsync(ws, registry));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Challenge_BlankProof_IsRejected(string? blank)
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) => Task.FromResult(ProofFrame(blank)));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public void Verify_BlankProof_IsRejectedBeforeTheSecretIsLookedUp()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secret);

        var ok = ChannelReconnectAuth.Verify(
            ClientId, RandomNumberGenerator.GetBytes(32),
            new ReconnectProof { ClientId = ClientId, ProofHmacBase64 = "  " },
            registry, NullLogger.Instance);

        Assert.False(ok);
    }

    [Fact]
    public async Task Challenge_ProofBodyClaimingAnotherClientId_StillVerifiesAgainstTheDialedIdentity()
    {
        var secretA = RandomNumberGenerator.GetBytes(32);
        var secretB = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secretA);
        registry.RegisterClient("client-b", secretB);

        // Client B's valid proof, while dialing as client A, must not authenticate A's socket.
        var asB = new ScriptedSocket((s, _) =>
            Task.FromResult(ProofFrame(Hmac(secretB, s.ChallengeNonce), bodyClientId: "client-b")));
        Assert.False(await RunAsync(asB, registry));

        // And a body that names someone else is ignored when the proof is A's own.
        var asA = new ScriptedSocket((s, _) =>
            Task.FromResult(ProofFrame(Hmac(secretA, s.ChallengeNonce), bodyClientId: "client-b")));
        Assert.True(await RunAsync(asA, registry));
    }

    [Fact]
    public async Task Challenge_ClientNotInRegistry_IsRejected()
    {
        var registry = NewRegistry("someone-else", RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) =>
            Task.FromResult(ProofFrame(Hmac(RandomNumberGenerator.GetBytes(32), new byte[32]))));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public async Task Challenge_WrongMessageType_IsRejected()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = NewRegistry(ClientId, secret);
        var ws = new ScriptedSocket((s, _) => Task.FromResult(new Frame(MessageSerializer.Serialize(new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            // A correct proof payload under the wrong frame type must not be honoured.
            ReconnectProof = new ReconnectProof { ClientId = ClientId, ProofHmacBase64 = Hmac(secret, s.ChallengeNonce) },
        }))));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public async Task Challenge_ProofTypeWithNoPayload_IsRejected()
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) => Task.FromResult(new Frame(MessageSerializer.Serialize(
            new RemexMessage { Type = MessageTypes.ReconnectProof }))));

        Assert.False(await RunAsync(ws, registry));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"type\":")]
    [InlineData("")]
    public async Task Challenge_MalformedTextFrame_IsRejected(string text)
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) => Task.FromResult(new Frame(System.Text.Encoding.UTF8.GetBytes(text))));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public async Task Challenge_BinaryGarbageFrame_IsRejected()
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) =>
            Task.FromResult(new Frame([0xFF, 0x00, 0xFE, 0x01], WebSocketMessageType.Binary)));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public async Task Challenge_ClientClosesInsteadOfAnswering_IsRejected()
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) => Task.FromResult(new Frame([], WebSocketMessageType.Close)));

        Assert.False(await RunAsync(ws, registry));
    }

    [Fact]
    public async Task Challenge_CancelledWhileAwaitingProof_ReturnsFalseInsteadOfThrowing()
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        using var cts = new CancellationTokenSource();
        var ws = new ScriptedSocket(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return default!;
        });

        var ok = await RunAsync(ws, registry, cts.Token);

        Assert.False(ok);
    }

    [Fact]
    public async Task Challenge_SocketFaultsMidHandshake_NeverAuthenticates()
    {
        var registry = NewRegistry(ClientId, RandomNumberGenerator.GetBytes(32));
        var ws = new ScriptedSocket((_, _) =>
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely));

        // The caller sits inside a try/catch for the accept, so a fault may surface as an exception;
        // what must never happen is a true result.
        var authenticated = false;
        try
        {
            authenticated = await RunAsync(ws, registry);
        }
        catch (WebSocketException)
        {
        }

        Assert.False(authenticated);
    }
}
