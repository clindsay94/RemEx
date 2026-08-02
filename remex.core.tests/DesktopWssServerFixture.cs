using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;

namespace Remex.Core.Tests;

/// <summary>
/// A real <c>wss://</c> endpoint on loopback, presenting a certificate whose SPKI hash the test can
/// pin, so <c>RemexDesktopClient</c>'s post-connect paths can be reached at all.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS HAS TO BE A REAL SOCKET (RemEx-u5q0). <c>BuildDesktopUri</c> hardcodes <c>wss://</c> and
/// <c>ConnectAsync</c> installs a validation callback that hashes the presented certificate's
/// SubjectPublicKeyInfo and compares it to the caller's pin. Nothing reaches
/// <c>CompleteReconnectProofAsync</c>, the receive loop, or any other post-connect path without a TLS
/// endpoint whose key the test can hash. Every other test in this assembly points at TEST-NET-1
/// (192.0.2.1) and therefore fails at connect, one step before all of it.
/// </para>
/// <para>
/// KESTREL RATHER THAN A HAND-ROLLED UPGRADE, which is the whole reason this exists now. A previous
/// attempt built a <c>TcpListener</c> + <c>SslStream</c> + hand-written RFC 6455 upgrade and was
/// abandoned: TLS and pinning worked first try, but <c>ClientWebSocket</c> rejected the
/// <c>Sec-WebSocket-Accept</c> header even though its value was independently recomputed and matched
/// byte for byte, and the root cause was never found. That part is not worth re-deriving — Kestrel
/// already implements the upgrade, and <c>UseHttps</c> with an in-memory certificate gives the same
/// pinnable key. The bead's advice was "budget real time for this or use a library rather than
/// hand-rolling"; this is the library.
/// </para>
/// <para>
/// The certificate is generated per fixture and round-tripped through a PFX export. That is not
/// ceremony: a certificate straight out of <see cref="CertificateRequest.CreateSelfSigned"/> carries
/// an ephemeral key that Kestrel refuses on some platforms, and re-importing it produces one it
/// accepts while leaving the public key — and therefore the pin — unchanged.
/// </para>
/// </remarks>
internal sealed class DesktopWssServerFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DesktopWssServerFixture(WebApplication app, int port, string spkiHashBase64)
    {
        _app = app;
        Port = port;
        SpkiHashBase64 = spkiHashBase64;
    }

    /// <summary>The loopback port the endpoint is listening on.</summary>
    public int Port { get; }

    /// <summary>Base64 SHA-256 of the presented certificate's SubjectPublicKeyInfo — the pin to pass.</summary>
    public string SpkiHashBase64 { get; }

    /// <summary>Set once a client has completed the WebSocket upgrade against <c>/ws/desktop</c>.</summary>
    public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Starts the endpoint. <paramref name="onAccepted"/> runs once the socket is open and decides what
    /// the host does next; returning without sending anything is what a silent host looks like.
    /// </summary>
    public static async Task<DesktopWssServerFixture> StartAsync(
        Func<WebSocket, CancellationToken, Task>? onAccepted = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=remex-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Re-import so the private key is one Kestrel will accept; the public key is untouched.
        var certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);

        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        var spkiHash = Convert.ToBase64String(SHA256.HashData(spki));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        var app = builder.Build();
        app.UseWebSockets();

        DesktopWssServerFixture? fixture = null;

        app.Map(RemexConstants.WebSocketPath + "/desktop", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            fixture!.Accepted.TrySetResult();

            if (onAccepted is not null)
            {
                await onAccepted(socket, context.RequestAborted);
            }
            else
            {
                // A host that upgrades and then says nothing — the shape that makes the proof
                // exchange's deadline expire, which is the failure this fixture exists to reach.
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
        });

        await app.StartAsync();

        var port = new Uri(app.Urls.Single()).Port;
        fixture = new DesktopWssServerFixture(app, port, spkiHash);
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }
}
