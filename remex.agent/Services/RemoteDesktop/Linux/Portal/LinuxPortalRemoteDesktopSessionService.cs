using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.RemoteDesktop.Linux.Portal;

/// <summary>
/// State of the managed portal remote desktop session.
/// </summary>
[SupportedOSPlatform("linux")]
public enum PortalSessionState
{
    Idle,
    Creating,
    Active,
    Restarting,
    Failed,
    Closed,
}

/// <summary>
/// Manages the lifecycle of an xdg-desktop-portal combined
/// RemoteDesktop + ScreenCast session.
///
/// Responsibilities:
///   - Open a combined session via D-Bus (RemoteDesktop.CreateSession)
///   - Select devices (keyboard + pointer)
///   - Select sources (monitor; cursor mode configurable, hidden by default)
///   - Start the session and surface the PipeWire node IDs to the caller
///   - Close the session cleanly on dispose
///
/// All D-Bus interaction goes through <see cref="PortalDbusHelper.CallPortalAsync"/>,
/// which implements the portal Request/Response handshake correctly: the method
/// call returns immediately with a Request object path, and the actual result is
/// delivered later via a <c>Response</c> signal on that Request.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPortalRemoteDesktopSessionService : IAsyncDisposable
{
    private readonly ILogger<LinuxPortalRemoteDesktopSessionService> _logger;
    private readonly string _appId;

    private DBusConnection? _conn;
    private string? _normalizedSender;

    private volatile PortalSessionState _state = PortalSessionState.Idle;
    private PortalStartResult? _lastResult;
    private string? _sessionHandle;

    /// <summary>Fired when a new session is established and PipeWire node IDs are available.</summary>
    public event Action<PortalStartResult>? SessionStarted;

    /// <summary>Fired when the portal session is lost and a restart has been initiated.</summary>
    public event Action? SessionLost;

    public PortalSessionState State => _state;
    public PortalStartResult? CurrentSession => _lastResult;

    public LinuxPortalRemoteDesktopSessionService(
        string appId = "com.clindsay94.RemEx",
        ILogger<LinuxPortalRemoteDesktopSessionService>? logger = null,
        PortalCursorMode cursorMode = PortalCursorMode.Hidden)
    {
        _appId = appId;
        _logger = logger ?? NullLogger<LinuxPortalRemoteDesktopSessionService>.Instance;
        _cursorMode = cursorMode;
    }

    // Cursor compositing requested from the compositor. Hidden by default: the production client
    // (Android) always renders its own cursor from the streamed cursor state, so an Embedded
    // compositor cursor showed up as a SECOND cursor in every frame. Embedded remains available
    // for legacy clients that rely on host-composited cursors. (RemEx-lq6h)
    private readonly PortalCursorMode _cursorMode;

    /// <summary>
    /// Opens the portal session and starts the PipeWire stream.
    /// Returns the node IDs on success, throws on unrecoverable failure.
    /// </summary>
    public async Task<PortalStartResult> StartSessionAsync(CancellationToken ct = default)
    {
        _state = PortalSessionState.Creating;
        _logger.LogInformation(
            "Opening xdg-desktop-portal RemoteDesktop + ScreenCast session (app_id={AppId}).",
            _appId);

        try
        {
            if (!await EnsureConnectionAsync(ct))
                throw new InvalidOperationException(
                    "Failed to connect to D-Bus session bus (DBUS_SESSION_BUS_ADDRESS unset?).");

            string sessionInterface = PortalDbusNames.RemoteDesktopInterface;
            bool isFallbackScreenCastOnly = false;
            Dictionary<string, VariantValue>? createResults = null;

            // Step 1 — CreateSession on RemoteDesktop. If the portal frontend doesn't
            // expose the interface, try one round of stale-frontend recovery before
            // falling back to ScreenCast-only.
            createResults = await TryCreateRemoteDesktopSessionAsync(ct);
            if (createResults is null)
            {
                isFallbackScreenCastOnly = true;
                sessionInterface = PortalDbusNames.ScreenCastInterface;
            }

            if (isFallbackScreenCastOnly)
            {
                createResults = await PortalDbusHelper.CallPortalAsync(
                    _conn!,
                    _normalizedSender!,
                    PortalDbusNames.ScreenCastInterface,
                    method: "CreateSession",
                    signature: "a{sv}",
                    requestTimeout: TimeSpan.FromSeconds(30),
                    writeArgs: (ref MessageWriter w, string handleToken) =>
                    {
                        var sessionToken = $"remex_session_{handleToken}";
                        var dictStart = w.WriteDictionaryStart();

                        w.WriteDictionaryEntryStart();
                        w.WriteString("handle_token");
                        w.WriteVariantString(handleToken);

                        w.WriteDictionaryEntryStart();
                        w.WriteString("session_handle_token");
                        w.WriteVariantString(sessionToken);

                        w.WriteDictionaryEnd(dictStart);
                    },
                    logger: _logger,
                    ct: ct);
            }

            if (createResults is null)
            {
                _state = PortalSessionState.Failed;
                throw new InvalidOperationException(
                    "Portal CreateSession failed or returned non-zero response. " +
                    "Ensure xdg-desktop-portal and a backend (xdg-desktop-portal-kde) are running.");
            }

            if (!createResults.TryGetValue("session_handle", out var sessionVariant))
            {
                _state = PortalSessionState.Failed;
                throw new InvalidOperationException(
                    "Portal CreateSession Response missing 'session_handle' key.");
            }

            var sessionHandle = sessionVariant.GetString();
            _sessionHandle = sessionHandle;
            _logger.LogDebug("Portal session handle: {Handle}", sessionHandle);

            // Persistence: replayed into SelectDevices (RemoteDesktop portal v2) or
            // SelectSources (legacy / ScreenCast-only) below so the portal grants access
            // WITHOUT re-prompting on every reconnect. This is essential for
            // unattended/remote access where nobody can click "Share".
            var savedRestoreToken = LoadRestoreToken();
            if (!string.IsNullOrEmpty(savedRestoreToken))
                _logger.LogInformation("Replaying saved portal restore_token to avoid the permission prompt.");

            // Step 2 — SelectDevices on RemoteDesktop (Keyboard | Pointer = 3).
            // Skip this step for ScreenCast-only sessions.
            //
            // RemoteDesktop portal version 2 moved session persistence HERE: persist_mode +
            // restore_token are SelectDevices options (not SelectSources ones), and the
            // refreshed token comes back in the Start results. This is the only
            // prompt-free-reconnect mechanism KDE Plasma accepts for remote desktop
            // sessions — its portal rejects ScreenCast persistence on RD sessions outright
            // (see Step 3 / RemEx-82fk). (RemEx-mswt)
            bool persistViaSelectDevices = false;
            if (!isFallbackScreenCastOnly)
            {
                var rdVersion = await GetPortalInterfaceVersionAsync(
                    PortalDbusNames.RemoteDesktopInterface, ct);
                var requestDevicePersist = rdVersion >= 2;
                if (!requestDevicePersist)
                {
                    _logger.LogInformation(
                        "RemoteDesktop portal version {Version} predates SelectDevices persistence " +
                        "(needs 2); will request ScreenCast persistence instead.", rdVersion);
                }

                PortalDbusHelper.ArgWriter selectDevicesArgs = (ref MessageWriter w, string handleToken) =>
                {
                    w.WriteObjectPath(sessionHandle);
                    var dictStart = w.WriteDictionaryStart();

                    w.WriteDictionaryEntryStart();
                    w.WriteString("handle_token");
                    w.WriteVariantString(handleToken);

                    w.WriteDictionaryEntryStart();
                    w.WriteString("types");
                    w.WriteVariantUInt32((uint)(PortalDeviceType.Keyboard | PortalDeviceType.Pointer));

                    if (requestDevicePersist)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString("persist_mode");
                        w.WriteVariantUInt32((uint)PortalPersistMode.PersistUntilRevoked);

                        if (!string.IsNullOrEmpty(savedRestoreToken))
                        {
                            w.WriteDictionaryEntryStart();
                            w.WriteString("restore_token");
                            w.WriteVariantString(savedRestoreToken);
                        }
                    }

                    w.WriteDictionaryEnd(dictStart);
                };

                Dictionary<string, VariantValue>? selectDevResults;
                try
                {
                    selectDevResults = await PortalDbusHelper.CallPortalAsync(
                        _conn!,
                        _normalizedSender!,
                        PortalDbusNames.RemoteDesktopInterface,
                        method: "SelectDevices",
                        signature: "oa{sv}",
                        requestTimeout: TimeSpan.FromSeconds(30),
                        writeArgs: selectDevicesArgs,
                        logger: _logger,
                        ct: ct);
                }
                catch (DBusErrorReplyException ex) when (requestDevicePersist && (
                    ex.ErrorName == "org.freedesktop.portal.Error.InvalidArgument" ||
                    (ex.Message?.Contains("persist", StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    // Portals must ignore unknown options, but KDE has shipped hard rejections
                    // for persistence before (RemEx-82fk) — never let persistence take down the
                    // session. Retry once without it; Step 3 then attempts the legacy path.
                    _logger.LogWarning(
                        "Portal rejected persist_mode on SelectDevices ({Error}); retrying without " +
                        "persistence.", ex.ErrorName);
                    requestDevicePersist = false;
                    selectDevResults = await PortalDbusHelper.CallPortalAsync(
                        _conn!,
                        _normalizedSender!,
                        PortalDbusNames.RemoteDesktopInterface,
                        method: "SelectDevices",
                        signature: "oa{sv}",
                        requestTimeout: TimeSpan.FromSeconds(30),
                        writeArgs: selectDevicesArgs,
                        logger: _logger,
                        ct: ct);
                }

                if (selectDevResults is null)
                {
                    _state = PortalSessionState.Failed;
                    await CloseSessionCoreAsync();
                    throw new InvalidOperationException("Portal SelectDevices failed.");
                }

                persistViaSelectDevices = requestDevicePersist;
            }

            // Step 3 — SelectSources on ScreenCast (Monitor; cursor per _cursorMode, Hidden by default).

            // When persistence was already requested via SelectDevices (RemoteDesktop v2),
            // it MUST NOT be repeated here: KDE's portal rejects ScreenCast persistence on
            // remote desktop sessions with InvalidArgument "Remote desktop sessions cannot
            // persist" (RemEx-82fk). Only legacy RD sessions (portal v1) and ScreenCast-only
            // fallback sessions request persistence through SelectSources — and if the
            // compositor rejects even that, retry once WITHOUT persistence so a failed
            // persist request can never take down capture entirely.
            var requestPersist = !persistViaSelectDevices;
            PortalDbusHelper.ArgWriter selectSourcesArgs = (ref MessageWriter w, string handleToken) =>
            {
                w.WriteObjectPath(sessionHandle);
                var dictStart = w.WriteDictionaryStart();

                w.WriteDictionaryEntryStart();
                w.WriteString("handle_token");
                w.WriteVariantString(handleToken);

                w.WriteDictionaryEntryStart();
                w.WriteString("types");
                w.WriteVariantUInt32((uint)PortalSourceType.Monitor);

                w.WriteDictionaryEntryStart();
                w.WriteString("cursor_mode");
                w.WriteVariantUInt32((uint)_cursorMode);

                if (requestPersist)
                {
                    w.WriteDictionaryEntryStart();
                    w.WriteString("persist_mode");
                    w.WriteVariantUInt32((uint)PortalPersistMode.PersistUntilRevoked);

                    if (!string.IsNullOrEmpty(savedRestoreToken))
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString("restore_token");
                        w.WriteVariantString(savedRestoreToken);
                    }
                }

                w.WriteDictionaryEnd(dictStart);
            };

            Dictionary<string, VariantValue>? selectSrcResults;
            try
            {
                selectSrcResults = await PortalDbusHelper.CallPortalAsync(
                    _conn!,
                    _normalizedSender!,
                    PortalDbusNames.ScreenCastInterface,
                    method: "SelectSources",
                    signature: "oa{sv}",
                    requestTimeout: TimeSpan.FromSeconds(30),
                    writeArgs: selectSourcesArgs,
                    logger: _logger,
                    ct: ct);
            }
            catch (DBusErrorReplyException ex) when (requestPersist && (
                ex.ErrorName == "org.freedesktop.portal.Error.InvalidArgument" ||
                (ex.Message?.Contains("persist", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                _logger.LogWarning(
                    "Portal rejected persist_mode ({Error}); retrying SelectSources without " +
                    "persistence. Remote desktop will work, but this compositor will re-prompt " +
                    "for the screen-share on each connect (no prompt-free reconnect for RemoteDesktop).",
                    ex.ErrorName);
                requestPersist = false;
                selectSrcResults = await PortalDbusHelper.CallPortalAsync(
                    _conn!,
                    _normalizedSender!,
                    PortalDbusNames.ScreenCastInterface,
                    method: "SelectSources",
                    signature: "oa{sv}",
                    requestTimeout: TimeSpan.FromSeconds(30),
                    writeArgs: selectSourcesArgs,
                    logger: _logger,
                    ct: ct);
            }

            if (selectSrcResults is null)
            {
                _state = PortalSessionState.Failed;
                await CloseSessionCoreAsync();
                throw new InvalidOperationException("Portal SelectSources failed.");
            }

            // Step 4 — Start the session. Shows the permission dialog, so use a generous timeout.
            if (isFallbackScreenCastOnly)
            {
                _logger.LogInformation("Waiting for user to grant portal ScreenCast permission...");
            }
            else
            {
                _logger.LogInformation("Waiting for user to grant portal RemoteDesktop+ScreenCast permission...");
            }
            var startResults = await PortalDbusHelper.CallPortalAsync(
                _conn!,
                _normalizedSender!,
                sessionInterface,
                method: "Start",
                signature: "osa{sv}",
                requestTimeout: TimeSpan.FromMinutes(2),
                writeArgs: (ref MessageWriter w, string handleToken) =>
                {
                    w.WriteObjectPath(sessionHandle);
                    w.WriteString(string.Empty); // parent_window
                    var dictStart = w.WriteDictionaryStart();

                    w.WriteDictionaryEntryStart();
                    w.WriteString("handle_token");
                    w.WriteVariantString(handleToken);

                    w.WriteDictionaryEnd(dictStart);
                },
                logger: _logger,
                ct: ct);

            if (startResults is null)
            {
                _state = PortalSessionState.Failed;
                await CloseSessionCoreAsync();
                throw new InvalidOperationException(
                    "Portal Start failed: user declined permission or dialog timed out.");
            }

            // Persist the (possibly refreshed) restore_token so the next connection
            // skips the permission dialog entirely (unattended/remote access).
            if (startResults.TryGetValue("restore_token", out var restoreTokenVariant))
            {
                try
                {
                    var newToken = restoreTokenVariant.GetString();
                    if (!string.IsNullOrEmpty(newToken))
                    {
                        SaveRestoreToken(newToken);
                        _logger.LogInformation("Portal returned a restore_token; saved for prompt-free reconnects.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read/persist portal restore_token.");
                }
            }

            var streams = ParseStreams(startResults, _logger);
            var nodeIds = new List<uint>(streams.Count);
            foreach (var s in streams)
                nodeIds.Add(s.NodeId);

            if (nodeIds.Count == 0)
            {
                _logger.LogWarning(
                    "Portal Start succeeded but returned no PipeWire streams. " +
                    "Capture will fall back to legacy path.");
            }

            // Obtain the portal-scoped PipeWire fd on THIS connection (the one that owns
            // the session). Passing it to the native bridge avoids the sender-scope
            // rejection that occurs when the native library opens its own sd-bus
            // connection — the root cause of the KDE "0 frames -> 1 FPS legacy" regression.
            SafeFileHandle? pipeWireFd = null;
            if (nodeIds.Count > 0)
            {
                try
                {
                    pipeWireFd = await OpenPipeWireRemoteAsync(sessionHandle, ct);
                    _logger.LogInformation(
                        "OpenPipeWireRemote succeeded; portal-scoped PipeWire fd acquired on the session-owning connection.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OpenPipeWireRemote failed; native bridge will attempt its own fd acquisition (may yield 0 frames on KDE).");
                }
            }

            var result = new PortalStartResult
            {
                Success = true,
                SessionHandle = sessionHandle,
                NodeIds = nodeIds.AsReadOnly(),
                Streams = streams,
                PipeWireFd = pipeWireFd,
            };

            _lastResult = result;
            _state = PortalSessionState.Active;
            _logger.LogInformation(
                "Portal session active. Streams: [{Streams}]",
                string.Join(", ", System.Linq.Enumerable.Select(streams,
                    s => $"node={s.NodeId} {s.Width}x{s.Height}@({s.X},{s.Y})")));

            SessionStarted?.Invoke(result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = PortalSessionState.Failed;
            _logger.LogError(ex, "Portal session creation failed.");
            throw;
        }
    }

    /// <summary>
    /// Closes and restarts the portal session. Used on monitor hotplug events.
    /// </summary>
    public async Task<PortalStartResult> RestartSessionAsync(CancellationToken ct = default)
    {
        _state = PortalSessionState.Restarting;
        SessionLost?.Invoke();

        await CloseSessionCoreAsync();
        return await StartSessionAsync(ct);
    }

    /// <summary>
    /// Injects an ABSOLUTE pointer position through this unified RemoteDesktop + ScreenCast
    /// session (fire-and-forget). Coordinates are relative to the given ScreenCast stream's
    /// surface — the compositor clamps them to the stream bounds natively, which eliminates the
    /// cumulative drift of the relative-delta emulation used by input-only portal sessions.
    /// Returns false when the session is not active (caller falls back to relative motion).
    /// (RemEx-lq6h)
    /// </summary>
    public bool TryNotifyPointerMotionAbsolute(uint streamNodeId, double streamX, double streamY)
    {
        var sessionHandle = _sessionHandle;
        var conn = _conn;
        if (_state != PortalSessionState.Active || sessionHandle is null || conn is null)
        {
            return false;
        }

        try
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDbusNames.PortalService,
                path: PortalDbusNames.PortalPath,
                @interface: PortalDbusNames.RemoteDesktopInterface,
                member: "NotifyPointerMotionAbsolute",
                signature: "oa{sv}udd",
                flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(sessionHandle);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart);
            writer.WriteUInt32(streamNodeId);
            writer.WriteDouble(streamX);
            writer.WriteDouble(streamY);
            var buf = writer.CreateMessage();
            conn.TrySendMessage(buf);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyPointerMotionAbsolute failed.");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _state = PortalSessionState.Closed;
        await CloseSessionCoreAsync();

        if (_conn is not null)
        {
            try { _conn.Dispose(); }
            catch { /* best-effort */ }
            _conn = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts <c>RemoteDesktop.CreateSession</c>; on the specific D-Bus errors
    /// indicating the interface isn't exposed, runs portal-frontend recovery
    /// once per process and retries. Returns <c>null</c> when the interface
    /// remains unavailable after recovery (caller falls back to ScreenCast-only).
    /// </summary>
    private async Task<Dictionary<string, VariantValue>?> TryCreateRemoteDesktopSessionAsync(
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await PortalDbusHelper.CallPortalAsync(
                    _conn!,
                    _normalizedSender!,
                    PortalDbusNames.RemoteDesktopInterface,
                    method: "CreateSession",
                    signature: "a{sv}",
                    requestTimeout: TimeSpan.FromSeconds(30),
                    writeArgs: (ref MessageWriter w, string handleToken) =>
                    {
                        var sessionToken = $"remex_session_{handleToken}";
                        var dictStart = w.WriteDictionaryStart();

                        w.WriteDictionaryEntryStart();
                        w.WriteString("handle_token");
                        w.WriteVariantString(handleToken);

                        w.WriteDictionaryEntryStart();
                        w.WriteString("session_handle_token");
                        w.WriteVariantString(sessionToken);

                        w.WriteDictionaryEnd(dictStart);
                    },
                    logger: _logger,
                    ct: ct);
            }
            catch (DBusErrorReplyException ex) when (
                ex.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod" ||
                ex.ErrorName == "org.freedesktop.DBus.Error.ServiceUnknown" ||
                ex.ErrorName == "org.freedesktop.DBus.Error.UnknownInterface")
            {
                if (attempt == 0 && PortalRecoveryHelper.ShouldAttempt())
                {
                    _logger.LogWarning(ex,
                        "RemoteDesktop portal interface missing. Attempting one-shot " +
                        "recovery (restart xdg-desktop-portal with re-imported env)...");
                    var recovered = await PortalRecoveryHelper.TryRecoverAsync(_logger, ct)
                        ;
                    if (recovered)
                    {
                        // Loop and retry CreateSession on the freshly-restarted frontend.
                        continue;
                    }
                }
                _logger.LogWarning(ex,
                    "RemoteDesktop portal interface not available. Falling back to " +
                    "ScreenCast-only session.");
                return null;
            }
        }
        return null;
    }

    private async Task<bool> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_conn is not null) return true;

        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            _logger.LogWarning(
                "DBUS_SESSION_BUS_ADDRESS unset; portal capture session cannot start.");
            return false;
        }

        try
        {
            var conn = new DBusConnection(address);
            await conn.ConnectAsync();
            _conn = conn;

            var unique = _conn.UniqueName;
            if (string.IsNullOrEmpty(unique))
            {
                _logger.LogWarning(
                    "D-Bus connection has no unique name; portal session cannot proceed.");
                return false;
            }

            _normalizedSender = PortalDbusHelper.NormalizeSender(unique);
            _logger.LogDebug("D-Bus connected (sender={Sender}, normalized={Normalized}).",
                unique, _normalizedSender);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to D-Bus session bus.");
            return false;
        }
    }

    private async Task CloseSessionCoreAsync()
    {
        if (_sessionHandle is null || _conn is null)
        {
            _sessionHandle = null;
            return;
        }

        try
        {
            _logger.LogDebug("Closing portal session {Handle}.", _sessionHandle);
            var writer = _conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDbusNames.PortalService,
                path: _sessionHandle,
                @interface: PortalDbusNames.SessionInterface,
                member: "Close",
                flags: MessageFlags.NoReplyExpected);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
            await Task.Yield();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing portal session failed (best effort).");
        }
        finally
        {
            _sessionHandle = null;
        }
    }

    /// <summary>
    /// Parses the <c>streams</c> entry from the Start Response results, including each
    /// stream's node_id and (when present) its <c>position</c> and <c>size</c> so the
    /// caller can build a per-monitor catalog / crop to a selected monitor.
    /// Signature: <c>a(ua{sv})</c> — array of (node_id, properties) structs.
    /// </summary>
    private static IReadOnlyList<PortalStreamInfo> ParseStreams(
        Dictionary<string, VariantValue> results,
        ILogger logger)
    {
        if (!results.TryGetValue("streams", out var streamsVariant))
        {
            logger.LogWarning("Portal Start Response has no 'streams' entry.");
            return Array.Empty<PortalStreamInfo>();
        }

        var list = new List<PortalStreamInfo>();
        try
        {
            // streamsVariant is a(ua{sv}) — array of structs. GetItem(0) = uint32 node_id,
            // GetItem(1) = a{sv} properties (keys "position" (ii), "size" (ii), ...).
            var streams = streamsVariant.GetArray<VariantValue>();
            foreach (var stream in streams)
            {
                uint nodeId = stream.GetItem(0).GetUInt32();
                int x = 0, y = 0, w = 0, h = 0;
                try
                {
                    var props = stream.GetItem(1).GetDictionary<string, VariantValue>();
                    if (props.TryGetValue("size", out var sizeV))
                        (w, h) = ReadIntPair(sizeV);
                    if (props.TryGetValue("position", out var posV))
                        (x, y) = ReadIntPair(posV);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not parse geometry for stream node {NodeId}.", nodeId);
                }

                list.Add(new PortalStreamInfo
                {
                    NodeId = nodeId,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to parse 'streams' from portal Start Response.");
        }

        return list.AsReadOnly();
    }

    /// <summary>
    /// Reads a portal <c>(ii)</c> pair (position or size), unwrapping the surrounding
    /// variant when the D-Bus value is boxed as <c>v</c>.
    /// </summary>
    private static (int, int) ReadIntPair(VariantValue value)
    {
        var inner = value;
        try { inner = value.GetVariantValue(); }
        catch { /* value was already the (ii) struct, not a boxed variant */ }
        return (inner.GetItem(0).GetInt32(), inner.GetItem(1).GetInt32());
    }

    /// <summary>
    /// Filesystem path where the portal restore_token is cached (per user), enabling
    /// prompt-free reconnects. Opaque token string; not a secret credential but stored
    /// user-private.
    /// </summary>
    private static string RestoreTokenPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Remex",
        "portal_restore_token");

    private string? LoadRestoreToken()
    {
        try
        {
            var path = RestoreTokenPath();
            if (!System.IO.File.Exists(path)) return null;
            var token = System.IO.File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read portal restore_token cache.");
            return null;
        }
    }

    private void SaveRestoreToken(string token)
    {
        try
        {
            var path = RestoreTokenPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, token);
            try
            {
                System.IO.File.SetUnixFileMode(path,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
            }
            catch { /* best-effort perms */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist portal restore_token; reconnects may prompt again.");
        }
    }

    /// <summary>
    /// Reads the <c>version</c> property of a portal interface (plain D-Bus Properties.Get,
    /// not the Request/Response pattern). Portal capabilities are gated on this: e.g.
    /// RemoteDesktop persistence (persist_mode on SelectDevices) needs version >= 2.
    /// Returns 1 — the lowest version any live portal can be — when the property cannot
    /// be read, so callers simply skip newer-version features.
    /// </summary>
    private async Task<uint> GetPortalInterfaceVersionAsync(string interfaceName, CancellationToken ct)
    {
        try
        {
            MessageBuffer buf;
            {
                var writer = _conn!.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: PortalDbusNames.PortalService,
                    path: PortalDbusNames.PortalPath,
                    @interface: "org.freedesktop.DBus.Properties",
                    member: "Get",
                    signature: "ss");
                writer.WriteString(interfaceName);
                writer.WriteString("version");
                buf = writer.CreateMessage();
            }

            var version = await _conn!.CallMethodAsync(
                buf,
                static (Message msg, object? state) =>
                {
                    var reader = msg.GetBodyReader();
                    return reader.ReadVariantValue().GetUInt32();
                });

            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Portal interface {Interface} reports version {Version}.",
                interfaceName, version);
            return version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Could not read the 'version' property of {Interface}; assuming version 1.",
                interfaceName);
            return 1;
        }
    }

    /// <summary>
    /// Calls <c>org.freedesktop.portal.ScreenCast.OpenPipeWireRemote</c> on THIS
    /// connection (the session owner) and returns the portal-scoped PipeWire fd from
    /// the method reply. Unlike CreateSession/Start this is a plain method call whose
    /// reply carries the fd directly (type <c>h</c>) — not the Request/Response signal
    /// pattern — so it does not use <see cref="PortalDbusHelper.CallPortalAsync"/>.
    /// </summary>
    private async Task<SafeFileHandle?> OpenPipeWireRemoteAsync(
        string sessionHandle, CancellationToken ct)
    {
        MessageBuffer buf;
        {
            var writer = _conn!.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDbusNames.PortalService,
                path: PortalDbusNames.PortalPath,
                @interface: PortalDbusNames.ScreenCastInterface,
                member: "OpenPipeWireRemote",
                signature: "oa{sv}");
            writer.WriteObjectPath(sessionHandle);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart); // empty options a{sv}
            buf = writer.CreateMessage();
        }

        var fd = await _conn!.CallMethodAsync(
            buf,
            static (Message msg, object? state) =>
            {
                var reader = msg.GetBodyReader();
                return reader.ReadHandle<SafeFileHandle>();
            });

        ct.ThrowIfCancellationRequested();
        return fd;
    }
}
