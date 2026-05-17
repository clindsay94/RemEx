using System.Net.WebSockets;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Host.Services;
using Remex.Host.Services.Telemetry;
using Remex.Host.Services.Security;

namespace Remex.Host.Handlers;

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
    PairedClientRegistry pairedClientRegistry)
{
    public async Task HandleAsync(WebSocket webSocket, bool isLoopback, CancellationToken ct)
    {
        // Per-connection pairing gate. Loopback connections come from the embedded host on the
        // same machine, where pairing adds no security and is intentionally skipped on the client
        // side as well — see ConnectionViewModel.IsLoopbackHost. All other connections must
        // complete the PIN-based pairing handshake before issuing destructive or stateful commands.
        // We also check the registry to see if this client (by ID) has previously paired.
        bool isPaired = isLoopback;
        bool pairingStarted = false;

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

                // Check registry for persistent pairing if not already connection-paired
                if (!isPaired && !string.IsNullOrWhiteSpace(message.ClientId))
                {
                    if (pairedClientRegistry.IsClientPaired(message.ClientId))
                    {
                        isPaired = true;
                        logger.LogInformation("Connection authenticated via paired client identity: {ClientId}", message.ClientId);
                    }
                }

                // Reject messages from clients running a protocol version older than 2.
                // 1.x clients will get a clear rejection rather than silently operating in a
                // degraded state.  ProtocolVersion defaults to 2 in RemEx 2.0 messages, so a
                // zero value also indicates a legacy or malformed client.
                if (message.ProtocolVersion < 2)
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
                        }
                        break;

                    // ── 2.0 File Transfer ──
                    case MessageTypes.FileRootsRequest:
                        await fileTransferHandler.HandleFileRootsRequestAsync(webSocket, ct);
                        break;

                    case MessageTypes.FileBrowseRequest:
                        await fileTransferHandler.HandleFileBrowseRequestAsync(message, webSocket, ct);
                        break;

                    case MessageTypes.FileTransferStart:
                        await fileTransferHandler.HandleFileTransferStartAsync(message, webSocket, ct);
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
                        await fileTransferHandler.HandleFileHashRequestAsync(message, webSocket, ct);
                        break;

                    case MessageTypes.FileRootManageRequest:
                        await fileTransferHandler.HandleFileRootManageRequestAsync(message, webSocket, ct);
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


    private async Task<RemexMessage> ExecuteCommandAsync(RemexMessage message)
    {
        try
        {
            switch (message.CommandAction!.ToUpperInvariant())
            {
                case "SHUTDOWN":
                    commandService.Shutdown(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Shutdown executed.");
                case "FORCESHUTDOWN":
                    commandService.ForceShutdown(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Force shutdown executed.");
                case "RESTART":
                    commandService.Restart(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Restart executed.");
                case "FORCERESTART":
                    commandService.ForceRestart(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Force restart executed.");
                case "RESTARTTOUEFI":
                    commandService.RestartToUefi(ParseDelaySeconds(message.CommandParameters));
                    return MakeCommandResponse(true, "Restart to UEFI executed.");
                case "SLEEP":
                    commandService.Sleep();
                    return MakeCommandResponse(true, "Sleep executed.");
                case "HIBERNATE":
                    commandService.Hibernate();
                    return MakeCommandResponse(true, "Hibernate executed.");
                case "MONITOROFF":
                    commandService.MonitorOff();
                    return MakeCommandResponse(true, "Monitor off executed.");
                case "SIGNOUT":
                    commandService.SignOut();
                    return MakeCommandResponse(true, "Sign out executed.");
                case "KILLPROCESS":
                    if (message.CommandParameters?.TryGetValue("ProcessId", out var pidStr) == true
                        && int.TryParse(pidStr, out var pid))
                    {
                        var killed = processMonitorService.KillProcess(pid);
                        return MakeCommandResponse(killed, killed ? "Process killed." : "Failed to kill process.");
                    }
                    return MakeCommandResponse(false, "Missing or invalid ProcessId parameter.");
                case "KILLPROCESSELEVATED":
                    if (message.CommandParameters?.TryGetValue("ProcessId", out var epidStr) == true
                        && int.TryParse(epidStr, out var epid))
                    {
                        // Use the same KillProcess for now as requested.
                        var killed = processMonitorService.KillProcess(epid);
                        return MakeCommandResponse(killed, killed ? "Elevated process kill executed." : "Failed to kill process (elevated).");
                    }
                    return MakeCommandResponse(false, "Missing or invalid ProcessId parameter.");
                case "LOCK":
                    commandService.Lock();
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

    private static RemexMessage MakeCommandResponse(bool success, string msg) => new()
    {
        Type = MessageTypes.CommandResponse,
        CommandSuccess = success,
        CommandMessage = msg,
    };

    /// <summary>
    /// Returns true when the message type must be rejected before the connection has completed
    /// pairing. Ping and the pairing handshake messages are intentionally exempt.
    /// </summary>
    private static bool RequiresPairing(string type) => type switch
    {
        MessageTypes.Ping => false,
        MessageTypes.PairingRequest => false,
        MessageTypes.PairingComplete => false,
        _ => true,
    };

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
