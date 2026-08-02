using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop.Linux.Portal;
using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.Input.Linux;

/// <summary>
/// Injects pointer and keyboard events via the xdg-desktop-portal RemoteDesktop D-Bus API.
///
/// Wayland-only path. Maintains a persistent <see cref="Connection"/> so that the
/// portal session (which is tied to the caller's unique bus name) survives across
/// every Notify* invocation. Permission dialogue is shown once during
/// <see cref="EnsureStartedAsync"/>; afterwards every Notify* method is fire-and-forget.
///
/// Limitations:
///   * <c>NotifyPointerMotionAbsolute</c> requires a unified ScreenCast + RemoteDesktop
///     session (it needs a PipeWire stream id). Input-only sessions cannot dispatch
///     absolute motion; the caller must convert to relative deltas. This method is
///     therefore a no-op on input-only injectors and is kept only for API compatibility.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPortalInputInjector : IAsyncDisposable, IPortalInputSink
{
    private const string PortalDestination = PortalDbusNames.PortalService;
    private const string PortalPath = PortalDbusNames.PortalPath;
    private const string RemoteDesktopInterface = PortalDbusNames.RemoteDesktopInterface;
    private const string SessionInterface = PortalDbusNames.SessionInterface;

    private readonly ILogger _logger;

    private Connection? _conn;
    private string? _sessionHandle;
    private string? _normalizedSender;
    private volatile bool _active;
    private int _startInProgress;

    public bool IsActive => _active;

    public string? SessionHandle => _sessionHandle;

    public LinuxPortalInputInjector(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    // ── Session lifecycle ─────────────────────────────────────────────────

    /// <summary>
    /// Ensures the portal input session is active. Safe to call concurrently; only
    /// one start attempt proceeds at a time. Returns true if the session is active.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (_active) return true;

        if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
        {
            for (var i = 0; i < 1200 && !_active; i++)
                await Task.Delay(100, ct);
            return _active;
        }

        try
        {
            return await StartSessionAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Portal input session start failed.");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _startInProgress, 0);
        }
    }

    private async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        if (!await EnsureConnectionAsync(ct))
            return false;

        _logger.LogInformation(
            "Starting xdg-desktop-portal RemoteDesktop input session (sender={Sender}).",
            _conn!.UniqueName);

        // Step 1: CreateSession — the session_handle is delivered in the Response signal,
        // NOT in the immediate method reply (which only returns the Request object path).
        // When the portal frontend doesn't expose RemoteDesktop, attempt a one-shot
        // recovery (shared with the capture-session service via PortalRecoveryHelper).
        var createResults = await CreateSessionWithRecoveryAsync(ct);

        if (createResults is null)
        {
            _logger.LogWarning("Portal CreateSession failed or returned non-zero response.");
            return false;
        }

        if (!createResults.TryGetValue("session_handle", out var sessionVariant))
        {
            _logger.LogWarning("Portal CreateSession Response missing 'session_handle' key.");
            return false;
        }

        var sessionHandle = sessionVariant.GetString();
        _logger.LogDebug("Portal session handle: {Handle}", sessionHandle);

        // Step 2: SelectDevices (Keyboard | Pointer = 3).
        var selectResults = await PortalDbusHelper.CallPortalAsync(
            _conn!,
            _normalizedSender!,
            RemoteDesktopInterface,
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
                w.WriteVariantUInt32(3u); // Keyboard | Pointer

                w.WriteDictionaryEnd(dictStart);
            },
            logger: _logger,
            ct: ct);

        if (selectResults is null)
        {
            _logger.LogWarning("Portal SelectDevices failed or returned non-zero response.");
            await CloseSessionInternalAsync();
            return false;
        }

        // Step 3: Start — shows the KDE permission dialog. Allow generous timeout.
        _logger.LogInformation("Waiting for user to grant portal RemoteDesktop permission...");
        var startResults = await PortalDbusHelper.CallPortalAsync(
            _conn!,
            _normalizedSender!,
            RemoteDesktopInterface,
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
            _logger.LogWarning("User declined portal RemoteDesktop permission or dialog timed out.");
            await CloseSessionInternalAsync();
            return false;
        }

        _sessionHandle = sessionHandle;
        _active = true;
        _logger.LogInformation("Portal RemoteDesktop input session is active.");
        return true;
    }

    // ── Input injection (fire-and-forget) ─────────────────────────────────

    /// <summary>Injects a relative pointer motion event (trackpad-style).</summary>
    public void NotifyPointerMotionRelative(double dx, double dy)
    {
        if (!_active || _sessionHandle is null || _conn is null) return;

        try
        {
            var writer = _conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: PortalPath,
                @interface: RemoteDesktopInterface,
                member: "NotifyPointerMotion",
                signature: "oa{sv}dd",
                flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(_sessionHandle);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart);
            writer.WriteDouble(dx);
            writer.WriteDouble(dy);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyPointerMotion (relative) failed.");
        }
    }

    /// <summary>
    /// Absolute motion is NOT supported on input-only portal sessions — the portal
    /// method <c>NotifyPointerMotionAbsolute</c> requires a stream id obtained from a
    /// unified ScreenCast + RemoteDesktop session. This method is a no-op; callers
    /// must convert absolute coordinates to relative deltas at a higher layer.
    /// </summary>
    public void NotifyPointerMotionAbsolute(double x, double y)
    {
        // Intentional no-op; see remarks above.
    }

    /// <summary>Injects a pointer button press or release.</summary>
    /// <param name="linuxButtonCode">Linux BTN_ code: 0x110=left, 0x111=right, 0x112=middle.</param>
    public void NotifyPointerButton(int linuxButtonCode, bool pressed)
    {
        if (!_active || _sessionHandle is null || _conn is null) return;

        try
        {
            var writer = _conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: PortalPath,
                @interface: RemoteDesktopInterface,
                member: "NotifyPointerButton",
                signature: "oa{sv}iu",
                flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(_sessionHandle);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart);
            writer.WriteInt32(linuxButtonCode);
            writer.WriteUInt32(pressed ? 1u : 0u);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyPointerButton failed.");
        }
    }

    /// <summary>Injects a discrete (tick-based) scroll event.</summary>
    /// <remarks>
    /// Maps to <c>NotifyPointerAxisDiscrete</c>. Axis 0 = vertical, 1 = horizontal.
    /// Each call emits one or both axes as separate messages because the portal
    /// API only supports one axis per call.
    /// </remarks>
    public void NotifyPointerScrollDiscrete(int dx, int dy)
    {
        if (!_active || _sessionHandle is null || _conn is null) return;
        if (dx == 0 && dy == 0) return;

        if (dy != 0) SendAxisDiscrete(axis: 0u, steps: dy);
        if (dx != 0) SendAxisDiscrete(axis: 1u, steps: dx);
    }

    private void SendAxisDiscrete(uint axis, int steps)
    {
        try
        {
            var writer = _conn!.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: PortalPath,
                @interface: RemoteDesktopInterface,
                member: "NotifyPointerAxisDiscrete",
                signature: "oa{sv}ui",
                flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(_sessionHandle!);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart);
            writer.WriteUInt32(axis);
            writer.WriteInt32(steps);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyPointerAxisDiscrete failed.");
        }
    }

    /// <summary>Injects a keyboard key press or release via Linux keycode.</summary>
    public void NotifyKeyboardKeycode(int keycode, bool pressed)
    {
        if (!_active || _sessionHandle is null || _conn is null) return;

        try
        {
            var writer = _conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: PortalPath,
                @interface: RemoteDesktopInterface,
                member: "NotifyKeyboardKeycode",
                signature: "oa{sv}iu",
                flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(_sessionHandle);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart);
            writer.WriteInt32(keycode);
            writer.WriteUInt32(pressed ? 1u : 0u);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyKeyboardKeycode failed.");
        }
    }

    /// <summary>Injects a keyboard keysym press or release.</summary>
    public void NotifyKeyboardKeysym(int keysym, bool pressed)
    {
        if (!_active || _sessionHandle is null || _conn is null) return;

        try
        {
            var writer = _conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: PortalPath,
                @interface: RemoteDesktopInterface,
                member: "NotifyKeyboardKeysym",
                signature: "oa{sv}iu",
                flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(_sessionHandle);
            var dictStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(dictStart);
            writer.WriteInt32(keysym);
            writer.WriteUInt32(pressed ? 1u : 0u);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyKeyboardKeysym failed.");
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _active = false;
        await CloseSessionInternalAsync();
        _conn?.Dispose();
        _conn = null;
    }

    private async Task CloseSessionInternalAsync()
    {
        if (_sessionHandle is null || _conn is null)
        {
            _sessionHandle = null;
            return;
        }

        try
        {
            var writer = _conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: _sessionHandle,
                @interface: SessionInterface,
                member: "Close",
                flags: MessageFlags.NoReplyExpected);
            var buf = writer.CreateMessage();
            _conn.TrySendMessage(buf);
            await Task.Yield();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing portal session failed (best effort).");
        }
        finally
        {
            _sessionHandle = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Wraps the RemoteDesktop <c>CreateSession</c> portal call with one-shot
    /// stale-frontend recovery. On the first <c>UnknownMethod</c>/<c>ServiceUnknown</c>/<c>UnknownInterface</c>
    /// error per process, restarts the portal frontend via
    /// <see cref="Portal.PortalRecoveryHelper"/> and retries once. On any other
    /// outcome, returns the original result (or null).
    /// </summary>
    private async Task<Dictionary<string, VariantValue>?> CreateSessionWithRecoveryAsync(
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await PortalDbusHelper.CallPortalAsync(
                    _conn!,
                    _normalizedSender!,
                    RemoteDesktopInterface,
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
                        "Input injector: RemoteDesktop portal interface missing. " +
                        "Attempting one-shot recovery (restart xdg-desktop-portal)...");
                    var recovered = await PortalRecoveryHelper
                        .TryRecoverAsync(_logger, ct);
                    if (recovered)
                    {
                        // D-Bus routes by well-known name, so the existing
                        // connection will reach the new portal frontend; no need
                        // to rebind.
                        continue;
                    }
                }
                _logger.LogWarning(ex, "Input injector: RemoteDesktop interface unavailable.");
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
            _logger.LogInformation(
                "No D-Bus session address available (DBUS_SESSION_BUS_ADDRESS unset); " +
                "portal input injector cannot start.");
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
                _logger.LogWarning("D-Bus connection has no unique name; portal session cannot proceed.");
                return false;
            }

            _normalizedSender = PortalDbusHelper.NormalizeSender(unique);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to D-Bus session bus.");
            return false;
        }
    }

}
