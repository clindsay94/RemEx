using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Remex.Core.Exceptions;
using Remex.Core.Guards;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services.Command;
using Remex.Core.Services.Security;

namespace Remex.Core.Services.Network;

public class RemexNetworkListener : INetworkListener, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemexNetworkListener> _logger;
    private readonly ISystemCommandService _commandService;
    private readonly IWakeOnLanService _wakeOnLanService;
    private readonly ICertificateService? _certificateService;
    private readonly ICommandChannelAuthenticator? _authenticator;
    private const int MaxPayloadSize = 10 * 1024 * 1024; // 10MB limit for JSON commands

    // Bound the number of concurrent TCP command sessions so a flood of half-open
    // connections cannot exhaust threads/sockets. Configurable via Remex:CommandMaxConcurrent.
    private const int DefaultMaxConcurrentClients = 16;

    // Time budget for the TLS handshake. A client that connects but never completes the
    // handshake otherwise pins a slot indefinitely. Configurable via Remex:CommandHandshakeTimeoutSeconds.
    private const int DefaultHandshakeTimeoutSeconds = 10;

    // Time budget for reading the length-prefixed request payload. Configurable via
    // Remex:CommandReadTimeoutSeconds.
    private const int DefaultReadTimeoutSeconds = 30;

    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private X509Certificate2? _serverCert;
    private SemaphoreSlim? _connectionGate;
    private int _connectionGateMax;
    private TimeSpan _handshakeTimeout;
    private TimeSpan _readTimeout;

    public RemexNetworkListener(
        IConfiguration configuration,
        ILogger<RemexNetworkListener> logger,
        ISystemCommandService commandService,
        IWakeOnLanService wakeOnLanService,
        ICertificateService? certificateService = null,
        ICommandChannelAuthenticator? authenticator = null)
    {
        _configuration = Guard.NotNull(configuration);
        _logger = Guard.NotNull(logger);
        _commandService = Guard.NotNull(commandService);
        _wakeOnLanService = Guard.NotNull(wakeOnLanService);
        _certificateService = certificateService;
        _authenticator = authenticator;
        // Authentication is enforced application-side via _authenticator (paired-client identity);
        // the legacy shared access key was removed in 2.0.
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken)
    {
        var portStr = _configuration["Remex:CommandPort"];
        if (!int.TryParse(portStr, out int port) || port < 1 || port > 65535)
        {
            port = 8338;
        }

        // Bound concurrency + per-connection timeouts (config with sane defaults).
        int maxConcurrent = ReadPositiveIntConfig("Remex:CommandMaxConcurrent", DefaultMaxConcurrentClients);
        _connectionGateMax = maxConcurrent;
        _connectionGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _handshakeTimeout = TimeSpan.FromSeconds(
            ReadPositiveIntConfig("Remex:CommandHandshakeTimeoutSeconds", DefaultHandshakeTimeoutSeconds));
        _readTimeout = TimeSpan.FromSeconds(
            ReadPositiveIntConfig("Remex:CommandReadTimeoutSeconds", DefaultReadTimeoutSeconds));

        // Load TLS certificate for secure TCP connections (Phase 1 Track 1A).
        // FAIL-CLOSED: if the certificate cannot be loaded the listener must not
        // start in plaintext mode.  A silent downgrade would silently strip the
        // entire 2.0 security backbone (TLS + pairing) from any TCP clients.
        if (_certificateService != null)
        {
            try
            {
                _serverCert = await _certificateService.GetOrCreateCertificateAsync(cancellationToken);
                _logger.LogInformation("Loaded TLS certificate for TCP command port");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(
                    ex,
                    "FATAL: Could not load TLS certificate for the TCP command port. " +
                    "Remex refuses to start without TLS — plaintext fallback is not permitted. " +
                    "Check that the certificate store is accessible and try again.");
                throw new InvalidOperationException(
                    "Remex.Agent cannot start without a valid TLS certificate. " +
                    "See inner exception for details.", ex);
            }
        }

        const int maxPortAttempts = 5;
        int actualPort = port;
        TcpListener? listener = null;

        for (int attempt = 0; attempt < maxPortAttempts; attempt++)
        {
            actualPort = port + attempt;
            if (actualPort > 65535) break;
            _logger.LogInformation("Starting external network listener on port {Port}", actualPort);
            try
            {
                listener = new TcpListener(IPAddress.Any, actualPort);
                listener.Start();
                if (attempt > 0)
                {
                    _logger.LogWarning("Primary port {Primary} unavailable; fell back to port {Fallback}", port, actualPort);
                }
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.LogWarning("Port {Port} is already in use. Trying next port...", actualPort);
                listener = null;
            }
        }

        if (listener is null)
        {
            _logger.LogError("Could not bind to any port in range {Start}-{End}. Network listener disabled.", port, port + maxPortAttempts - 1);
            return;
        }

        _tcpListener = listener;

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listenTask = AcceptClientsAsync(_cts.Token);
            await _listenTask;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Socket error in external network listener");
            throw;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error in external network listener");
            throw;
        }
        // Let unexpected exceptions (OutOfMemoryException, etc.) propagate naturally
    }

    public void StopListening()
    {
        _logger.LogInformation("Stopping external network listener");
        // StopListening is invoked multiple times on shutdown: BackgroundService.StopAsync,
        // BackgroundService.Dispose, and this singleton's own Dispose (which also disposes _cts).
        // Once _cts is disposed there is nothing left to cancel — tolerate it so host shutdown
        // completes and releases the listening port / named pipe (a throw here otherwise aborts the
        // remaining hosted-service teardown).
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _tcpListener?.Stop();
        if (_listenTask != null && !_listenTask.IsCompleted)
        {
            try
            {
                _listenTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex, "Error while waiting for listen task to stop");
            }
            catch (ObjectDisposedException)
            {
                // Task was already disposed
            }
        }
        _tcpListener = null;
    }

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        try
        {
            var gate = _connectionGate;
            while (!token.IsCancellationRequested)
            {
                if (_tcpListener == null) break;

                var client = await _tcpListener.AcceptTcpClientAsync(token);

                // Bound concurrency: only spawn a handler if a slot is free. At capacity we
                // reject immediately (close the socket) rather than queueing, so a connection
                // flood cannot pin unbounded resources. The slot is released in the handler's
                // finally block.
                if (gate != null && !await gate.WaitAsync(0, token))
                {
                    _logger.LogWarning(
                        "Command channel at capacity ({Max} concurrent connections); rejecting new connection.",
                        _connectionGateMax);
                    client.Dispose();
                    continue;
                }

                _ = HandleClientSafeAsync(client, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Socket error while accepting client connection");
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("TcpListener was disposed, stopping client acceptance");
        }
        // Let unexpected exceptions propagate
    }

    private async Task HandleClientSafeAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            await HandleClientAsync(client, token);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Network error while handling client");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error while handling client");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Client handling cancelled during shutdown");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            // Top-level safety net for per-client processing
            // Only catch exceptions we can handle; let fatal exceptions propagate
            _logger.LogError(ex, "Unexpected exception in client handler");
        }
        finally
        {
            // Release the concurrency slot acquired in AcceptClientsAsync.
            _connectionGate?.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                Stream stream;

                // Phase 1 Track 1A: Wrap TCP socket in SslStream for TLS 1.3 transport
                if (_serverCert != null)
                {
                    var sslStream = new SslStream(client.GetStream(), false);
                    try
                    {
                        // Bound the handshake: a client that connects but stalls the handshake
                        // would otherwise hold its concurrency slot indefinitely. The token is
                        // threaded into the handshake so CancelAfter actually aborts it.
                        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        handshakeCts.CancelAfter(_handshakeTimeout);
                        var serverAuthOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _serverCert,
                            ClientCertificateRequired = false,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        };
                        await sslStream.AuthenticateAsServerAsync(serverAuthOptions, handshakeCts.Token);
                        stream = sslStream;
                        _logger.LogDebug("TLS handshake completed with TCP client");
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _logger.LogWarning("TLS handshake timed out after {Timeout}s - rejecting connection", _handshakeTimeout.TotalSeconds);
                        await sslStream.DisposeAsync();
                        return;
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "TLS handshake failed with TCP client - rejecting connection");
                        await sslStream.DisposeAsync();
                        return;
                    }
                }
                else
                {
                    // This branch should be unreachable: StartListeningAsync throws if
                    // _serverCert is null.  Log as critical and refuse the connection
                    // rather than silently falling back to plaintext.
                    _logger.LogCritical(
                        "TCP command port has no TLS certificate — rejecting connection. " +
                        "This indicates a programming error; StartListeningAsync should have " +
                        "refused to start without a valid certificate.");
                    return;
                }

                using (stream)
                {
                    // Bound the request read: a client that completes the TLS handshake but then
                    // sends no (or partial) data would otherwise hold its concurrency slot until
                    // shutdown. The timeout applies to reading the length prefix AND the payload.
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(_readTimeout);

                    // 1. Read 4-byte length prefix
                    var lengthBuffer = new byte[4];
                    await stream.ReadExactlyAsync(lengthBuffer, 0, 4, readCts.Token);
                    var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

                    // 2. Validate length
                    if (length <= 0 || length > MaxPayloadSize)
                    {
                        _logger.LogWarning($"Received invalid payload length: {length}. Closing connection.");
                        return;
                    }

                    // 3. Read payload
                    var buffer = new byte[length];
                    await stream.ReadExactlyAsync(buffer, 0, length, readCts.Token);

                    var json = Encoding.UTF8.GetString(buffer);
                    CommandRequest? request = null;
                    try
                    {
                        request = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.CommandRequest);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize command request - invalid JSON format");
                    }
                    catch (NotSupportedException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize command request - unsupported JSON structure");
                    }

                    CommandResponse response;
                    if (request == null)
                    {
                        response = new CommandResponse(false, "Invalid Request", "Payload could not be parsed as CommandRequest.");
                    }
                    else if (!IsRequestAuthenticated(request))
                    {
                        // Default-deny: the 8338 channel uses server-only TLS, so the caller is not
                        // identified by the transport. Commands must carry a ClientId that maps to a
                        // client which has completed PIN-based pairing — mirroring the /ws pairing gate.
                        // Reject before dispatch and close the connection.
                        _logger.LogWarning(
                            "Rejecting command {Action} from unauthenticated TCP client (ClientId='{ClientId}').",
                            request.Action,
                            request.ClientId);
                        response = new CommandResponse(
                            false,
                            "Unauthorized",
                            "Command rejected: the client is not paired. Complete PIN-based pairing before issuing commands.");

                        // Send the rejection, then close the connection (do not dispatch).
                        var unauthorizedJson = RemexJson.Serialize(response, RemexJsonSerializerContext.Default.CommandResponse);
                        var unauthorizedBytes = Encoding.UTF8.GetBytes(unauthorizedJson);
                        var unauthorizedLengthBuffer = new byte[4];
                        BinaryPrimitives.WriteInt32BigEndian(unauthorizedLengthBuffer, unauthorizedBytes.Length);
                        await stream.WriteAsync(unauthorizedLengthBuffer, token);
                        await stream.WriteAsync(unauthorizedBytes, token);
                        return;
                    }
                    else
                    {
                        response = await ExecuteCommandAsync(request);
                    }

                    // 4. Send length-prefixed response
                    var responseJson = RemexJson.Serialize(response, RemexJsonSerializerContext.Default.CommandResponse);
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    var responseLengthBuffer = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(responseLengthBuffer, responseBytes.Length);

                    await stream.WriteAsync(responseLengthBuffer, token);
                    await stream.WriteAsync(responseBytes, token);
                }
            }
        }
        catch (EndOfStreamException)
        {
            _logger.LogDebug("Client closed connection unexpectedly");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Client handling cancelled during shutdown");
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Network error during client communication");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error during client communication");
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Stream or client was disposed during communication");
        }
        // Let unexpected exceptions propagate to HandleClientSafeAsync
    }

    private async Task<CommandResponse> ExecuteCommandAsync(CommandRequest request)
    {
        try
        {
            switch (request.Action.ToUpperInvariant())
            {
                case "SHUTDOWN":
                    await _commandService.Shutdown(ParseDelaySeconds(request.Parameters));
                    return new CommandResponse(true, "Shutdown command executed successfully.", null);
                case "FORCESHUTDOWN":
                    await _commandService.ForceShutdown(ParseDelaySeconds(request.Parameters));
                    return new CommandResponse(true, "Force Shutdown command executed successfully.", null);
                case "RESTART":
                    await _commandService.Restart(ParseDelaySeconds(request.Parameters));
                    return new CommandResponse(true, "Restart command executed successfully.", null);
                case "FORCERESTART":
                    await _commandService.ForceRestart(ParseDelaySeconds(request.Parameters));
                    return new CommandResponse(true, "Force Restart command executed successfully.", null);
                case "RESTARTTOUEFI":
                    await _commandService.RestartToUefi(ParseDelaySeconds(request.Parameters));
                    return new CommandResponse(true, "Restart to UEFI command executed successfully.", null);
                case "SLEEP":
                    await _commandService.Sleep();
                    return new CommandResponse(true, "Sleep command executed successfully.", null);
                case "HIBERNATE":
                    await _commandService.Hibernate();
                    return new CommandResponse(true, "Hibernate command executed successfully.", null);
                case "SIGNOUT":
                    await _commandService.SignOut();
                    return new CommandResponse(true, "SignOut command executed successfully.", null);
                case "LOCK":
                    await _commandService.Lock();
                    return new CommandResponse(true, "Lock command executed successfully.", null);
                case "MONITOROFF":
                    await _commandService.MonitorOff();
                    return new CommandResponse(true, "Monitor off command executed successfully.", null);
                case "WAKEONLAN":
                    if (request.Parameters != null && request.Parameters.TryGetValue("MacAddress", out var mac))
                    {
                        var broadcastIp = request.Parameters.TryGetValue("BroadcastIp", out var bip) ? bip : "255.255.255.255";
                        var port = request.Parameters.TryGetValue("Port", out var pStr) && int.TryParse(pStr, out var p) ? p : 9;
                        await _wakeOnLanService.WakeAsync(mac, broadcastIp, port);
                        return new CommandResponse(true, $"Wake-on-LAN packet sent to {mac}.", null);
                    }
                    else
                    {
                        return new CommandResponse(false, "Missing MacAddress", "Wake-on-LAN requires a MacAddress parameter.");
                    }
                default:
                    return new CommandResponse(false, "Unknown Command", $"Command action '{request.Action}' is not supported.");
            }
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Network error executing command {Action}", request.Action);
            return new CommandResponse(false, "Network Error", $"Command '{request.Action}' failed due to network issue");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation executing command {Action}", request.Action);
            return new CommandResponse(false, "Invalid Operation", ex.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument for command {Action}", request.Action);
            return new CommandResponse(false, "Invalid Argument", ex.Message);
        }
        // Let unexpected exceptions propagate to caller
    }

    // ValidateAccessKey removed in 2.0 — TLS certificate pinning + pairing protocol replaced it.
    // See: master-plan.md Track 1B

    /// <summary>
    /// Gates an inbound command on a paired-client identity. Default-deny: if no authenticator is
    /// wired (e.g. the host has not registered <see cref="ICommandChannelAuthenticator"/>) the
    /// command is rejected, because the 8338 channel carries privileged power commands and must
    /// never run unauthenticated.
    /// </summary>
    private bool IsRequestAuthenticated(CommandRequest request)
    {
        if (_authenticator is null)
        {
            _logger.LogError(
                "No command-channel authenticator is configured; rejecting all TCP commands. " +
                "Remex.Agent must register an ICommandChannelAuthenticator implementation.");
            return false;
        }

        return _authenticator.IsClientAuthenticated(request.ClientId);
    }

    private int ReadPositiveIntConfig(string key, int defaultValue)
    {
        var raw = _configuration[key];
        if (int.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    public void Dispose()
    {
        StopListening();
        _cts?.Dispose();
        _serverCert?.Dispose();
        _connectionGate?.Dispose();
    }

    private static int ParseDelaySeconds(Dictionary<string, string>? parameters)
    {
        if (parameters == null)
        {
            return 0;
        }

        foreach (var key in new[] { "DelaySeconds", "Seconds", "TimerSeconds" })
        {
            if (parameters.TryGetValue(key, out var raw)
                && int.TryParse(raw, out var parsed)
                && parsed > 0)
            {
                return Math.Clamp(parsed, 0, 315360000);
            }
        }

        return 0;
    }
}
