using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// The Android pairing handshake client (<see cref="PairingClient"/>), driven against a scripted host
/// that performs the real ECDH/HKDF/HMAC maths. Pins the security contract: the client only
/// acknowledges a host that proves the PIN, key material never outlives an attempt, and a dead or
/// hostile socket can never produce a "paired" result.
/// </summary>
public sealed class PairingClientTests
{
    private const string Pin = "482913";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private sealed class HostSocket : WebSocket
    {
        private readonly Queue<byte[]> _inbox = new();
        private bool _closeDelivered;

        public List<RemexMessage> Sent { get; } = [];
        public Func<RemexMessage, IEnumerable<RemexMessage>> OnSend { get; set; } = _ => [];
        public bool HangWhenEmpty { get; set; }
        public Action? BeforeHang { get; set; }
        public int ReceiveCalls { get; private set; }

        public void Enqueue(RemexMessage message) => _inbox.Enqueue(MessageSerializer.Serialize(message));
        public void EnqueueRaw(string text) => _inbox.Enqueue(Encoding.UTF8.GetBytes(text));

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan())!;
            Sent.Add(message);
            foreach (var reply in OnSend(message))
            {
                Enqueue(reply);
            }

            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ReceiveCalls++;
            if (_inbox.TryDequeue(out var bytes))
            {
                bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }

            if (HangWhenEmpty)
            {
                BeforeHang?.Invoke();
                await Task.Delay(Timeout.Infinite, ct);
            }

            // A real socket reports Close once, then rejects further reads.
            if (_closeDelivered)
            {
                throw new InvalidOperationException("Socket is closed.");
            }

