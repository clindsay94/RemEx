using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Remex.Host.Services.RemoteDesktop.Linux.Portal;

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
/// Manages the lifecycle of an xdg-desktop-portal RemoteDesktop + ScreenCast session.
///
/// Responsibilities:
///   - Open a combined RemoteDesktop + ScreenCast portal session via D-Bus
///   - Select sources (monitor, embedded cursor)
///   - Select devices (keyboard + pointer)
///   - Start the session and surface the PipeWire node IDs to the caller
///   - Handle session restart on monitor hotplug or stream identity change
///   - Close the session cleanly on dispose
///
/// D-Bus communication is performed via busctl subprocess calls for maximum portability
/// while the project evaluates adding Tmds.DBus.Protocol as a direct dependency.
/// Once that dependency is confirmed present in the host project, the implementation
/// can migrate to the native D-Bus client path without changing the public contract.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPortalRemoteDesktopSessionService : IAsyncDisposable
{
    private readonly ILogger<LinuxPortalRemoteDesktopSessionService> _logger;
    private readonly string _appId;

    private volatile PortalSessionState _state = PortalSessionState.Idle;
    private PortalStartResult? _lastResult;
    private string? _sessionHandle;

    // Fired when a new session is established and PipeWire node IDs are available.
    public event Action<PortalStartResult>? SessionStarted;

    // Fired when the portal session is lost and a restart has been initiated.
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
        _logger.LogInformation("Opening xdg-desktop-portal RemoteDesktop + ScreenCast session.");

        try
        {
            // Step 1 — Create a combined portal session.
            var sessionToken = $"remex_{Guid.NewGuid():N}";
            var sessionHandle = await CallPortalMethodAsync(
                "CreateSession",
                $"--session-token={sessionToken}",
                ct);

            if (sessionHandle is null)
            {
                _state = PortalSessionState.Failed;
                throw new InvalidOperationException(
                    "Portal CreateSession returned no session handle. " +
                    "Ensure xdg-desktop-portal and a backend (xdg-desktop-portal-kde) are running.");
            }

            _sessionHandle = sessionHandle;

            // Step 2 — Select devices (keyboard + pointer).
            await SelectDevicesAsync(sessionHandle, ct);

            // Step 3 — Select sources (monitor, embedded cursor).
            await SelectSourcesAsync(sessionHandle, ct);

            // Step 4 — Start the session.
            var nodeIds = await StartSessionCoreAsync(sessionHandle, ct);

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
    }

    // ── Portal D-Bus interaction ───────────────────────────────────────────

    private async Task SelectDevicesAsync(string sessionHandle, CancellationToken ct)
    {
        _logger.LogDebug("SelectDevices: keyboard + pointer.");

        // Device types: 1=Keyboard, 2=Pointer — combined = 3
        var args = BuildBusctlArgs(
            "org.freedesktop.portal.RemoteDesktop",
            "SelectDevices",
            $"--session-handle={sessionHandle}",
            "--types=3");   // Keyboard | Pointer

        await RunBusctlAsync(args, ct);
    }

    private async Task SelectSourcesAsync(string sessionHandle, CancellationToken ct)
    {
        _logger.LogDebug("SelectSources: Monitor, EmbeddedCursor.");

        // source_type=1 (Monitor), cursor_mode=2 (Embedded), persist=0 (none)
        var args = BuildBusctlArgs(
            "org.freedesktop.portal.ScreenCast",
            "SelectSources",
            $"--session-handle={sessionHandle}",
            "--types=1",       // Monitor
            "--cursor-mode=2", // Embedded
            "--persist-mode=0");

        await RunBusctlAsync(args, ct);
    }

    private async Task<IReadOnlyList<uint>> StartSessionCoreAsync(string sessionHandle, CancellationToken ct)
    {
        _logger.LogDebug("Starting portal session.");

        var args = BuildBusctlArgs(
            "org.freedesktop.portal.RemoteDesktop",
            "Start",
            $"--session-handle={sessionHandle}");

        var output = await RunBusctlAsync(args, ct);

        // Parse node IDs from busctl output. Format varies by portal version;
        // look for 'node_id' in the variant map returned by Start.
        var nodeIds = ParseNodeIds(output);

        if (nodeIds.Count == 0)
        {
            _logger.LogWarning(
                "Portal Start succeeded but returned no stream node IDs. " +
                "PipeWire capture will attempt to open fd 0 as fallback.");
        }

        return nodeIds;
    }

    private async Task CloseSessionCoreAsync()
    {
        if (_sessionHandle is null) return;

        try
        {
            _logger.LogDebug("Closing portal session {Handle}.", _sessionHandle);
            var args = $"--user call org.freedesktop.portal.Desktop " +
                       $"{_sessionHandle} org.freedesktop.portal.Session Close";
            await RunBusctlRawAsync(args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing portal session (best effort).");
        }
        finally
        {
            _sessionHandle = null;
        }
    }

    private static IReadOnlyList<uint> ParseNodeIds(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<uint>();

        var ids = new List<uint>();
        // busctl output for Start includes stream descriptors; look for uint32 node_id values.
        // This is a best-effort text parse — a proper D-Bus binding handles this cleanly.
        var span = output.AsSpan();
        const string marker = "node_id";
        int pos = 0;
        while ((pos = output.IndexOf(marker, pos, StringComparison.Ordinal)) >= 0)
        {
            pos += marker.Length;
            // Skip whitespace and look for a digit sequence
            while (pos < output.Length && !char.IsDigit(output[pos])) pos++;
            int start = pos;
            while (pos < output.Length && char.IsDigit(output[pos])) pos++;
            if (start < pos && uint.TryParse(output.AsSpan(start, pos - start), out var id))
                ids.Add(id);
        }
        return ids.AsReadOnly();
    }

    private async Task<string?> CallPortalMethodAsync(string method, string extraArgs, CancellationToken ct)
    {
        var args = BuildBusctlArgs("org.freedesktop.portal.RemoteDesktop", method, extraArgs);
        var output = await RunBusctlAsync(args, ct);

        // Extract session handle from output (string path returned by CreateSession)
        if (output is null) return null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("/org/freedesktop/portal/desktop/session/", StringComparison.Ordinal))
                return trimmed;
            if (trimmed.Contains("/session/"))
            {
                // Extract path segment
                var idx = trimmed.IndexOf("/org/freedesktop/portal/desktop/session/", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var path = trimmed[idx..].Split(' ')[0].TrimEnd('"', '\'');
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
            }
        }
        return null;
    }

    private static string BuildBusctlArgs(string iface, string method, params string[] extraArgs)
    {
        var extra = string.Join(" ", extraArgs);
        return $"--user call {PortalDbusNames.PortalService} {PortalDbusNames.PortalPath} {iface} {method} {extra}";
    }

    private async Task<string?> RunBusctlAsync(string args, CancellationToken ct)
        => await RunBusctlRawAsync(args, ct);

    private static async Task<string?> RunBusctlRawAsync(string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("busctl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // portal prompts can take time

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }
}
