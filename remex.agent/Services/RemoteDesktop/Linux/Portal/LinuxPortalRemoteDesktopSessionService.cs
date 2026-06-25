using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
///   - Select sources (monitor + embedded cursor)
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

    private Connection? _conn;
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
        ILogger<LinuxPortalRemoteDesktopSessionService>? logger = null)
    {
        _appId = appId;
        _logger = logger ?? NullLogger<LinuxPortalRemoteDesktopSessionService>.Instance;
    }

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

            // Step 2 — SelectDevices on RemoteDesktop (Keyboard | Pointer = 3).
            // Skip this step for ScreenCast-only sessions.
            if (!isFallbackScreenCastOnly)
            {
                var selectDevResults = await PortalDbusHelper.CallPortalAsync(
                    _conn!,
                    _normalizedSender!,
                    PortalDbusNames.RemoteDesktopInterface,
                    method: "SelectDevices",
                    signature: "oa{sv}",
                    requestTimeout: TimeSpan.FromSeconds(30),
                    writeArgs: (ref MessageWriter w, string handleToken) =>
                    {
                        w.WriteObjectPath(sessionHandle);
                        var dictStart = w.WriteDictionaryStart();

                        w.WriteDictionaryEntryStart();
                        w.WriteString("handle_token");
                        w.WriteVariantString(handleToken);

                        w.WriteDictionaryEntryStart();
                        w.WriteString("types");
                        w.WriteVariantUInt32((uint)(PortalDeviceType.Keyboard | PortalDeviceType.Pointer));

                        w.WriteDictionaryEnd(dictStart);
                    },
                    logger: _logger,
                    ct: ct);

                if (selectDevResults is null)
                {
                    _state = PortalSessionState.Failed;
                    await CloseSessionCoreAsync();
                    throw new InvalidOperationException("Portal SelectDevices failed.");
                }
            }

            // Step 3 — SelectSources on ScreenCast (Monitor, Embedded cursor, no persist).
            var selectSrcResults = await PortalDbusHelper.CallPortalAsync(
                _conn!,
                _normalizedSender!,
                PortalDbusNames.ScreenCastInterface,
                method: "SelectSources",
                signature: "oa{sv}",
                requestTimeout: TimeSpan.FromSeconds(30),
                writeArgs: (ref MessageWriter w, string handleToken) =>
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
                    w.WriteVariantUInt32((uint)PortalCursorMode.Embedded);

                    w.WriteDictionaryEntryStart();
                    w.WriteString("persist_mode");
                    w.WriteVariantUInt32((uint)PortalPersistMode.DoNotPersist);

                    w.WriteDictionaryEnd(dictStart);
                },
                logger: _logger,
                ct: ct);

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

            var nodeIds = ParseStreamNodeIds(startResults, _logger);
            if (nodeIds.Count == 0)
            {
                _logger.LogWarning(
                    "Portal Start succeeded but returned no PipeWire streams. " +
                    "Capture will fall back to legacy path.");
            }

            var result = new PortalStartResult
            {
                Success = true,
                SessionHandle = sessionHandle,
                NodeIds = nodeIds,
            };

            _lastResult = result;
            _state = PortalSessionState.Active;
            _logger.LogInformation(
                "Portal session active. PipeWire node IDs: [{Ids}]",
                string.Join(", ", nodeIds));

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
            catch (DBusException ex) when (
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

        var address = Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            _logger.LogWarning(
                "DBUS_SESSION_BUS_ADDRESS unset; portal capture session cannot start.");
            return false;
        }

        try
        {
            var conn = new Connection(address);
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
    /// Parses the <c>streams</c> entry from the Start Response results.
    /// Signature: <c>a(ua{sv})</c> — array of (node_id, properties) structs.
    /// </summary>
    private static IReadOnlyList<uint> ParseStreamNodeIds(
        Dictionary<string, VariantValue> results,
        ILogger logger)
    {
        if (!results.TryGetValue("streams", out var streamsVariant))
        {
            logger.LogWarning("Portal Start Response has no 'streams' entry.");
            return Array.Empty<uint>();
        }

        var nodeIds = new List<uint>();
        try
        {
            // streamsVariant is a(ua{sv}) — array of structs. Each struct is a VariantValue
            // whose first field (GetItem(0)) is the uint32 node_id.
            var streams = streamsVariant.GetArray<VariantValue>();
            foreach (var stream in streams)
            {
                nodeIds.Add(stream.GetItem(0).GetUInt32());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to parse 'streams' from portal Start Response. " +
                "Variant type was {Type}, signature {Sig}.",
                streamsVariant.Type, streamsVariant.GetType().Name);
        }

        return nodeIds.AsReadOnly();
    }
}
