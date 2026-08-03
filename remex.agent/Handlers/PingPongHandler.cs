using System.Net.WebSockets;
using System.Security.Cryptography;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Validation;
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
    Remex.Agent.Services.Screenshot.IScreenshotService screenshotService,
    PairingHandler pairingHandler,
    FileTransferHandler fileTransferHandler,
    TransferSessionManager transferSessionManager,
    PairedClientRegistry pairedClientRegistry,
    Remex.Agent.Services.FileTransfer.FilePushOriginator pushOriginator,
    ClientSessionRegistry sessionRegistry) : IDisposable
{
    /// <summary>
    /// Keys this client pressed and did not release, so disconnecting can release them (RemEx-73dc).
    /// </summary>
    /// <remarks>
    /// The same hazard RemEx-e2p4 fixed for the Remote Desktop stream: a key press is two messages
    /// and a chord is six, so a client that vanishes between them leaves the modifier physically held
    /// on the user's desktop, and only the host can clean that up because the client is what went
    /// away.
    ///
    /// BE HONEST ABOUT WHAT THIS CURRENTLY PROTECTS. No shipping client sends <c>desktop_input</c>
    /// over this socket: the Android side routes it through <c>HandleDesktopMessage</c> to
    /// <c>/ws/desktop</c> instead, so the Remote Control screen's keystrokes are already covered by
    /// the Remote Desktop handler. This branch of the protocol is still reachable and still presses
    /// real keys, so leaving it as the one input path with no cleanup would be a trap for whoever
    /// next sends input here — but it is defensive symmetry, not a live user-facing bug.
    /// </remarks>
    private readonly HeldKeyTracker _heldKeys = new();
    public async Task HandleAsync(
        WebSocket webSocket, bool isLoopback, bool isTrustedForPinAutoFetch,
        string? remoteAddress, CancellationToken ct)
    {
        // Recorded for as long as this connection lives (RemEx-xuyu). A handle rather than a
        // matching unregister call, because this method has several exit paths and a missed one
        // would leave a phantom session behind - which is precisely the "1 phone connected" with
        // nothing attached that the presence work exists to fix.
        using var session = sessionRegistry.Register(remoteAddress, webSocket);

        // Loopback is the PC's own UI talking to itself and skips pairing by construction, so it is
        // authenticated the moment it connects — the same reasoning that seeds isPaired below. Every
        // other connection stays invisible in the registry until it clears the gate.
        if (isLoopback) sessionRegistry.MarkAuthenticated(session);

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

        // Start background telemetry stream — but NOT for the PC's own UI. That connection is this
        // same process talking to itself over loopback TLS, so streaming to it meant serializing the
        // payload, encrypting it, sending it through the loopback adapter, decrypting it and rebuilding
        // the whole record graph, once a second, forever, to hand a component in this process data it
        // could already reach by reference. It subscribes to TelemetryBackgroundService directly
        // instead (RemEx-ite8).
        //
        // The two sides agree by construction: a client reaching a localhost/127.0.0.1/::1 URI is
        // routed over loopback, which is exactly what IPAddress.IsLoopback sees here, and
        // ConnectionViewModel gates its in-process subscription on the same test it already uses to
        // bypass pairing. The "some other process owns port 5005" case cannot arise — Program.cs holds
        // a Local\RemExGuiHost single-instance mutex.
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamTask = isLoopback ? Task.CompletedTask : StreamTelemetryAsync(webSocket, streamCts.Token);

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
                {
                    connectionClientId = message.ClientId;
                    // The registry learns the identity at the same moment this connection does.
                    // A device name is NOT available on most messages — only pairing_request
                    // carries one (handled below) — so it is left null here rather than guessed.
                    // ClientSession documents null as "not named", and PhonePresence falls back to
                    // a count-only display, so this degrades rather than misreports.
                    sessionRegistry.Identify(session, message.ClientId, deviceName: null);
                }

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

                // Pairing is NOT sufficient for the three messages that mutate the launcher
                // allowlist. VULN-3 (RemEx-s032.3) hardened LAUNCHAPP by requiring the target to
                // match a persisted launcher entry, and that mitigation only means anything while
                // the list is curated by the person at the PC. Before this gate a paired client
                // could add any local path with launcher_add - or replace the entire list in one
                // message with launcher_sync - and then launch it as the always-elevated host
                // (RemEx-q6xt).
                //
                // Loopback is the PC's own UI talking to its embedded host, which is the only
                // sender that legitimately writes this list. Android never sends these types; it
                // only ever asks with launcher_sync_request. Note this restricts the INBOUND
                // direction only - the host still emits launcher_sync to clients on connect.
                if (!isLoopback && RequiresLoopback(message.Type))
                {
                    logger.LogWarning(
                        "Rejecting {Type} from a non-loopback connection — the launcher allowlist " +
                        "may only be changed on the PC itself.",
                        message.Type);
                    var notLocal = new RemexMessage
                    {
                        Type = MessageTypes.CommandResponse,
                        CommandSuccess = false,
                        CommandMessage = "The list of programs can only be changed on the PC itself.",
                        CorrelationId = message.CorrelationId,
                    };
                    await MessageSerializer.SendAsync(webSocket, notLocal, ct);
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
                        var cmdResponse = await ExecuteCommandAsync(message, webSocket, connectionClientId, ct);
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

                    // The read counterpart to the three mutating launcher types above, and the only
                    // launcher message Android sends (pull-to-refresh on the App Launcher screen).
                    // Deliberately not loopback-gated: re-reading a list the host already pushes on
                    // connect grants nothing extra. Mirrors LayoutRequest → LayoutSync below.
                    case MessageTypes.LauncherSyncRequest:
                        var reqEntries = await launcherStorage.LoadEntriesAsync();
                        await MessageSerializer.SendAsync(webSocket, new RemexMessage { Type = MessageTypes.LauncherSync, LauncherEntries = reqEntries }, ct);
                        logger.LogInformation("Launcher list sent to client on request ({Count} entries).", reqEntries.Count);
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
                        // The ONLY message that carries a device name, so this is the only chance to
                        // learn one.
                        //
                        // BE HONEST ABOUT WHAT THIS BUYS TODAY: NOTHING VISIBLE. Android pairs over a
                        // dedicated throwaway socket that AndroidNativeExports disposes the moment
                        // pairing succeeds, and the long-lived session RemexClientManager then opens
                        // never sends pairing_request. So the sessions a presence UI would display are
                        // never the ones that carried a name, and PairedClientRegistry stores
                        // clientId → secret with nowhere to keep it. RemEx-yzqs is therefore a hard
                        // prerequisite for showing a name at all, not a polish item, and RemEx-8m3r
                        // matters after it because Android sends the constant "Android Client" rather
                        // than the actual device. Recording it here is what makes fixing those two
                        // sufficient instead of requiring a wire change as well.
                        sessionRegistry.Identify(session, message.ClientId, message.PairingRequest?.ClientName);

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
                            // Only now is this connection allowed to be counted, found or sent to.
                            sessionRegistry.MarkAuthenticated(session);
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
                            sessionRegistry.MarkAuthenticated(session);
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

                    case MessageTypes.FilePushResponse when message.FilePushResponse is not null:
                        // The answer to an offer THIS host made (RemEx-y7my). Every other file_* case
                        // here serves a request the phone started; this one completes one we started.
                        pushOriginator.Complete(message.FilePushResponse);
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
            try { await streamTask; } catch (OperationCanceledException) { /* expected on cancel; the sibling catch below reports anything else */ } catch (Exception ex) { logger.LogTrace(ex, "Stream task ended with error."); }

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


    /// <remarks>
    /// Takes the connection's token so a command that does real work - the screenshot capture writes
    /// a file - stops when the client goes away, rather than finishing into a socket nobody is
    /// reading. The power verbs ignore it, which is correct: a shutdown that has been asked for
    /// should not be abandoned because the phone dropped off mid-request.
    /// </remarks>
    private async Task<RemexMessage> ExecuteCommandAsync(
        RemexMessage message, WebSocket webSocket, string? clientId, CancellationToken ct)
    {
        try
        {
            switch (message.CommandAction!.ToUpperInvariant())
            {
                case "SHUTDOWN":
                    await commandService.Shutdown(Remex.Core.Services.Command.CommandDelayParameter.ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Shutdown executed.");
                case "FORCESHUTDOWN":
                    await commandService.ForceShutdown(Remex.Core.Services.Command.CommandDelayParameter.ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Force shutdown executed.");
                case "RESTART":
                    await commandService.Restart(Remex.Core.Services.Command.CommandDelayParameter.ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Restart executed.");
                case "FORCERESTART":
                    await commandService.ForceRestart(Remex.Core.Services.Command.CommandDelayParameter.ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Force restart executed.");
                case "RESTARTTOUEFI":
                    await commandService.RestartToUefi(Remex.Core.Services.Command.CommandDelayParameter.ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Restart to UEFI executed.");
                case "SLEEP":
                    await commandService.Sleep();
                    return MakeCommandResponse(true, "Sleep executed.");
                case "HIBERNATE":
                    await commandService.Hibernate();
                    return MakeCommandResponse(true, "Hibernate executed.");
                case "SCREENSHOT":
                {
                    // SAVES ON THIS PC FIRST, THEN OFFERS IT TO THE PHONE (below, RemEx-y7my). The
                    // file exists on this machine either way; whether it also reaches the phone is
                    // the phone's decision, because a screenshot carries whatever happened to be on
                    // the screen.
                    //
                    // The label is optional and comes from the client; ScreenshotFileName sanitises
                    // it to ASCII letters, digits and dashes and truncates it to 16 characters, so a
                    // monitor name like ".\DISPLAY1" cannot put a separator inside the file name.
                    string? displayLabel = null;
                    message.CommandParameters?.TryGetValue("DisplayLabel", out displayLabel);
                    var saved = await screenshotService.CaptureAsync(displayLabel, ct);

                    // THE NAME, NOT THE PATH. The full path carries the account name, and a path on
                    // THIS machine is not something the phone can open - when the push below runs it
                    // identifies the file by name alone, so a path buys the client nothing and
                    // discloses the user's login. The host log keeps the full path for anyone actually
                    // diagnosing this machine.
                    logger.LogInformation("Screenshot saved to {Path}", saved);

                    // OFFERED TO THE PHONE, NOT PUSHED AT IT (RemEx-y7my). A screenshot carries
                    // whatever was on the screen, so the phone gates it before a byte moves.
                    //
                    // **DETACHED, AND THE FIRST VERSION DEADLOCKED FOR WANT OF THIS.** This runs
                    // inline in the single control-socket reader loop, and the phone's answer arrives
                    // ON THAT SOCKET - so awaiting it here meant waiting for a message only this loop
                    // could read. It timed out after 70 seconds every time, and stalled every other
                    // inbound message meanwhile. The mirror-image case eleven lines above already
                    // runs detached for exactly this reason.
                    //
                    // The response therefore reports the CAPTURE, which has already happened. How the
                    // transfer went reaches the phone through the file_transfer_* messages it already
                    // renders - it cannot be a value this method waits for.
                    var name = Path.GetFileName(saved);
                    if (!string.IsNullOrWhiteSpace(clientId))
                    {
                        _ = RunDetachedAsync(
                            () => TryPushScreenshotAsync(saved, name, webSocket, clientId, ct),
                            "screenshot_push");
                    }

                    return MakeCommandResponse(
                        true, $"Screenshot saved to your Pictures folder as {name}.");
                }
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
                        // ExpectedName is optional on the wire: a client older than RemEx-druh
                        // simply omits it and is killed unverified, exactly as before. Nothing
                        // breaks on an older phone; it just does not gain the protection.
                        message.CommandParameters.TryGetValue("ExpectedName", out var expectedName);
                        message.CommandParameters.TryGetValue("ExpectedStartUnixMs", out var startStr);
                        var expectedStart = long.TryParse(startStr, out var parsedStart) ? parsedStart : (long?)null;
                        var killResult = processMonitorService.KillProcess(pid, expectedName, expectedStart);
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
                        message.CommandParameters.TryGetValue("ExpectedName", out var eExpectedName);
                        message.CommandParameters.TryGetValue("ExpectedStartUnixMs", out var eStartStr);
                        var eExpectedStart = long.TryParse(eStartStr, out var eParsedStart) ? eParsedStart : (long?)null;
                        var killResult = processMonitorService.KillProcess(epid, eExpectedName, eExpectedStart);
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

    /// <summary>
    /// Offers the screenshot to the phone and, if it agrees, sends it (RemEx-y7my).
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THE PHONE DECIDES.** A screenshot is whatever happened to be on the screen - a document, an
    /// inbox, a password manager - so it is offered rather than pushed. Usually that means a prompt;
    /// where the user has already ticked "remember" for incoming files from this PC, their standing
    /// grant answers instead. Either way the decision is the phone's and was made by its owner - it is
    /// NOT asked afresh every time, which an earlier version of this comment claimed.
    /// </para>
    /// <para>
    /// FAILING TO SEND IS NOT FAILING TO CAPTURE. The file is already safely on the PC by the time
    /// this runs, so every outcome here returns false and lets the caller say so, rather than turning
    /// a successful screenshot into a failed command.
    /// </para>
    /// </remarks>
    private async Task<bool> TryPushScreenshotAsync(
        string absolutePath, string fileName, WebSocket webSocket, string? clientId, CancellationToken ct)
    {
        try
        {
            return await PushScreenshotCoreAsync(absolutePath, fileName, webSocket, clientId, ct);
        }
        catch (Exception ex)
        {
            // CATCHES EVERYTHING SO THE STATED INVARIANT IS TRUE. The remarks promise that failing to
            // send never turns a successful capture into a failure, and the typed catches inside let
            // an OperationCanceledException - the ordinary shape of a dropping connection - escape
            // and do exactly that.
            logger.LogWarning(ex, "Could not offer the screenshot to the phone.");
            return false;
        }
    }

    private async Task<bool> PushScreenshotCoreAsync(
        string absolutePath, string fileName, WebSocket webSocket, string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            // An unidentified peer has nothing to grant consent AS, and the receiver mints its ids
            // against a device identity. Nothing to push to.
            return false;
        }

        long size;
        try
        {
            size = new FileInfo(absolutePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not measure the screenshot before offering it.");
            return false;
        }

        var outcome = await pushOriginator.OfferFileAsync(webSocket, fileName, size, ct);
        if (!outcome.Accepted)
        {
            return false;
        }

        // One file offered, so one id back - the negotiation refuses any other count precisely so
        // this index is safe.
        // THE SAME NAME AND SIZE THE OFFER CARRIED, not values re-derived from the path. Deriving the
        // name twice let the consent prompt show one name while the transfer carried another; the
        // size had the same shape of bug until RemEx-ccqb, and re-measuring it also left the phone
        // with nothing to check the transfer's claim against.
        return await transferSessionManager.PushFileAsync(
            clientId, outcome.TransferIds[0], absolutePath, fileName, size, webSocket, ct);
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
    /// <summary>
    /// Message types that a paired-but-remote client still may not send: the three that MUTATE the
    /// launcher allowlist.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RequiresPairing"/> because it answers a different question. That one
    /// asks "has this connection authenticated"; this asks "is this connection the PC itself". The
    /// allowlist is the thing VULN-3's LAUNCHAPP check is measured against, so being able to rewrite
    /// it over the wire makes that mitigation self-referential (RemEx-q6xt).
    /// <para>
    /// <see cref="MessageTypes.LauncherSyncRequest"/> is deliberately absent: asking for the list is
    /// a read, and it is what the Android client actually sends. (When this gate was written that
    /// type had no constant to name it — RemEx-vpxx added one along with the host case that answers
    /// it, so the exclusion can now be stated in the type system rather than in prose.)
    /// </para>
    /// </remarks>
    internal static bool RequiresLoopback(string type) =>
        type is MessageTypes.LauncherAdd
             or MessageTypes.LauncherRemove
             or MessageTypes.LauncherSync;

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

    private async Task StreamTelemetryAsync(WebSocket webSocket, CancellationToken ct)
    {
        // Per-iteration try/catch: a single transient failure (e.g. /proc read race,
        // hwmon entry briefly unreadable, websocket send hiccup) used to kill the loop
        // forever — the user would see "connected but telemetry never updates". Now
        // we log a warning and keep going; only WebSocket-state failures end the stream.
        TelemetryBackgroundService.TelemetrySnapshot? lastSentSnapshot = null;

        while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = telemetryBackgroundService.CurrentSnapshot;

                // Only send a sample this connection has not already had. Normally every tick brings
                // a new one, so this changes nothing; it matters when the sampler stalls — WMI can
                // block for seconds — where the old code cheerfully re-sent an identical 60-100 KB
                // envelope every second to every client. Reference equality is the right test: the
                // sampler publishes a new snapshot object per successful poll. (RemEx-0zbj)
                if (snapshot != null && !ReferenceEquals(snapshot, lastSentSnapshot))
                {
                    await MessageSerializer.SendRawAsync(webSocket, snapshot.Frame, ct);
                    lastSentSnapshot = snapshot;
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

    /// <summary>Applies one input event to the host.</summary>
    /// <remarks>
    /// Internal rather than private so the held-key bookkeeping can be tested end to end — the path
    /// that normally feeds it is a WebSocket, so without a seam the wiring between here and
    /// <see cref="Dispose"/> is the kind of link that can be deleted with every test still green
    /// (RemEx-73dc, and RemEx-y6x6 for why that matters).
    /// </remarks>
    internal void DispatchInput(InputEvent input)
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
                    // See the note on the identical case in RemoteDesktopHandler: an unclamped
                    // delta off the wire can throw out of the Linux backends (RemEx-hnin).
                    inputSimulation.MouseScroll(
                        CoordinateValidation.ClampScrollDelta(input.DeltaX),
                        CoordinateValidation.ClampScrollDelta(input.DeltaY));
                    break;
                case InputEventTypes.KeyDown when input.KeyCode.HasValue:
                    inputSimulation.KeyDown(input.KeyCode.Value);
                    _heldKeys.Pressed(input.KeyCode.Value);
                    break;
                case InputEventTypes.KeyUp when input.KeyCode.HasValue:
                    inputSimulation.KeyUp(input.KeyCode.Value);
                    _heldKeys.Released(input.KeyCode.Value);
                    break;
                case InputEventTypes.TypeText when input.Text is not null:
                    inputSimulation.TypeText(input.Text);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Deliberately broader than the Remote Desktop dispatcher's list, and it always was —
            // which is why this path never had that one's failure mode. Checked as part of RemEx-q4wm
            // rather than changed: here the switch runs INLINE on the receive loop, so a swallowed
            // event costs that event and the next message is read normally. There is no consumer
            // thread to lose. RemoteDesktopHandler queues instead, and an escape there ended the
            // consuming loop and every remaining input for the session.
            //
            // No cancellation carve-out here, and none in the other dispatcher either. One was
            // written into the first draft of RemEx-q4wm on the theory that excluding
            // OperationCanceledException preserved a graceful-shutdown path; it does not. Neither
            // handler shuts down that way — this one ends when the receive loop ends, and the other
            // when Dispose calls CompleteAdding on an untokened queue — so the only thing such a
            // filter can do is give a backend exception an unguarded route out.
            logger.LogWarning(ex, "Failed to dispatch input: {Type}", input.EventType);
        }
    }

    /// <summary>
    /// Releases every key this client still had down when its connection ended (RemEx-73dc).
    /// </summary>
    /// <remarks>
    /// Reached because the connection site holds this in a <c>using</c>, so it runs on a clean close,
    /// a dropped socket and an exception alike. Best-effort per key: one failing release must not
    /// strand the rest, which for modifiers is the difference between a stuck Ctrl and a stuck
    /// Ctrl+Shift+Alt.
    ///
    /// Unlike the Remote Desktop handler there is no input queue to drain first — this path
    /// dispatches inline on the receive loop, so by the time the connection is being disposed no
    /// further input can arrive.
    /// </remarks>
    public void Dispose()
    {
        var held = _heldKeys.TakeAll();
        if (held.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Releasing {Count} key(s) still held by the client at disconnect: {Keys}",
            held.Count, string.Join(", ", held));

        foreach (var keyCode in held)
        {
            try
            {
                inputSimulation.KeyUp(keyCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release held key {KeyCode} at disconnect.", keyCode);
            }
        }
    }
}