            _closeDelivered = true;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _closeDelivered ? WebSocketState.CloseReceived : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
    }

    /// <summary>The host half of the handshake, using the same derivation the client must match.</summary>
    private sealed class FakeHost
    {
        private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _spkiHash = RandomNumberGenerator.GetBytes(32);

        public byte[]? SessionKey { get; private set; }

        public PairingResponse RespondTo(PairingRequest request, string pin = Pin)
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.ClientPublicKeyBase64), out _);
            var shared = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            SessionKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256, shared, 32, _spkiHash, Encoding.UTF8.GetBytes("remex-pair-v1"));

            return new PairingResponse
            {
                HostPublicKeyBase64 = Convert.ToBase64String(_ecdh.PublicKey.ExportSubjectPublicKeyInfo()),
                HostId = "host-1",
                HostName = "Test host",
                CertificateSpkiHashBase64 = Convert.ToBase64String(_spkiHash),
                PinHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(SessionKey, Encoding.UTF8.GetBytes(pin))),
            };
        }

        public byte[] ExpectedAck(string pin = Pin) =>
            HMACSHA256.HashData(SessionKey!, Encoding.UTF8.GetBytes("ack:" + pin));
    }

    private static RemexMessage Msg(string type, Action<Builder>? configure = null)
    {
        var b = new Builder();
        configure?.Invoke(b);
        return new RemexMessage
        {
            Type = type,
            PairingResponse = b.Response,
            CommandSuccess = b.Success,
            ErrorText = b.Error,
            PairingPin = b.PinInfo,
        };
    }

    private sealed class Builder
    {
        public PairingResponse? Response { get; set; }
        public bool? Success { get; set; }
        public string? Error { get; set; }
        public PairingPinInfo? PinInfo { get; set; }
    }

    private static Task<T> Bounded<T>(Task<T> task) => task.WaitAsync(Budget);

    /// <summary>Runs Start against a host that answers with a correct response; returns the pieces.</summary>
    private static async Task<(PairingClient Client, HostSocket Socket, FakeHost Host, PairingResponse Response)>
        StartedAsync(string pin = Pin)
    {
        var host = new FakeHost();
        var socket = new HostSocket();
        socket.OnSend = sent => sent.Type == MessageTypes.PairingRequest
            ? [Msg(MessageTypes.PairingResponse, b => b.Response = host.RespondTo(sent.PairingRequest!, pin))]
            : [];
        var client = new PairingClient(socket) { ClientId = "phone-1" };

        var response = await Bounded(client.StartPairingAsync("Pixel", "1.0", CancellationToken.None));
        return (client, socket, host, response!);
    }

    // ── StartPairingAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Start_SendsAV2RequestWithAFreshP256KeyAndReturnsTheHostResponse()
    {
        var (_, socket, _, response) = await StartedAsync();

        var request = Assert.Single(socket.Sent);
        Assert.Equal(MessageTypes.PairingRequest, request.Type);
        Assert.Equal(2, request.ProtocolVersion);
        Assert.Equal("phone-1", request.ClientId);
        Assert.Equal("Pixel", request.PairingRequest!.ClientName);
        Assert.Equal("phone-1", request.PairingRequest.ClientId);
        using var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PairingRequest.ClientPublicKeyBase64), out _);
        Assert.Equal(256, key.KeySize);
        Assert.Equal("host-1", response.HostId);
    }

    [Fact]
    public async Task Start_UsesADifferentEphemeralKeyEveryAttempt()
    {
        var first = new HostSocket();
        var second = new HostSocket();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await new PairingClient(first).StartPairingAsync("a", "1", cts.Token);
        await new PairingClient(second).StartPairingAsync("a", "1", cts.Token);

        Assert.NotEqual(
            first.Sent[0].PairingRequest!.ClientPublicKeyBase64,
            second.Sent[0].PairingRequest!.ClientPublicKeyBase64);
    }

    [Fact]
    public async Task Start_SkipsUnrelatedAndMalformedFramesUntilThePairingResponseArrives()
    {
        var host = new FakeHost();
        var socket = new HostSocket();
        socket.OnSend = sent =>
        {
            socket.EnqueueRaw("{ not json");
            socket.Enqueue(Msg(MessageTypes.PairingError));
            return [Msg(MessageTypes.PairingResponse, b => b.Response = host.RespondTo(sent.PairingRequest!))];
        };
        var client = new PairingClient(socket);

        var response = await Bounded(client.StartPairingAsync("Pixel", "1.0", CancellationToken.None));

        Assert.NotNull(response);
        Assert.Equal("host-1", response!.HostId);
    }

    [Fact]
    public async Task Start_AlreadyCancelled_ReturnsNullWithoutWaitingOnTheSocket()
    {
        var socket = new HostSocket();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await Bounded(new PairingClient(socket).StartPairingAsync("Pixel", "1.0", cts.Token));

        Assert.Null(response);
        Assert.Equal(0, socket.ReceiveCalls);
    }

    [Fact]
    public async Task Start_CancelledWhileWaitingForTheHost_Throws()
    {
        var socket = new HostSocket { HangWhenEmpty = true };
        using var cts = new CancellationTokenSource();
        socket.BeforeHang = cts.Cancel;

        var act = () => Bounded(new PairingClient(socket).StartPairingAsync("Pixel", "1.0", cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task Start_HostClosesBeforeResponding_ReturnsNullOnTheCloseFrameWithoutReadingAgain()
    {
        var socket = new HostSocket();

        var response = await Bounded(new PairingClient(socket).StartPairingAsync("Pixel", "1.0", CancellationToken.None));

        Assert.Null(response);
        Assert.Equal(1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task Start_StrayFramesThenClose_ReturnsNullOnTheCloseFrame()
    {
        var socket = new HostSocket();
        socket.OnSend = _ =>
        {
            socket.EnqueueRaw("{ not json");
            return [Msg(MessageTypes.PairingError)];
        };

        var response = await Bounded(new PairingClient(socket).StartPairingAsync("Pixel", "1.0", CancellationToken.None));

        Assert.Null(response);
        Assert.Equal(3, socket.ReceiveCalls);
    }

    // ── CompletePairingAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Complete_BeforeStart_ReturnsFalseAndSendsNothing()
    {
        var socket = new HostSocket();
        var response = new PairingResponse
        {
            HostPublicKeyBase64 = "x", HostId = "h", HostName = "h", CertificateSpkiHashBase64 = "x", PinHmacBase64 = "x",
        };

        var ok = await Bounded(new PairingClient(socket).CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.False(ok);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task Complete_CorrectPinAndHostConfirmation_PairsAndExposesTheSharedSecret()
    {
        var (client, socket, host, response) = await StartedAsync();
        socket.OnSend = sent => sent.Type == MessageTypes.PairingComplete
            ? [Msg(MessageTypes.PairingComplete, b => b.Success = true)]
            : [];

        var ok = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.True(ok);
        Assert.Equal(Convert.ToBase64String(host.SessionKey!), client.LastReconnectSecretBase64);
        var ack = socket.Sent.Last();
        Assert.Equal(MessageTypes.PairingComplete, ack.Type);
        Assert.Equal(2, ack.ProtocolVersion);
        Assert.Equal("phone-1", ack.PairingComplete!.ClientId);
        Assert.Equal(host.ExpectedAck(), Convert.FromBase64String(ack.PairingComplete.ClientPinHmacBase64));
    }

    [Fact]
    public async Task Complete_WrongPin_RejectsWithoutSendingAnAckOrKeepingASecret()
    {
        var (client, socket, _, response) = await StartedAsync();
        var sentBefore = socket.Sent.Count;

        var ok = await Bounded(client.CompletePairingAsync("000000", response, CancellationToken.None));

        Assert.False(ok);
        Assert.Equal(sentBefore, socket.Sent.Count);
        Assert.Null(client.LastReconnectSecretBase64);
    }

    [Fact]
    public async Task Complete_HostPinHmacTampered_RejectsWithoutSendingAnAck()
    {
        var (client, socket, _, response) = await StartedAsync();
        var forged = Convert.FromBase64String(response.PinHmacBase64);
        forged[0] ^= 0xFF;
        var sentBefore = socket.Sent.Count;

        var ok = await Bounded(client.CompletePairingAsync(
            Pin, response with { PinHmacBase64 = Convert.ToBase64String(forged) }, CancellationToken.None));

        Assert.False(ok);
        Assert.Equal(sentBefore, socket.Sent.Count);
    }

    [Fact]
    public async Task Complete_HostAnswersWithPairingError_ReturnsFalseAndNoSecret()
    {
        var (client, socket, _, response) = await StartedAsync();
        socket.OnSend = sent => sent.Type == MessageTypes.PairingComplete
            ? [Msg(MessageTypes.PairingError, b => b.Error = "pin expired")]
            : [];

        var ok = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.False(ok);
        Assert.Null(client.LastReconnectSecretBase64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Complete_HostConfirmationWithoutExplicitSuccess_IsNotPaired(bool? commandSuccess)
    {
        var (client, socket, _, response) = await StartedAsync();
        socket.OnSend = sent => sent.Type == MessageTypes.PairingComplete
            ? [Msg(MessageTypes.PairingComplete, b => b.Success = commandSuccess)]
            : [];

        var ok = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.False(ok);
        Assert.Null(client.LastReconnectSecretBase64);
    }

    [Fact]
    public async Task Complete_IgnoresStrayFramesWhileAwaitingConfirmation()
    {
        var (client, socket, _, response) = await StartedAsync();
        socket.OnSend = sent => sent.Type == MessageTypes.PairingComplete
            ?
            [
                Msg(MessageTypes.PairingResponse, b => b.Response = response),
                Msg(MessageTypes.PairingComplete, b => b.Success = true),
            ]
            : [];

        var ok = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.True(ok);
    }

    [Fact]
    public async Task Complete_HostClosesAfterTheAck_ReturnsFalseOnTheCloseFrameWithoutReadingAgain()
    {
        var (client, socket, _, response) = await StartedAsync();
        var readsBefore = socket.ReceiveCalls;

        var ok = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.False(ok);
        Assert.Null(client.LastReconnectSecretBase64);
        Assert.Equal(readsBefore + 1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task Complete_CancelledWhileAwaitingConfirmation_Throws()
    {
        var (client, socket, _, response) = await StartedAsync();
        socket.HangWhenEmpty = true;
        using var cts = new CancellationTokenSource();
        socket.OnSend = _ => [];
        socket.BeforeHang = cts.Cancel;

        var act = () => Bounded(client.CompletePairingAsync(Pin, response, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Null(client.LastReconnectSecretBase64);
    }

    [Fact]
    public async Task Complete_MalformedHostPayload_ThrowsAndDiscardsTheKeyPairForGood()
    {
        var (client, socket, _, response) = await StartedAsync();
        var sentBefore = socket.Sent.Count;

        var act = () => Bounded(client.CompletePairingAsync(
            Pin, response with { PinHmacBase64 = "***not-base64***" }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<FormatException>(act);

        // The ephemeral key is gone, so the same (even valid) response cannot be replayed.
        var retry = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));
        Assert.False(retry);
        Assert.Equal(sentBefore, socket.Sent.Count);
    }

    [Fact]
    public async Task Complete_HostPublicKeyIsNotAValidKey_Throws()
    {
        var (client, _, _, response) = await StartedAsync();

        var act = () => Bounded(client.CompletePairingAsync(
            Pin, response with { HostPublicKeyBase64 = Convert.ToBase64String(new byte[32]) }, CancellationToken.None));

        await Assert.ThrowsAnyAsync<CryptographicException>(act);
    }

    [Fact]
    public async Task Complete_AfterSuccess_SecondAttemptWithSameResponseIsRefused()
    {
        var (client, socket, _, response) = await StartedAsync();
        socket.OnSend = sent => sent.Type == MessageTypes.PairingComplete
            ? [Msg(MessageTypes.PairingComplete, b => b.Success = true)]
            : [];
        Assert.True((await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None))));
        var sentBefore = socket.Sent.Count;

        var again = await Bounded(client.CompletePairingAsync(Pin, response, CancellationToken.None));

        Assert.False(again);
        Assert.Equal(sentBefore, socket.Sent.Count);
    }

    // ── RequestPinAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task RequestPin_ReturnsTheHostsPin_AndTagsTheRequestForCorrelation()
    {
        var socket = new HostSocket();
        socket.OnSend = _ => [Msg(MessageTypes.PairingPinResponse, b => b.PinInfo = new PairingPinInfo(Pin, 1234))];
        var client = new PairingClient(socket) { ClientId = "phone-1" };

        var info = await Bounded(client.RequestPinAsync(CancellationToken.None));

        Assert.Equal(new PairingPinInfo(Pin, 1234), info);
        var request = Assert.Single(socket.Sent);
        Assert.Equal(MessageTypes.PairingPinRequest, request.Type);
        Assert.Equal("phone-1", request.ClientId);
        Assert.False(string.IsNullOrWhiteSpace(request.CorrelationId));
    }

    [Fact]
    public async Task RequestPin_HostDeclines_ReturnsNull()
    {
        var socket = new HostSocket();
        socket.OnSend = _ => [Msg(MessageTypes.PairingPinResponse)];

        var info = await Bounded(new PairingClient(socket).RequestPinAsync(CancellationToken.None));

        Assert.Null(info);
    }

    [Fact]
    public async Task RequestPin_SkipsStrayFramesBeforeThePinResponse()
    {
        var socket = new HostSocket();
        socket.OnSend = _ =>
        [
            Msg(MessageTypes.PairingError),
            Msg(MessageTypes.PairingPinResponse, b => b.PinInfo = new PairingPinInfo(Pin, 9)),
        ];

        var info = await Bounded(new PairingClient(socket).RequestPinAsync(CancellationToken.None));

        Assert.Equal(Pin, info!.Pin);
    }

    [Fact]
    public async Task RequestPin_SocketClosesFirst_ReturnsNullWithoutReadingAgain()
    {
        var socket = new HostSocket();

        var info = await Bounded(new PairingClient(socket).RequestPinAsync(CancellationToken.None));

        Assert.Null(info);
        Assert.Equal(1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task RequestPin_AlreadyCancelled_ReturnsNullWithoutReading()
    {
        var socket = new HostSocket();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var info = await Bounded(new PairingClient(socket).RequestPinAsync(cts.Token));

        Assert.Null(info);
        Assert.Equal(0, socket.ReceiveCalls);
    }

    [Fact]
    public async Task RequestPin_CancelledWhileWaiting_Throws()
    {
        var socket = new HostSocket { HangWhenEmpty = true };
        using var cts = new CancellationTokenSource();
        socket.BeforeHang = cts.Cancel;

        var act = () => Bounded(new PairingClient(socket).RequestPinAsync(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }
}
