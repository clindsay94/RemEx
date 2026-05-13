using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Remex.Host.Services.Input.Linux;

/// <summary>
/// Injects pointer and keyboard events via the xdg-desktop-portal RemoteDesktop D-Bus API.
///
/// This is the correct input injection path for Wayland sessions where neither xdotool
/// (X11-only) nor ydotool (requires daemon) is reliable.  A RemoteDesktop-only portal
/// session is created on first use; the user is prompted once to grant pointer/keyboard
/// control, and the session persists for the lifetime of this injector instance.
///
/// Thread safety: <see cref="EnsureStartedAsync"/> serialises concurrent callers;
/// all Notify* methods are safe to call from any thread after the session is active.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPortalInputInjector : IAsyncDisposable
{
    private readonly ILogger _logger;
    private string? _sessionHandle;
    private volatile bool _active;
    private int _startInProgress;   // 0 = idle, 1 = in-progress (Interlocked flag)

    public bool IsActive => _active;

    public LinuxPortalInputInjector(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    // ── Session lifecycle ─────────────────────────────────────────────────

    /// <summary>
    /// Ensures the portal input session is active.  Safe to call concurrently;
    /// only one attempt proceeds at a time.  Returns true if the session is active
    /// after the call.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (_active) return true;

        // Guard: only one start attempt in flight.
        if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
        {
            // Another thread is already starting; wait briefly and check.
            for (int i = 0; i < 20 && !_active; i++)
                await Task.Delay(100, ct);
            return _active;
        }

        try
        {
            return await StartSessionAsync(ct);
        }
        finally
        {
            Interlocked.Exchange(ref _startInProgress, 0);
        }
    }

    // ── Input injection ───────────────────────────────────────────────────

    /// <summary>Injects a relative pointer motion event (trackpad-style).</summary>
    public void NotifyPointerMotionRelative(double dx, double dy)
    {
        if (!_active || _sessionHandle is null) return;

        // gdbus call format:
        //   gdbus call --session --dest org.freedesktop.portal.Desktop
        //     --object-path /org/freedesktop/portal/desktop
        //     --method org.freedesktop.portal.RemoteDesktop.NotifyPointerMotionRelative
        //     '<session_handle>' '{}' <dx> <dy>
        RunGdbusFireAndForget(
            "org.freedesktop.portal.RemoteDesktop",
            "NotifyPointerMotionRelative",
            $"'{_sessionHandle}'", "'{}'",
            FormatDouble(dx), FormatDouble(dy));
    }

    /// <summary>Injects an absolute pointer motion event.</summary>
    public void NotifyPointerMotionAbsolute(double x, double y)
    {
        if (!_active || _sessionHandle is null) return;

        RunGdbusFireAndForget(
            "org.freedesktop.portal.RemoteDesktop",
            "NotifyPointerMotionAbsolute",
            $"'{_sessionHandle}'", "'{}'",
            FormatDouble(x), FormatDouble(y));
    }

    /// <summary>Injects a pointer button press or release.</summary>
    /// <param name="linuxButtonCode">Linux BTN_ code: 272=left, 273=right, 274=middle.</param>
    /// <param name="pressed">True for press, false for release.</param>
    public void NotifyPointerButton(int linuxButtonCode, bool pressed)
    {
        if (!_active || _sessionHandle is null) return;

        // Signature: (o, a{sv}, i, u)  — session, options, button (int32), state (uint32)
        RunGdbusFireAndForget(
            "org.freedesktop.portal.RemoteDesktop",
            "NotifyPointerButton",
            $"'{_sessionHandle}'", "'{}'",
            linuxButtonCode.ToString(),
            pressed ? "1" : "0");
    }

    /// <summary>Injects a discrete (tick-based) scroll event.</summary>
    public void NotifyPointerScrollDiscrete(int dx, int dy)
    {
        if (!_active || _sessionHandle is null) return;

        RunGdbusFireAndForget(
            "org.freedesktop.portal.RemoteDesktop",
            "NotifyPointerScrollDiscrete",
            $"'{_sessionHandle}'", "'{}'",
            dx.ToString(), dy.ToString());
    }

    /// <summary>Injects a keyboard key press or release via Linux keycode.</summary>
    public void NotifyKeyboardKeycode(int keycode, bool pressed)
    {
        if (!_active || _sessionHandle is null) return;

        RunGdbusFireAndForget(
            "org.freedesktop.portal.RemoteDesktop",
            "NotifyKeyboardKeycode",
            $"'{_sessionHandle}'", "'{}'",
            keycode.ToString(),
            pressed ? "1" : "0");
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _active = false;
        if (_sessionHandle is not null)
        {
            try
            {
                await RunGdbusAsync(
                    "org.freedesktop.portal.Session",
                    "Close",
                    sessionOverride: _sessionHandle,
                    timeout: TimeSpan.FromSeconds(3));
            }
            catch { /* best effort */ }
            _sessionHandle = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation(
                "Starting xdg-desktop-portal RemoteDesktop input session on Wayland.");

            // ── Step 1: CreateSession ──────────────────────────────────────
            var token = $"remex_{Environment.ProcessId:X}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var createOutput = await RunGdbusAsync(
                "org.freedesktop.portal.RemoteDesktop",
                "CreateSession",
                args: $"\"{{ 'session-handle-token': <'{token}'> }}\"",
                timeout: TimeSpan.FromSeconds(10),
                ct: ct);

            var sessionHandle = ParseSessionHandle(createOutput);
            if (sessionHandle is null)
            {
                _logger.LogWarning(
                    "Portal CreateSession returned no session handle. " +
                    "Output: {Output}", createOutput);
                return false;
            }

            _logger.LogDebug("Portal session handle: {Handle}", sessionHandle);

            // ── Step 2: SelectDevices (Pointer | Keyboard = 3) ─────────────
            await RunGdbusAsync(
                "org.freedesktop.portal.RemoteDesktop",
                "SelectDevices",
                args: $"'{sessionHandle}' \"{{ 'types': <uint32 3> }}\"",
                timeout: TimeSpan.FromSeconds(10),
                ct: ct);

            // ── Step 3: Start — shows user dialog (up to 60 s) ────────────
            _logger.LogInformation(
                "Waiting for user to grant portal RemoteDesktop permission...");

            var startOutput = await RunGdbusAsync(
                "org.freedesktop.portal.RemoteDesktop",
                "Start",
                args: $"'{sessionHandle}' '' \"{{}}\"",
                timeout: TimeSpan.FromSeconds(60),
                ct: ct);

            // Start returns a Response; on success the response_code is 0.
            // If the user denied or the dialog timed out, log and fail.
            if (startOutput is not null && startOutput.Contains("response_code") &&
                !startOutput.Contains("response_code: 0") &&
                !startOutput.Contains("'response': <uint32 0>"))
            {
                _logger.LogWarning(
                    "User declined portal RemoteDesktop permission or dialog timed out. " +
                    "Output: {Output}", startOutput);
                return false;
            }

            _sessionHandle = sessionHandle;
            _active = true;
            _logger.LogInformation("Portal RemoteDesktop input session is active.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Portal input session start cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Portal input session start failed.");
            return false;
        }
    }

    private static string? ParseSessionHandle(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // gdbus output for CreateSession:
        //   (objectpath '/org/freedesktop/portal/desktop/session/...',)
        // busctl output: /org/freedesktop/portal/desktop/session/...
        const string prefix = "/org/freedesktop/portal/desktop/session/";
        int idx = output.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;

        int end = idx;
        while (end < output.Length &&
               output[end] != '\'' &&
               output[end] != '"' &&
               output[end] != ',' &&
               output[end] != ')' &&
               output[end] != ' ' &&
               output[end] != '\n')
        {
            end++;
        }
        var handle = output[idx..end].Trim();
        return string.IsNullOrWhiteSpace(handle) ? null : handle;
    }

    private async Task<string?> RunGdbusAsync(
        string iface,
        string method,
        string? args = null,
        string? sessionOverride = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var objPath = sessionOverride is not null
            ? $"'{sessionOverride}'"
            : "/org/freedesktop/portal/desktop";

        var argString = args is null ? string.Empty : $" {args}";
        var cmdArgs =
            $"call --session " +
            $"--dest org.freedesktop.portal.Desktop " +
            $"--object-path {objPath} " +
            $"--method {iface}.{method}" +
            argString;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout.HasValue)
                cts.CancelAfter(timeout.Value);

            var psi = new ProcessStartInfo("gdbus", cmdArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "gdbus {Method} failed.", method);
            return null;
        }
    }

    /// <summary>Fire-and-forget gdbus call for high-frequency input events.</summary>
    private void RunGdbusFireAndForget(string iface, string method, params string[] argValues)
    {
        var argString = argValues.Length > 0 ? " " + string.Join(" ", argValues) : string.Empty;
        var cmdArgs =
            $"call --session " +
            $"--dest org.freedesktop.portal.Desktop " +
            $"--object-path /org/freedesktop/portal/desktop " +
            $"--method {iface}.{method}" +
            argString;

        try
        {
            var psi = new ProcessStartInfo("gdbus", cmdArgs)
            {
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            // Do not wait — fire and forget. proc lifetime is OS-managed.
        }
        catch { /* best effort — silently ignored for high-frequency path */ }
    }

    /// <summary>Formats a double to 6 significant figures without culture-specific separators.</summary>
    private static string FormatDouble(double v)
        => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
}
