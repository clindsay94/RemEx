using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Command;
using Remex.Core.Services.Network;
using Remex.Core.Services.Security;
using Remex.Host.Services.Security;

namespace Remex.Host.Services.IPC;

public class LocalIpcServerService : BackgroundService
{
    private readonly ILogger<LocalIpcServerService> _logger;
    private readonly ISystemCommandService _commandService;
    private readonly IWakeOnLanService _wakeOnLanService;
    private readonly IPairingService _pairingService;
    private readonly IAppLauncherService _appLauncherService;

    // Single canonical pipe name shared with every local IPC client (see Remex.Core RemExLocalIPC).
    private const string PipeName = RemExLocalIPC.PipeName;
    private const string MutexName = @"Global\RemExServiceMutex";

    // State-changing or secret-returning commands require the caller to be the interactive console
    // user. Read-only/idempotent commands (e.g. WAKEONLAN, LAUNCHAPP) do not gate on identity beyond
    // the pipe ACL, which already restricts connections to LocalSystem and the interactive user.
    private static readonly HashSet<string> PrivilegedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHUTDOWN",
        "FORCESHUTDOWN",
        "RESTART",
        "FORCERESTART",
        "RESTARTTOUEFI",
        "SLEEP",
        "HIBERNATE",
        "MONITOROFF",
        "SIGNOUT",
        "LOCK",
        "GETPAIRINGPIN",
        "STARTPAIRING",
        "GENERATEPAIRINGPIN",
    };

    // Per-connection read timeout. A connected client that never sends a frame must not pin a pipe
    // instance forever, and the canonical pipe is single-purpose request/response.
    private static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(15);

    private Mutex? _mutex;

    public LocalIpcServerService(
        ILogger<LocalIpcServerService> logger,
        ISystemCommandService commandService,
        IWakeOnLanService wakeOnLanService,
        IPairingService pairingService,
        IAppLauncherService appLauncherService)
    {
        _logger = logger;
        _commandService = commandService;
        _wakeOnLanService = wakeOnLanService;
        _pairingService = pairingService;
        _appLauncherService = appLauncherService;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var mutexSecurity = new MutexSecurity();
                var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                mutexSecurity.AddAccessRule(new MutexAccessRule(worldSid, MutexRights.FullControl, AccessControlType.Allow));

                _mutex = MutexAcl.Create(true, MutexName, out bool createdNew, mutexSecurity);
                if (!createdNew)
                {
                    _logger.LogWarning($"Mutex {MutexName} already exists. IPC Server might be running.");
                }
            }
            else
            {
                _mutex = new Mutex(true, MutexName, out bool createdNew);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Global Mutex for IPC.");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipeServer = null;
            try
            {
                pipeServer = CreatePipeServer();
                await pipeServer.WaitForConnectionAsync(stoppingToken);

                // Hand ownership of the connected stream to the per-connection handler and immediately
                // loop to accept the next client. The handler disposes the stream when it finishes.
                var accepted = pipeServer;
                pipeServer = null;
                _ = HandleClientAsync(accepted, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
                pipeServer?.Dispose();
            }
            catch (UnauthorizedAccessException ex)
            {
                pipeServer?.Dispose();
                _logger.LogWarning(ex,
                    "Access denied creating named pipe '{Pipe}'. " +
                    "Another process may own the pipe, or the service account lacks pipe-creation rights. Retrying in 5s.",
                    PipeName);
                await Task.Delay(5000, stoppingToken);
            }
            catch (Exception ex)
            {
                pipeServer?.Dispose();
                _logger.LogError(ex, "Error in IPC Server execution loop.");
                await Task.Delay(1000, stoppingToken); // Backoff
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsPipeServer();
        }

        // Linux/macOS: named pipes are backed by a Unix domain socket created under the user's runtime
        // dir. Owner-only (0600) semantics restrict access to the service account, mirroring the
        // Windows ACL intent without per-rule SIDs.
        return new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsPipeServer()
    {
        var pipeSecurity = new PipeSecurity();

        // LocalSystem (the service account itself) needs full control to own and recreate the pipe.
        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(localSystemSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        // The interactive (console) user — the tray/dashboard UI — gets read/write only, never the
        // ability to change the ACL or take ownership. Replaces the former Everyone (WorldSid) rule.
        var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(interactiveSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Also grant FullControl to the current identity so elevated/service accounts can recreate the
        // pipe even when a stale handle exists (stale-handle recovery).
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            pipeSecurity);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipeServer, CancellationToken token)
    {
        using (pipeServer)
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeoutCts.CancelAfter(ConnectionReadTimeout);
            var ct = timeoutCts.Token;

            try
            {
                var payload = await RemExLocalIPC.ReadFrameAsync(pipeServer, ct);
                if (payload == null)
                {
                    return;
                }

                CommandRequest? request = null;
                try
                {
                    request = RemexJson.Deserialize(payload, RemexJsonSerializerContext.Default.CommandRequest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize command request over IPC.");
                }

                CommandResponse response;

                if (request == null)
                {
                    response = new CommandResponse(false, "Invalid Request", "Payload could not be parsed as CommandRequest.");
                }
                else if (!IsCallerAuthorized(pipeServer, request.Action))
                {
                    _logger.LogWarning("Rejected privileged IPC command '{Action}': caller is not the interactive console user.", request.Action);
                    response = new CommandResponse(false, "Unauthorized", "This command may only be issued by the signed-in interactive user.");
                }
                else
                {
                    response = await ExecuteCommandAsync(request);
                }

                var responseBytes = RemexJson.SerializeToUtf8Bytes(response, RemexJsonSerializerContext.Default.CommandResponse);
                await RemExLocalIPC.WriteFrameAsync(pipeServer, responseBytes, ct);
            }
            catch (OperationCanceledException)
            {
                // Connection timed out or the host is shutting down; drop the connection quietly.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling IPC client connection.");
            }
        }
    }

    /// <summary>
    /// Confirms that a state-changing or secret-returning command originates from the signed-in
    /// interactive console user. On Windows this impersonates the connected client and compares its
    /// user SID with the SID owning the active console session. On Linux the pipe's owner-only (0600)
    /// semantics already restrict access to the service account, so no extra impersonation check is
    /// performed (the OS enforces it at connect time).
    /// </summary>
    private bool IsCallerAuthorized(NamedPipeServerStream pipeServer, string action)
    {
        if (!PrivilegedActions.Contains(action))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Divergence: Linux relies on Unix-socket 0600 owner-only access; impersonation/console-SID
            // comparison is Windows-specific.
            return true;
        }

        return IsConnectedClientInteractiveUser(pipeServer);
    }

    [SupportedOSPlatform("windows")]
    private bool IsConnectedClientInteractiveUser(NamedPipeServerStream pipeServer)
    {
        try
        {
            SecurityIdentifier? clientSid = null;
            pipeServer.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                clientSid = identity.User;
            });

            if (clientSid == null)
            {
                return false;
            }

            // LocalSystem itself (e.g. another service component) is always allowed.
            var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (clientSid.Equals(localSystemSid))
            {
                return true;
            }

            var consoleUserSid = TryGetActiveConsoleUserSid();
            return consoleUserSid != null && clientSid.Equals(consoleUserSid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify IPC caller identity; rejecting the command.");
            return false;
        }
    }

    /// <summary>
    /// Resolves the SID of the user owning the active console session, or null when no user is signed
    /// in (e.g. the lock/login screen) or the lookup fails.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? TryGetActiveConsoleUserSid()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            return null;
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr token))
        {
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User;
        }
        finally
        {
            CloseHandle(token);
        }
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
                case "MONITOROFF":
                    await _commandService.MonitorOff();
                    return new CommandResponse(true, "Monitor off command executed successfully.", null);
                case "SIGNOUT":
                    await _commandService.SignOut();
                    return new CommandResponse(true, "SignOut command executed successfully.", null);
                case "LOCK":
                    await _commandService.Lock();
                    return new CommandResponse(true, "Lock command executed successfully.", null);
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
                case "LAUNCHAPP":
                    if (request.Parameters == null
                        || !request.Parameters.TryGetValue("TargetPath", out var targetPath)
                        || string.IsNullOrWhiteSpace(targetPath))
                    {
                        return new CommandResponse(false, "No target path provided.", null);
                    }

                    try
                    {
                        await _appLauncherService.LaunchAppAsync(targetPath);
                        return new CommandResponse(true, "App launched successfully.", null);
                    }
                    catch (Exception ex)
                    {
                        return new CommandResponse(false, $"Error launching app: {ex.Message}", ex.ToString());
                    }
                case "GETPAIRINGPIN":
                    if (_pairingService.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs))
                    {
                        return new CommandResponse(true, "Active pairing PIN retrieved.", null)
                        {
                            PairingPinInfo = new PairingPinInfo(pin, expiresAtUnixMs),
                        };
                    }

                    return new CommandResponse(false, "No active pairing session.", null);

                case "STARTPAIRING":
                case "GENERATEPAIRINGPIN":
                    try
                    {
                        var state = await _pairingService.GetOrStartPairingAsync(CancellationToken.None);
                        return new CommandResponse(true, "Pairing session started and PIN generated.", null)
                        {
                            PairingPinInfo = new PairingPinInfo(state.Pin, state.ExpiresAtUnixMs),
                        };
                    }
                    catch (Exception ex)
                    {
                        return new CommandResponse(false, $"Failed to start pairing session: {ex.Message}", null);
                    }
                default:
                    return new CommandResponse(false, "Unknown Command", $"Command action '{request.Action}' is not supported.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error executing command {request.Action}");
            return new CommandResponse(false, "Command Failed", ex.Message);
        }
    }

    public override void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ignore if we don't own the mutex
        }

        _mutex?.Dispose();
        base.Dispose();
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
