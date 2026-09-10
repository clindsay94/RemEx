using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// <c>file_volumes_request</c> over a real socket, against a real pairing, in the real host (RemEx-9i4b).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-220r's impersonation test could not be written because this request produced NO response at all
/// in this host — not a deny, not an error, nothing — and it was an open question whether that was a
/// product bug or something about the test host (no real volumes, <c>VolumeEnumerator</c> under
/// TestServer). It was a product bug: the silence came from a request whose <b>body</b> did not bind,
/// which the handler used to answer by returning.
/// </para>
/// <para>
/// SOCKET-LEVEL ON PURPOSE, and this is the whole reason the file exists. Every hop the bug could have
/// been hiding in — the pairing gate, the identity freeze, the detached dispatch off the reader loop,
/// the per-socket send gate, the consent route — is only assembled when a real connection runs through
/// <c>HostBootstrapper</c>. A handler-level test with a fake socket passes with any of them broken.
/// <c>FileTransferHandlerTests.VolumesRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence</c>
/// covers the branch; this covers the claim the bead actually made, which was about the wire. Tighten
/// the dispatch at <c>PingPongHandler</c> to <c>when message.FileVolumesRequest is { }</c> and the
/// handler test still passes while this one fails — which is the failure this bead was filed for.
/// </para>
/// <para>
/// The connection is forced NON-loopback. TestServer leaves <c>RemoteIpAddress</c> null, which the host
/// reads as loopback — the PC's own UI, pairing skipped, telemetry not streamed. That would bypass the
/// pairing gate this exercises and hide the telemetry traffic the response has to share the socket with.
/// </para>
/// <para>
/// PORTED FROM SALVAGE. Two earlier autonomous attempts at this bead were killed by session limits and
/// their work was recovered from <c>.ralph/salvage/RemEx-9i4b/</c>. The salvaged file also carried two
/// consent-path tests; those are not here. One is substantially covered by <c>ConsentRoutingTests</c>,
/// and the other asserts a property belonging to RemEx-220r rather than to this bead — it is tracked
/// separately rather than dropped, because it is NOT covered by RemEx-4215's
/// <c>LoopbackIdentityClaimTests</c> (that one exercises the loopback gate; this is the paired,
/// non-loopback identity freeze, which is a different code path).
/// </para>
/// </remarks>
public sealed class FileVolumesRequestSocketTests
{
    /// <summary>Makes the test connection look like a phone on the LAN rather than the PC's own UI.</summary>
    private sealed class NonLoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
                    return nextMiddleware();
                });
                next(builder);
            };
        }
    }

    [Fact]
    public async Task ARequestWithNoBodyIsAnsweredWithAnErrorRatherThanSilence()
    {
        using var factory = NewHost();
        var pairingService = factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();

        const string clientId = "bodyless-phone";
        using var ws = await ConnectAsync(factory);
        await PairAsync(ws, pairingService, clientId);

        // The wrapper absent: exactly what a peer that spells the body differently across a protocol
        // change puts on the wire, and what used to produce nothing at all.
        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.FileVolumesRequest,
            ClientId = clientId,
        }, CancellationToken.None);

        var (volumes, seen) = await AwaitVolumesResponseAsync(ws);

        Assert.True(volumes is not null, $"no file_volumes_response arrived; saw: {Summarize(seen)}");
        Assert.False(volumes!.FileVolumesResponse!.FullBrowseGranted);
        Assert.Empty(volumes.FileVolumesResponse.Volumes);

        // An error, not a deny. A deny is a decision somebody made; this is the host saying it could not
        // read the question — and the phone shows a failure for one and a closed prompt for the other.
        Assert.False(string.IsNullOrWhiteSpace(volumes.FileVolumesResponse.ErrorMessage));
        Assert.Null(volumes.FileVolumesResponse.DenyReason);
    }

    /// <summary>
    /// Builds the host with a non-loopback connection and a THROWAWAY file-trust store.
    /// </summary>
    /// <remarks>
    /// The store override is not tidiness. <c>FileTrustService</c>'s production path is the machine-wide
    /// <c>C:\ProgramData\RemEx\file_transfer_trust.json</c>, beside the pairing secrets. An earlier
    /// version of this file wrote a real, durable full-browse grant into the developer's own store under
    /// a client id anyone can choose for themselves — caught in review, after it had already happened,
    /// and the reason RemEx-4u29 exists. That redirect now covers this at build level too, so this
    /// override is belt and braces rather than the only defence.
    /// </remarks>
    /// <summary>
    /// The identity freeze RemEx-220r closed with no coverage for, now that the request it needed
    /// answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A paired phone asks while claiming a DIFFERENT paired phone's id, one that already holds a
    /// full-browse grant. The grant must not be inherited: the asker is asked about itself, and told
    /// no.
    /// </para>
    /// <para>
    /// **NOT A DUPLICATE OF RemEx-4215's LoopbackIdentityClaimTests, AND THAT WAS GOT WRONG ONCE
    /// ALREADY (RemEx-xywl).** Those drive the LOOPBACK gate, where identity is never proven and the
    /// fix was to freeze at NO identity. This is the PAIRED non-loopback path, which reaches a
    /// different freeze point entirely — the pairing handshake, and again on reconnect. Reading the
    /// two as the same is why this test was dropped when RemEx-9i4b was finished, leaving the
    /// property believed rather than held.
    /// </para>
    /// <para>
    /// ITS ORIGINAL BLOCKER IS GONE, verified rather than assumed: round 1 of the earlier attempt
    /// failed review for writing a real full-browse grant into the machine-wide trust store. NewHost
    /// above overrides that store with a throwaway path, and RemEx-4u29 landed the host-state
    /// redirect at build level, so this writes nothing outside its own temp directory.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AClaimedClientIdDoesNotInheritAnotherDevicesFullBrowseGrant()
    {
        using var factory = NewHost();
        var pairingService = factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();

        var trust = factory.Services.GetRequiredService<IFileTrustService>();
        var prompts = new List<string>();
        trust.ConsentRequested += prompt =>
        {
            prompts.Add(prompt.ClientId);
            trust.ResolveConsent(prompt.Request.ConsentId, granted: false, remember: false);
        };

        const string victim = "victim-phone";
        const string attacker = "attacker-phone";

        using (var victimWs = await ConnectAsync(factory))
        {
            await PairAsync(victimWs, pairingService, victim);
        }

        await trust.SetFullBrowseGrantedAsync(victim, granted: true, CancellationToken.None);

        using var ws = await ConnectAsync(factory);
        await PairAsync(ws, pairingService, attacker);

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.FileVolumesRequest,
            ClientId = victim,
            FileVolumesRequest = new FileVolumesRequest { RequestId = "vol-1" },
        }, CancellationToken.None);

        var (volumes, seen) = await AwaitVolumesResponseAsync(ws);

        Assert.True(volumes is not null, $"no file_volumes_response arrived; saw: {Summarize(seen)}");
        Assert.False(volumes!.FileVolumesResponse!.FullBrowseGranted);
        Assert.Empty(volumes.FileVolumesResponse.Volumes);

        // The connection is asked about ITSELF. A prompt naming the victim would mean the claimed id
        // reached the consent flow, and an empty list would then be one "allow" away from the victim's
        // drives.
        Assert.Equal([attacker], prompts);
    }

    private static RemexHostFactory NewHost() =>
        new RemexHostFactory().WithServices(services =>
        {
            services.AddSingleton<IStartupFilter, NonLoopbackStartupFilter>();
            services.AddSingleton<IFileTrustService>(sp => new FileTrustService(
                sp.GetRequiredService<ILogger<FileTrustService>>(),
                sp.GetRequiredService<PairedClientRegistry>(),
                sp.GetRequiredService<ClientSessionRegistry>(),
                Path.Combine(Path.GetTempPath(), $"remex-test-trust-{Guid.NewGuid():N}.json"),
                consentTimeout: null));
        });

    private static Task<WebSocket> ConnectAsync(RemexHostFactory factory) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/ws"), CancellationToken.None);

    /// <summary>
    /// Reads until the response arrives, giving up on a budget rather than on a message count — the
    /// telemetry stream ticks once a second and would otherwise decide how long this waits.
    /// </summary>
    private static async Task<(RemexMessage? Response, List<string> Seen)> AwaitVolumesResponseAsync(
        WebSocket ws)
    {
        var seen = new List<string>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            while (seen.Count < 60)
            {
                var message = await MessageSerializer.ReceiveAsync(ws, budget.Token);
                if (message is null) break;
                seen.Add(message.Type);
                if (message.Type == MessageTypes.FileVolumesResponse) return (message, seen);
            }
        }
        catch (OperationCanceledException)
        {
            // The budget ran out. The caller's assertion reports what did arrive, which is the whole
            // diagnostic value of this bead: "telemetry, telemetry, telemetry" is the bug's signature.
        }

        return (null, seen);
    }

    private static string Summarize(List<string> seen) =>
        seen.Count == 0 ? "(nothing at all)" : string.Join(", ", seen.Distinct());

    /// <summary>Drives the real PIN pairing handshake so the connection clears the pairing gate.</summary>
    private static async Task PairAsync(WebSocket ws, PairingService pairingService, string clientId)
    {
        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ClientId = clientId,
            PairingRequest = new PairingRequest
            {
                ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                ClientName = clientId,
                ClientVersion = "2.1.0",
                ClientId = clientId,
            },
        }, CancellationToken.None);

        var pairingResponse = await ReceiveOfTypeAsync(ws, MessageTypes.PairingResponse);
        Assert.NotNull(pairingResponse?.PairingResponse);

        using var hostPeer = ECDiffieHellman.Create();
        hostPeer.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(pairingResponse!.PairingResponse!.HostPublicKeyBase64), out _);

        var sessionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            clientEcdh.DeriveRawSecretAgreement(hostPeer.PublicKey),
            outputLength: 32,
            salt: Convert.FromBase64String(pairingResponse.PairingResponse.CertificateSpkiHashBase64),
            info: System.Text.Encoding.UTF8.GetBytes("remex-pair-v1"));

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.PairingComplete,
            ClientId = clientId,
            PairingComplete = new PairingComplete
            {
                ClientId = clientId,
                ClientPinHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(
                    sessionKey,
                    System.Text.Encoding.UTF8.GetBytes("ack:" + pairingService.GetActivePin()))),
            },
        }, CancellationToken.None);

        var complete = await ReceiveOfTypeAsync(ws, MessageTypes.PairingComplete);
        Assert.True(complete?.CommandSuccess, $"pairing did not complete for {clientId}");
    }

    private static async Task<RemexMessage?> ReceiveOfTypeAsync(WebSocket socket, string type)
    {
        for (var i = 0; i < 16; i++)
        {
            var message = await MessageSerializer.ReceiveAsync(socket, CancellationToken.None);
            if (message is null) return null;
            if (message.Type == type) return message;
        }

        return null;
    }
}
