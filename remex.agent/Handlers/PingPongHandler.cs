using System.Net.WebSockets;
using System.Security.Cryptography;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Telemetry;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Handlers;

/// <summary>
/// Handles a single WebSocket client session.
/// Responds to "ping" with "pong", echoing the client's timestamp for latency measurement.
/// Background streams telemetry data while the connection is established.
/// </summary>
public sealed class PingPongHandler(
    ILogger<PingPongHandler> logger,
    TelemetryBackgroundService telemetryBackgroundService,
    Remex.Core.Services.Command.ISystemCommandService commandService,
    Remex.Core.Services.Network.IWakeOnLanService wakeOnLanService,
    Remex.Core.Services.ILauncherStorageService launcherStorage,
    Remex.Core.Services.IAppLauncherService appLauncherService,
    Remex.Core.Services.IDashboardProfileStorageService profileStorage,
    Remex.Core.Services.IProcessMonitorService processMonitorService,
    IHostCapabilitiesProvider hostCapabilitiesProvider,
    IInputSimulationService inputSimulation,
    PairingHandler pairingHandler,
    FileTransferHandler fileTransferHandler,
    TransferSessionManager transferSessionManager,
    PairedClientRegistry pairedClientRegistry)
{
    public async Task HandleAsync(WebSocket webSocket, bool isLoopback, bool isTrustedForPinAutoFetch, CancellationToken ct)
    {
        // Per-connection pairing gate. Loopback connections come from the embedded host on the
        // same machine, where pairing adds no security and is intentionally skipped on the client
        // side as well — see ConnectionViewModel.IsLoopbackHost. All other connections must
        // complete the PIN-based pairing handshake before issuing destructive or stateful commands.
        // We also check the registry to see if this client (by ID) has previously paired.
        bool isPaired = isLoopback;
        bool pairingStarted = false;

        // PAIR-1 proof-of-possession reconnect state. When a known paired client reconnects we
        // send a one-time random nonce and require HMAC-SHA256(reconnectSecret, nonce) back before
        // trusting the connection. A bare clientId no longer authenticates.
        byte[]? pendingChallengeNonce = null;
        bool challengeIssued = false;

        // Last clientId seen on this connection, tracked so per-client handlers (e.g. the consent-gated
        // file_volumes_request) can identify the paired device even on a message that omits it. The
        // connection is already authenticated via pairing / reconnect proof before any gated handler runs.
        string? connectionClientId = null;

        if (isLoopback)
            logger.LogInformation("Client connected from loopback — pairing gate auto-satisfied.");
        else
            logger.LogInformation("Client connected. Awaiting pairing handshake or identity verification.");

        try
        {
            var hostInfo = new RemexMessage
            {
                Type = MessageTypes.HostInfo,
                HostCapabilities = hostCapabilitiesProvider.GetCurrent(),
            };
            await MessageSerializer.SendAsync(webSocket, hostInfo, ct);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "Failed to send host capability metadata on connect (WebSocket error).");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to send host capability metadata on connect (invalid state).");
        }

        // Sync launchers on connect
        try
        {
            var entries = await launcherStorage.LoadEntriesAsync();
            var syncMsg = new RemexMessage { Type = MessageTypes.LauncherSync, LauncherEntries = entries };
            await MessageSerializer.SendAsync(webSocket, syncMsg, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to sync launchers on connect (I/O error).");
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "Failed to sync launchers on connect (WebSocket error).");
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "Failed to sync launchers on connect (JSON error).");
        }

        // Sync layout on connect
        try
        {
            var profile = await profileStorage.LoadProfileAsync();
            var syncMsg = new RemexMessage { Type = MessageTypes.LayoutSync, DashboardProfile = profile };
            await MessageSerializer.SendAsync(webSocket, syncMsg, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to sync layout on connect (I/O error).");
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "Failed to sync layout on connect (WebSocket error).");
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "Failed to sync layout on connect (JSON error).");
        }

        // Start background telemetry stream
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamTask = StreamTelemetryAsync(webSocket, streamCts.Token);

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await MessageSerializer.ReceiveAsync(webSocket, ct);

                if (message is null)
                {
                    // Client disconnected or sent invalid data.
                    break;
                }

                logger.LogDebug("Received: {Type} (ProtocolVersion={ProtocolVersion})",
                    message.Type, message.ProtocolVersion);

                if (!string.IsNullOrWhiteSpace(message.ClientId))
                    connectionClientId = message.ClientId;

                // PAIR-1: a persisted clientId is NOT a bearer credential. Instead of trusting bare
                // presence, challenge the reconnecting client to prove possession of the reconnect
                // secret established at pairing time. We issue the challenge once, the first time an
                // unpaired connection presents a clientId that maps to a stored secret. The actual
                // authentication happens when the matching reconnect_proof arrives (handled below).
                if (!isPaired
                    && !challengeIssued
                    && message.Type != MessageTypes.ReconnectProof
                    && !string.IsNullOrWhiteSpace(message.ClientId))
                {
                    if (pairedClientRegistry.TryGetReconnectSecret(message.ClientId, out var secretProbe))
                    {
                        // Don't keep the secret around; we only needed to confirm one exists.
                        CryptographicOperations.ZeroMemory(secretProbe);

                        pendingChallengeNonce = RandomNumberGenerator.GetBytes(32);
                        challengeIssued = true;

                        var challenge = new RemexMessage
                        {
                            Type = MessageTypes.ReconnectChallenge,
                            ReconnectChallenge = new ReconnectChallenge
                            {
                                NonceBase64 = Convert.ToBase64String(pendingChallengeNonce),
                            },
                        };
                        await MessageSerializer.SendAsync(webSocket, challenge, ct);
                        logger.LogInformation(
                            "Issued reconnect challenge to client {ClientId}; awaiting proof-of-possession.",
                            message.ClientId);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Client {ClientId} presented a clientId with no reconnect secret on file — bare clientId does not authenticate; pairing required.",
                            message.ClientId);
                    }
                }

                // Reject messages from clients running an unsupported protocol version. The single
                // accept/reject rule lives in ProtocolVersionPolicy so /ws and /ws/desktop can never
                // diverge. 1.x clients get a clear rejection rather than silently operating in a
                // degraded state; ProtocolVersion defaults to 2 in RemEx 2.0 messages, so a zero
                // value also indicates a legacy or malformed client.
                if (!ProtocolVersionPolicy.IsSupported(message.ProtocolVersion))
                {
                    logger.LogWarning(
                        "Rejecting client with ProtocolVersion={Version} — minimum required is 2.",
                        message.ProtocolVersion);
                    var versionError = new RemexMessage
                    {
                        Type = MessageTypes.CommandResponse,
                        CommandSuccess = false,
                        CommandMessage =
                            $"Protocol version {message.ProtocolVersion} is not supported. " +
                            "Please upgrade your RemEx client to version 2.0 or later.",
                        CorrelationId = message.CorrelationId,
                    };
                    await MessageSerializer.SendAsync(webSocket, versionError, ct);
                    break;  // Close the loop; the finally block will close the WebSocket.
                }

                // Reject any gated message types from a connection that has not completed pairing.
                // Allowed pre-pairing: ping, pairing handshake messages.
                if (!isPaired && RequiresPairing(message.Type))
                {
                    logger.LogWarning(
                        "Rejecting {Type} from unpaired connection — pairing handshake required.",
                        message.Type);
                    var unauthorized = new RemexMessage
                    {
                        Type = MessageTypes.CommandResponse,
                        CommandSuccess = false,
                        CommandMessage = "Pairing required. Complete PIN-based pairing before issuing this request.",
                        CorrelationId = message.CorrelationId,
                    };
                    await MessageSerializer.SendAsync(webSocket, unauthorized, ct);
                    continue;
                }

                switch (message.Type)
                {
                    case MessageTypes.Ping:
                        var pong = new RemexMessage
                        {
                            Type = MessageTypes.Pong,
                            Timestamp = message.Timestamp  // Echo back sender's timestamp.
                        };
                        await MessageSerializer.SendAsync(webSocket, pong, ct);
                        logger.LogDebug("Sent pong.");
                        break;

                    case MessageTypes.Command when message.CommandAction is not null:
                        var cmdResponse = await ExecuteCommandAsync(message);
                        // Echo the correlation ID so the client can match the response to the request
                        if (message.CorrelationId is not null)
                            cmdResponse = cmdResponse with { CorrelationId = message.CorrelationId };
                        await MessageSerializer.SendAsync(webSocket, cmdResponse, ct);
                        logger.LogDebug("Sent command response for {Action}.", message.CommandAction);
                        // Surface a phone-initiated command in the Home "Recent activity" feed. Skip
                        // loopback: the PC UI's own commands travel over its self-connection into this same
                        // handler and are already recorded desktop-side, so recording them here too would
                        // double-count. Skip LaunchApp — its meaningful detail is the target app, captured
                        // elsewhere, not the bare verb "LaunchApp".
                        if (!isLoopback
                            && cmdResponse.CommandSuccess == true
                            && !string.Equals(message.CommandAction, "LaunchApp", StringComparison.OrdinalIgnoreCase))
                        {
                            Remex.Desktop.Services.ActivityService.Instance.Record(
                                Remex.Desktop.Services.ActivityKind.CommandRun, message.CommandAction);
                        }
                        break;

                    case MessageTypes.LauncherAdd when message.LauncherEntry is not null:
                        var curAdd = await launcherStorage.LoadEntriesAsync();
                        curAdd.Add(message.LauncherEntry);
                        await launcherStorage.SaveEntriesAsync(curAdd);
                        await MessageSerializer.SendAsync(webSocket, new RemexMessage { Type = MessageTypes.LauncherSync, LauncherEntries = curAdd }, ct);
                        break;

                    case MessageTypes.LauncherRemove when message.LauncherEntry is not null:
                        var curRem = await launcherStorage.LoadEntriesAsync();
                        curRem.RemoveAll(x => x.Id == message.LauncherEntry.Id);
                        await launcherStorage.SaveEntriesAsync(curRem);
                        await MessageSerializer.SendAsync(webSocket, new RemexMessage { Type = MessageTypes.LauncherSync, LauncherEntries = curRem }, ct);
                        break;

                    case MessageTypes.LauncherSync when message.LauncherEntries is not null:
                        await launcherStorage.SaveEntriesAsync(message.LauncherEntries);
                        logger.LogInformation("Launcher list synced from client ({Count} entries).", message.LauncherEntries.Count);
                        break;

                    case MessageTypes.ProcessListRequest:
                        var procs = await processMonitorService.GetProcessesAsync();
                        await MessageSerializer.SendAsync(webSocket, new RemexMessage { Type = MessageTypes.ProcessListSync, ProcessList = procs }, ct);
                        break;
                    case MessageTypes.LayoutUpdate when message.DashboardProfile is not null:
                        await profileStorage.SaveProfileAsync(message.DashboardProfile);
                        logger.LogInformation("Dashboard layout updated from client.");
                        break;

                    case MessageTypes.LayoutRequest:
                        var reqProfile = await profileStorage.LoadProfileAsync();
                        await MessageSerializer.SendAsync(webSocket, new RemexMessage { Type = MessageTypes.LayoutSync, DashboardProfile = reqProfile }, ct);
                        logger.LogInformation("Dashboard layout sent to client on request.");
                        break;

                    case MessageTypes.DesktopInput when message.InputEvent is not null:
                        DispatchInput(message.InputEvent);
                        break;

                    // ── 2.0 Pairing ──
                    case MessageTypes.PairingRequest:
                        var pairingResponse = await pairingHandler.HandlePairingRequestAsync(message, ct);
                        if (pairingResponse is not null)
                        {
                            // Flip pairingStarted before sending: HandlePairingRequestAsync has
                            // already taken the singleton session in PairingService, so the
                            // cleanup at the bottom of HandleAsync must run even if SendAsync
                            // throws (socket aborted mid-send). Previously the assignment lived
                            // after SendAsync, so a mid-send failure left the session live for
                            // the full 120-second pairing timeout and blocked retries.
                            if (pairingResponse.Type == MessageTypes.PairingResponse)
                            {
                                pairingStarted = true;
                            }
                            await MessageSerializer.SendAsync(webSocket, pairingResponse, ct);
                        }
                        break;

                    case MessageTypes.PairingComplete:
                        var completeResponse = await pairingHandler.HandlePairingCompleteAsync(message, ct);
                        if (completeResponse is not null)
                            await MessageSerializer.SendAsync(webSocket, completeResponse, ct);
                        if (completeResponse is { Type: MessageTypes.PairingComplete, CommandSuccess: true })
                        {
                            isPaired = true;
                            pairingStarted = false;
                            logger.LogInformation("Pairing verified — connection authenticated.");
                            RecordDeviceConnectedActivity();
                        }
                        break;

                    case MessageTypes.PairingPinRequest:
                        // ASI-compliant PIN relay (RemEx-1t0b) — the sole PIN auto-fetch path since the
                        // GET /pairing-pin HTTP endpoint was retired (RemEx-0xp0). Reply with the active
                        // PIN iff the transport is trusted for auto-fetch (isTrustedForPinAutoFetch is
                        // computed at the /ws map site via TransportTrust.IsTrustedForPinAutoFetch). The
                        // handler only reads an already-active PIN; it never creates or mutates a
                        // session. We always reply, so the client's fetch never hangs — a pin-less
                        // response simply means the user enters the PIN manually.
                        await MessageSerializer.SendAsync(
                            webSocket,
                            await pairingHandler.HandlePairingPinRequestAsync(message, isTrustedForPinAutoFetch, ct),
                            ct);
                        break;

                    case MessageTypes.ReconnectProof:
                        // PAIR-1: verify the client's proof-of-possession against the nonce we issued.
                        // A correct HMAC-SHA256(reconnectSecret, nonce) authenticates the reconnect; a
                        // missing/incorrect proof (or a clientId with no stored secret) is rejected.
                        if (TryAuthenticateReconnect(message, pendingChallengeNonce, pairedClientRegistry, logger))
                        {
                            isPaired = true;
                            logger.LogInformation(
                                "Reconnect proof verified — connection authenticated for client {ClientId}.",
                                message.ReconnectProof?.ClientId ?? message.ClientId);
                            RecordDeviceConnectedActivity();
                        }
                        else
                        {
                            logger.LogWarning("Reconnect proof verification failed — connection remains unpaired.");
                        }

                        if (pendingChallengeNonce is not null)
                        {
                            CryptographicOperations.ZeroMemory(pendingChallengeNonce);
                            pendingChallengeNonce = null;
                        }
                        break;

                    // ── 2.0 File Transfer ──
                    case MessageTypes.FileRootsRequest:
                        await fileTransferHandler.HandleFileRootsRequestAsync(webSocket, ct);
                        break;

                    case MessageTypes.FileBrowseRequest:
                        await fileTransferHandler.HandleFileBrowseRequestAsync(message, webSocket, connectionClientId, ct);
                        break;

                    case MessageTypes.FileTransferStart:
                        await fileTransferHandler.HandleFileTransferStartAsync(message, webSocket, connectionClientId, ct);
                        break;

                    case MessageTypes.FileTransferChunk:
                        await fileTransferHandler.HandleFileTransferChunkAsync(message, webSocket, ct);
                        break;

                    case MessageTypes.FileTransferEnd:
                        await fileTransferHandler.HandleFileTransferEndAsync(message, webSocket, ct);
                        break;

                    case MessageTypes.FileTransferCancel:
                        await fileTransferHandler.HandleFileTransferCancelAsync(message);
                        break;

                    case MessageTypes.FileManageRequest:
                        await fileTransferHandler.HandleFileManageRequestAsync(message, webSocket, ct);
                        break;

                    case MessageTypes.FileHashRequest:
                        await fileTransferHandler.HandleFileHashRequestAsync(message, webSocket, connectionClientId, ct);
                        break;

                    case MessageTypes.FileRootManageRequest:
                        await fileTransferHandler.HandleFileRootManageRequestAsync(message, webSocket, ct);
                        break;

                    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP2 ──
                    case MessageTypes.FileVolumesRequest:
                    {
                        // "Browse this PC" full-device access is consent-gated and awaits the user's decision
                        // (up to 60s). Run it OFF the reader loop — otherwise the whole connection stalls for
                        // the duration of the prompt, so file transfers on the same socket report "peer did
                        // not respond". Deferred sends are serialized per-socket in MessageSerializer.
                        var volMsg = message;
                        var volClientId = connectionClientId;
                        _ = RunDetachedAsync(
                            () => fileTransferHandler.HandleFileVolumesRequestAsync(volMsg, webSocket, volClientId, ct),
                            "file_volumes_request");
                        break;
                    }

                    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP3 ──
                    case MessageTypes.FileSearchRequest:
                        await fileTransferHandler.HandleFileSearchRequestAsync(message, webSocket, connectionClientId, ct);
                        break;

                    case MessageTypes.FileMetadataRequest:
                        await fileTransferHandler.HandleFileMetadataRequestAsync(message, webSocket, connectionClientId, ct);
                        break;

                    case MessageTypes.FileThumbnailRequest:
                        await fileTransferHandler.HandleFileThumbnailRequestAsync(message, webSocket, connectionClientId, ct);
                        break;

                    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP-jjdb: consent-response + push ──
                    // file_consent_response resolves a consent prompt this host raised (full-browse or
                    // incoming-push); file_push_offer raises an incoming-push consent and replies with the
                    // receiver-assigned transfer ids. connectionClientId identifies the paired device even
                    // when the message body omits clientId — the connection is already authenticated above.
                    case MessageTypes.FileConsentResponse:
                        fileTransferHandler.HandleFileConsentResponse(message);
                        break;

                    case MessageTypes.FilePushOffer:
                    {
                        // Consent-gated: this awaits a user consent decision (up to 60s). Run it OFF the
                        // reader loop so a pending consent cannot block file_transfer_offer / volumes / etc.
                        // on this same connection. Per-socket send serialization (MessageSerializer) makes
                        // the deferred response safe against the loop's own concurrent sends.
                        var pushMsg = message;
                        var pushClientId = connectionClientId;
                        _ = RunDetachedAsync(
                            () => fileTransferHandler.HandleFilePushOfferAsync(pushMsg, webSocket, pushClientId, ct),
                            "file_push_offer");
                        break;
                    }

                    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP4: v3 transfer negotiation ──
                    // Control plane for the binary /ws/files channel. The bulk data itself never touches this
                    // switch — it flows as FileFrameEnvelope frames on /ws/files (see TransferSessionManager
                    // .RunChannelAsync). connectionClientId identifies the paired device so the offer can be
                    // matched to its already-connected binary channel.
                    case MessageTypes.FileTransferOffer when message.FileTransferOffer is not null:
                        await transferSessionManager.HandleOfferAsync(connectionClientId, message.FileTransferOffer, webSocket, ct);
                        break;

                    case MessageTypes.FileTransferReady when message.FileTransferReady is not null:
                        transferSessionManager.HandleReady(message.FileTransferReady);
                        break;

                    case MessageTypes.FileTransferComplete when message.FileTransferComplete is not null:
                        await transferSessionManager.HandleCompleteAsync(message.FileTransferComplete, webSocket, isLoopback, ct);
                        break;

                    case MessageTypes.FileTransferResult when message.FileTransferResult is not null:
                        transferSessionManager.HandleResult(message.FileTransferResult);
                        break;

                    case MessageTypes.FileTransferControl when message.FileTransferControl is not null:
                        transferSessionManager.HandleControl(message.FileTransferControl);
                        break;

                    default:
                        logger.LogWarning("Unknown message type: {Type}", message.Type);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket error.");
        }
        catch (InvalidOperationException ex)
        {
            // Defensive: a WebSocket used out of order (e.g. a concurrent send/receive race or a
            // socket aborted mid-operation) surfaces as InvalidOperationException rather than
            // WebSocketException. Catch it here so it can never escape the receive loop and skip
            // the cleanup in the finally block below.
            logger.LogWarning(ex, "WebSocket session ended with an invalid-state error.");
        }
        finally
        {
            // Cleanup MUST run for every exit path — graceful close, cancellation, socket abort, or
            // an unexpected exception type. Previously this lived after the catch blocks (outside a
            // finally), so an exception that didn't match the catch clauses would leak the file
            // transfers, pairing session, and telemetry stream-CTS for this connection.

            // Clean up any active file transfers for this connection
            await fileTransferHandler.CleanupAllTransfersAsync();

            if (pairingStarted)
            {
                pairingHandler.CancelActivePairing();
                logger.LogInformation("Cancelled interrupted pairing session for disconnected client.");
            }

            // Cancel background stream
            streamCts.Cancel();
            try { await streamTask; } catch (OperationCanceledException) { } catch (Exception ex) { logger.LogTrace(ex, "Stream task ended with error."); }

            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Server shutting down",
                    CancellationToken.None);
            }

            logger.LogInformation("Client disconnected.");
        }
    }


    private async Task<RemexMessage> ExecuteCommandAsync(RemexMessage message)
    {
        try
        {
            switch (message.CommandAction!.ToUpperInvariant())
            {
                case "SHUTDOWN":
                    await commandService.Shutdown(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Shutdown executed.");
                case "FORCESHUTDOWN":
                    await commandService.ForceShutdown(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Force shutdown executed.");
                case "RESTART":
                    await commandService.Restart(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Restart executed.");
                case "FORCERESTART":
                    await commandService.ForceRestart(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Force restart executed.");
                case "RESTARTTOUEFI":
                    await commandService.RestartToUefi(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Restart to UEFI executed.");
                case "SLEEP":
                    await commandService.Sleep();
                    return MakeCommandResponse(true, "Sleep executed.");
                case "HIBERNATE":
                    await commandService.Hibernate();
                    return MakeCommandResponse(true, "Hibernate executed.");
                case "MONITOROFF":
                    await commandService.MonitorOff();
                    return MakeCommandResponse(true, "Monitor off executed.");
                case "SIGNOUT":
                    await commandService.SignOut();
                    return MakeCommandResponse(true, "Sign out executed.");
                case "KILLPROCESS":
                    if (message.CommandParameters?.TryGetValue("ProcessId", out var pidStr) == true
                        && int.TryParse(pidStr, out var pid))
                    {
                        var killResult = processMonitorService.KillProcess(pid);
                        return MakeCommandResponse(
                            killResult.Success,
                            killResult.Success
                                ? "Process killed."
                                : killResult.Message);
                    }
                    return MakeCommandResponse(false, "Missing or invalid ProcessId parameter.");
                case "KILLPROCESSELEVATED":
                    if (message.CommandParameters?.TryGetValue("ProcessId", out var epidStr) == true
                        && int.TryParse(epidStr, out var epid))
                    {
                        var killResult = processMonitorService.KillProcess(epid);
                        return MakeCommandResponse(
                            killResult.Success,
                            killResult.Success
                                ? "Elevated process kill executed."
                                : killResult.Message);
                    }
                    return MakeCommandResponse(false, "Missing or invalid ProcessId parameter.");
                case "LOCK":
                    await commandService.Lock();
                    return MakeCommandResponse(true, "Lock executed.");
                case "LAUNCHAPP":
                    if (message.CommandParameters?.TryGetValue("TargetPath", out var targetPath) == true
                        && !string.IsNullOrWhiteSpace(targetPath))
                    {
                        await appLauncherService.LaunchAppAsync(targetPath);
                        return MakeCommandResponse(true, "App launched.");
                    }
                    return MakeCommandResponse(false, "Missing TargetPath parameter.");
                case "WAKEONLAN":
                    if (message.CommandParameters?.TryGetValue("MacAddress", out var mac) == true)
                    {
                        var bip = message.CommandParameters.TryGetValue("BroadcastIp", out var b) ? b : "255.255.255.255";
                        var port = message.CommandParameters.TryGetValue("Port", out var ps) && int.TryParse(ps, out var p) ? p : 9;
                        await wakeOnLanService.WakeAsync(mac, bip, port);
                        return MakeCommandResponse(true, $"WoL sent to {mac}.");
                    }
                    return MakeCommandResponse(false, "Missing MacAddress parameter.");
                default:
                    return MakeCommandResponse(false, $"Unknown command: {message.CommandAction}");
            }
        }
        catch (Exception ex)
        {
            return MakeCommandResponse(false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a consent-gated file handler detached from the control reader loop so its (up to 60s) consent
    /// wait cannot stall other messages on the same connection. Exceptions are observed and logged here so
    /// the fire-and-forget task never faults silently; cancellation (connection closing) is expected.
    /// </summary>
    private async Task RunDetachedAsync(Func<Task> handler, string label)
    {
        try
        {
            await handler();
        }
        catch (OperationCanceledException)
        {
            // Connection closing while the handler awaited consent — nothing to do.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deferred {Label} handler failed.", label);
        }
    }

    private static RemexMessage MakeCommandResponse(bool success, string msg) => new()
    {
        Type = MessageTypes.CommandResponse,
        CommandSuccess = success,
        CommandMessage = msg,
    };

    private static long _lastDeviceConnectedTicks;

    /// <summary>
    /// Surfaces "Phone connected" in the Home recent-activity feed when a phone authenticates
    /// (fresh pairing or PAIR-1 reconnect proof). Loopback can never reach the call sites — it
    /// starts pre-paired — so this only ever records a real device. Throttled to once per minute
    /// process-wide: with the connect-time kickoff ping (RemEx-moqo) every reconnect authenticates,
    /// and a flapping network must not flood the 60-entry feed.
    /// </summary>
    private static void RecordDeviceConnectedActivity()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDeviceConnectedTicks);
        if (now - last < TimeSpan.FromSeconds(60).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastDeviceConnectedTicks, now, last) != last) return;

        Remex.Desktop.Services.ActivityService.Instance.Record(
            Remex.Desktop.Services.ActivityKind.DeviceConnected, string.Empty);
    }

    /// <summary>
    /// Returns true when the message type must be rejected before the connection has completed
    /// pairing. Ping and the pairing handshake messages are intentionally exempt.
    /// </summary>
    private static bool RequiresPairing(string type) => type switch
    {
        MessageTypes.Ping => false,
        MessageTypes.PairingRequest => false,
        MessageTypes.PairingComplete => false,
        // The reconnect challenge/response handshake is itself how an unpaired connection
        // authenticates, so it must be permitted before pairing is established.
        MessageTypes.ReconnectProof => false,
        // The PIN auto-fetch request must be usable *during* pairing (the connection is not yet
        // paired). Its own gate is transport trust (IsTrustedForPinAutoFetch), enforced in the
        // handler — not the pairing gate. It only relays an already-active PIN.
        MessageTypes.PairingPinRequest => false,
        _ => true,
    };

    /// <summary>
    /// PAIR-1 proof-of-possession verification. Confirms the client returned
    /// HMAC-SHA256(reconnectSecret, nonce) for the nonce previously issued on this connection.
    /// Returns false (without leaking timing) when there is no outstanding challenge, the proof or
    /// clientId is missing/malformed, the client has no stored secret, or the HMAC does not match.
    /// </summary>
    private static bool TryAuthenticateReconnect(
        RemexMessage message,
        byte[]? pendingChallengeNonce,
        PairedClientRegistry pairedClientRegistry,
        ILogger logger)
    {
        if (pendingChallengeNonce is null)
        {
            logger.LogWarning("Received reconnect_proof with no outstanding challenge.");
            return false;
        }

        var proof = message.ReconnectProof;
        if (proof is null || string.IsNullOrWhiteSpace(proof.ProofHmacBase64))
        {
            logger.LogWarning("Received reconnect_proof with no proof payload.");
            return false;
        }

        var clientId = proof.ClientId ?? message.ClientId;
        if (!pairedClientRegistry.TryGetReconnectSecret(clientId, out var reconnectSecret))
        {
            logger.LogWarning("Reconnect proof references a client with no stored secret.");
            return false;
        }

        try
        {
            var expected = HMACSHA256.HashData(reconnectSecret, pendingChallengeNonce);

            byte[] provided;
            try
            {
                provided = Convert.FromBase64String(proof.ProofHmacBase64);
            }
            catch (FormatException)
            {
                logger.LogWarning("Reconnect proof HMAC was not valid base64.");
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reconnectSecret);
        }
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

    private async Task StreamTelemetryAsync(WebSocket webSocket, CancellationToken ct)
    {
        // Per-iteration try/catch: a single transient failure (e.g. /proc read race,
        // hwmon entry briefly unreadable, websocket send hiccup) used to kill the loop
        // forever — the user would see "connected but telemetry never updates". Now
        // we log a warning and keep going; only WebSocket-state failures end the stream.
        while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var payload = telemetryBackgroundService.CurrentPayload;
                if (payload != null)
                {
                    var message = new RemexMessage
                    {
                        Type = MessageTypes.Telemetry,
                        Telemetry = payload,
                        Timestamp = System.Diagnostics.Stopwatch.GetTimestamp()
                    };

                    await MessageSerializer.SendAsync(webSocket, message, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException ex)
            {
                // Socket is gone; no point continuing.
                logger.LogWarning(ex, "Telemetry stream halted: WebSocket error.");
                return;
            }
            catch (Exception ex)
            {
                // Transient failure (e.g. /proc parse, hwmon read). Log loudly and keep going.
                logger.LogWarning(ex, "Telemetry stream tick failed; continuing.");
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private void DispatchInput(InputEvent input)
    {
        try
        {
            switch (input.EventType)
            {
                case InputEventTypes.MouseMove when input.X.HasValue && input.Y.HasValue:
                    inputSimulation.MoveMouse(input.X.Value, input.Y.Value);
                    break;
                case InputEventTypes.MouseMove when input.DeltaX.HasValue || input.DeltaY.HasValue:
                    inputSimulation.MouseMoveRelative(input.DeltaX ?? 0, input.DeltaY ?? 0);
                    break;
                case InputEventTypes.MouseDown when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                        inputSimulation.MoveMouse(input.X.Value, input.Y.Value);
                    inputSimulation.MouseDown(input.Button.Value);
                    break;
                case InputEventTypes.MouseUp when input.Button.HasValue:
                    inputSimulation.MouseUp(input.Button.Value);
                    break;
                case InputEventTypes.MouseClick when input.Button.HasValue:
                    if (input.X.HasValue && input.Y.HasValue)
                        inputSimulation.MoveMouse(input.X.Value, input.Y.Value);
                    inputSimulation.MouseClick(input.Button.Value);
                    break;
                case InputEventTypes.MouseScroll:
                    inputSimulation.MouseScroll(input.DeltaX ?? 0, input.DeltaY ?? 0);
                    break;
                case InputEventTypes.KeyDown when input.KeyCode.HasValue:
                    inputSimulation.KeyDown(input.KeyCode.Value);
                    break;
                case InputEventTypes.KeyUp when input.KeyCode.HasValue:
                    inputSimulation.KeyUp(input.KeyCode.Value);
                    break;
                case InputEventTypes.TypeText when input.Text is not null:
                    inputSimulation.TypeText(input.Text);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispatch input: {Type}", input.EventType);
        }
    }
}
