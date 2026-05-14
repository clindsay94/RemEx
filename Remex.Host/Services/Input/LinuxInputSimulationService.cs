using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Host.Services.Input.Linux;
using Remex.Host.Services.RemoteDesktop.Linux;

namespace Remex.Host.Services.Input;

[SupportedOSPlatform("linux")]
public class LinuxInputSimulationService : IInputSimulationService
{
    private readonly ILogger<LinuxInputSimulationService> _logger;
    private readonly LinuxDesktopBackendStatus _backendStatus;
    private readonly string? _display;

    // Stage 5: router is set when a WaylandNative/PortalNoPen session is active.
    // When null, the legacy xdotool/ydotool path runs as before.
    private LinuxInputBackendRouter? _router;

    // Portal-based Wayland input injector.  Created on Wayland when the portal is
    // available; null on X11 or when portal is absent.  Started lazily on first use
    // so that the permission dialog is shown only when a remote desktop session
    // actually begins sending input events.
    private readonly LinuxPortalInputInjector? _portalInjector;
    private int _portalStartAttempted; // 0 = not yet attempted, 1 = attempted (Interlocked)
    private readonly object _portalStartLock = new();

    // Virtual cursor tracking for absolute → relative conversion.
    // The portal RemoteDesktop API's NotifyPointerMotionAbsolute requires a PipeWire
    // stream id from a unified ScreenCast session; without that, only relative motion
    // works. We treat the first received absolute target as the cursor baseline and
    // dispatch every subsequent absolute target as a delta from the prior one.
    private double _lastVirtualX;
    private double _lastVirtualY;
    private bool _haveVirtualPosition;

    public string? BackendName
    {
        get
        {
            if (_router is not null) return _router.BackendName;
            if (_portalInjector?.IsActive == true) return "portal-notify";
            return _backendStatus.InputBackendName;
        }
    }

    public LinuxInputSimulationService(ILogger<LinuxInputSimulationService> logger)
    {
        _logger = logger;
        _display = Environment.GetEnvironmentVariable("DISPLAY");
        _backendStatus = LinuxDesktopBackendProbe.Probe();

        // On Wayland, create a portal input injector so that pointer events work even
        // when xdotool / ydotool are not available or cannot inject into the compositor.
        // Use the backend probe's detection (which considers XDG_SESSION_TYPE) rather
        // than a naive WAYLAND_DISPLAY-only check: KDE/GNOME Wayland sessions always
        // also set DISPLAY=:0 for XWayland, so requiring DISPLAY to be empty hides
        // the Wayland path on every real Wayland desktop.
        if (_backendStatus.IsWaylandSession)
        {
            _portalInjector = new LinuxPortalInputInjector(logger);
            _logger.LogInformation(
                "Wayland session detected — portal input injector created (will prompt for " +
                "permission on first input event).");
        }

        _logger.LogInformation(
            "Linux input backend: {InputBackend} ({InputPath}); cursor query backend: {CursorBackend} ({CursorPath})",
            _backendStatus.InputBackendName ?? "none",
            _backendStatus.InputToolPath ?? "n/a",
            _backendStatus.CursorQueryBackendName ?? "none",
            _backendStatus.CursorQueryToolPath ?? "n/a");
    }

    /// <summary>
    /// Sets the backend router for WaylandNative / PortalNoPen tiers.
    /// When set, all input events are routed through the router rather than shell tools.
    /// Pass null to revert to the legacy shell-tool path.
    /// </summary>
    public void SetRouter(LinuxInputBackendRouter? router)
    {
        _router = router;
    }

    /// <summary>
    /// Routes a pointer sample from the Android client.
    /// Pen events are forwarded to the uinput tablet; regular events go through
    /// the router or legacy path.
    /// </summary>
    public void EnqueuePointerSample(DesktopPointerSample sample)
    {
        if (_router is not null)
        {
            _router.EnqueuePointerSample(sample);
            return;
        }
        // Legacy path: convert to absolute mouse move
        MoveMouse((int)sample.LogicalX, (int)sample.LogicalY);
    }

    public (int X, int Y) GetCursorPosition()
    {
        if (_backendStatus.CursorQueryTool is LinuxDesktopTool.Kdotool or LinuxDesktopTool.Xdotool &&
            _backendStatus.CursorQueryToolPath is not null)
        {
            try
            {
                var result = RunToolWithOutput(_backendStatus.CursorQueryTool, _backendStatus.CursorQueryToolPath, "getmouselocation", "--shell");
                // Output format: X=123\nY=456\nSCREEN=0\nWINDOW=12345
                var lines = result.Split('\n');
                var x = 0;
                var y = 0;
                foreach (var line in lines)
                {
                    if (line.StartsWith("X=")) int.TryParse(line.Substring(2), out x);
                    if (line.StartsWith("Y=")) int.TryParse(line.Substring(2), out y);
                }
                return (x, y);
            }
            catch { return (0, 0); }
        }

        return (0, 0);
    }

    public void MoveMouse(int x, int y)
    {
        // Portal path: input-only sessions cannot dispatch absolute motion (the portal
        // requires a PipeWire stream from a unified ScreenCast session). We convert
        // each absolute target into a delta from the prior absolute target and emit
        // relative motion instead. The first event of every drag is treated as the
        // baseline — the cursor stays put until subsequent samples produce a delta.
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            EmitPortalRelativeFromAbsolute(x, y);
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("mousemove", "--absolute", x.ToString(), y.ToString());
        else
            RunTool("mousemove", x.ToString(), y.ToString());
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerMotionRelative(dx, dy);
            // Keep the virtual cursor in sync so absolute samples after a relative
            // move don't snap.
            if (_haveVirtualPosition)
            {
                _lastVirtualX += dx;
                _lastVirtualY += dy;
            }
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("mousemove", dx.ToString(), dy.ToString());
        else
            RunTool("mousemove_relative", "--", dx.ToString(), dy.ToString());
    }

    public void MouseDown(int button)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerButton(MapButtonLinux(button), pressed: true);
            return;
        }
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("click", $"0x{MapButtonYdotool(button):X5}D");
        else
            RunTool("mousedown", MapButtonXdotool(button).ToString());
    }

    public void MouseUp(int button)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerButton(MapButtonLinux(button), pressed: false);
            // Release ends a contact; the next absolute target should re-baseline
            // so the cursor does not jump when the user taps somewhere else.
            _haveVirtualPosition = false;
            return;
        }
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("click", $"0x{MapButtonYdotool(button):X5}U");
        else
            RunTool("mouseup", MapButtonXdotool(button).ToString());
    }

    /// <summary>
    /// Resets the virtual cursor baseline. Call when a new touch/pen contact starts
    /// so the cursor doesn't jump when the user lifts and re-places their finger.
    /// </summary>
    public void ResetVirtualCursor()
    {
        _haveVirtualPosition = false;
    }

    private void EmitPortalRelativeFromAbsolute(int x, int y)
    {
        if (_portalInjector is null) return;
        if (_haveVirtualPosition)
        {
            var dx = x - _lastVirtualX;
            var dy = y - _lastVirtualY;
            if (dx != 0 || dy != 0)
                _portalInjector.NotifyPointerMotionRelative(dx, dy);
        }
        _lastVirtualX = x;
        _lastVirtualY = y;
        _haveVirtualPosition = true;
    }

    public void MouseClick(int button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerScrollDiscrete(deltaX, deltaY);
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            // ydotool mousemove sends relative motion; wheel via separate abstraction not supported well
            // Use xdotool fallback pattern: scroll buttons
            if (deltaY != 0)
            {
                int clicks = Math.Clamp(Math.Abs(deltaY) / 120, 1, 10);
                int btn = deltaY > 0 ? 4 : 5;
                for (int i = 0; i < clicks; i++)
                    RunTool("click", $"0x{btn:X5}");
            }
            if (deltaX != 0)
            {
                int clicks = Math.Clamp(Math.Abs(deltaX) / 120, 1, 10);
                int btn = deltaX > 0 ? 7 : 6;
                for (int i = 0; i < clicks; i++)
                    RunTool("click", $"0x{btn:X5}");
            }
        }
        else
        {
            // xdotool: button 4=scroll up, 5=scroll down, 6=scroll left, 7=scroll right
            if (deltaY > 0)
                for (int i = 0; i < Math.Clamp(deltaY / 120, 1, 10); i++)
                    RunTool("click", "4");
            else if (deltaY < 0)
                for (int i = 0; i < Math.Clamp(-deltaY / 120, 1, 10); i++)
                    RunTool("click", "5");

            if (deltaX > 0)
                for (int i = 0; i < Math.Clamp(deltaX / 120, 1, 10); i++)
                    RunTool("click", "7");
            else if (deltaX < 0)
                for (int i = 0; i < Math.Clamp(-deltaX / 120, 1, 10); i++)
                    RunTool("click", "6");
        }
    }

    public void KeyDown(int keyCode)
    {
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("key", $"{keyCode}:1"); // 1 = press
        else
            RunTool("keydown", keyCode.ToString());
    }

    public void KeyUp(int keyCode)
    {
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("key", $"{keyCode}:0"); // 0 = release
        else
            RunTool("keyup", keyCode.ToString());
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            // ydotool type takes text directly, safe via argument array
            RunTool("type", "--", text);
        }
        else
        {
            // xdotool type: pass text via argument array to avoid shell injection
            RunTool("type", "--", text);
        }
    }

    private static int MapButtonXdotool(int button) => button switch
    {
        0 => 1, // left
        1 => 2, // middle
        2 => 3, // right
        _ => 1
    };

    private static int MapButtonYdotool(int button) => button switch
    {
        0 => 0x110, // BTN_LEFT
        1 => 0x112, // BTN_MIDDLE
        2 => 0x111, // BTN_RIGHT
        _ => 0x110
    };

    // Linux BTN_ codes used by the portal NotifyPointerButton API.
    private static int MapButtonLinux(int button) => button switch
    {
        0 => 0x110, // BTN_LEFT
        1 => 0x112, // BTN_MIDDLE
        2 => 0x111, // BTN_RIGHT
        _ => 0x110
    };

    /// <summary>
    /// Synchronously ensures the portal input session is active. The first caller blocks
    /// until the KDE permission dialog completes (up to 2 minutes); subsequent callers
    /// return immediately once the session is up. After a failed start we mark the
    /// attempt complete and fall through to the legacy shell path on the next event.
    /// </summary>
    /// <remarks>
    /// This runs on the input worker thread (no <see cref="SynchronizationContext"/>),
    /// so the sync-over-async <c>GetAwaiter().GetResult()</c> cannot deadlock.
    /// </remarks>
    /// <returns>True if the portal session is active.</returns>
    private bool EnsurePortalStarted()
    {
        if (_portalInjector is null) return false;
        if (_portalInjector.IsActive) return true;

        // Only one thread runs the start; others just observe IsActive afterwards.
        if (Interlocked.CompareExchange(ref _portalStartAttempted, 1, 0) == 0)
        {
            lock (_portalStartLock)
            {
                if (!_portalInjector.IsActive)
                {
                    try
                    {
                        _portalInjector.EnsureStartedAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Portal input session start raised an exception.");
                    }
                }
            }
        }
        else
        {
            // Another thread is starting; wait for it to publish IsActive (or give up).
            lock (_portalStartLock) { }
        }

        return _portalInjector.IsActive;
    }

    private void RunTool(params string[] arguments)
    {
        if (_backendStatus.InputTool == LinuxDesktopTool.None || _backendStatus.InputToolPath is null)
        {
            _logger.LogWarning("No input simulation tool available (install xdotool or ydotool).");
            return;
        }

        try
        {
            _ = RunToolWithOutput(_backendStatus.InputTool, _backendStatus.InputToolPath, arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Backend} command failed: {Args}", _backendStatus.InputBackendName ?? "none", string.Join(" ", arguments));
        }
    }

    private string RunToolWithOutput(LinuxDesktopTool backend, string toolPath, params string[] arguments)
    {
        var argList = new List<string>(arguments);
        var psi = new ProcessStartInfo(toolPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        if (backend == LinuxDesktopTool.Xdotool && !string.IsNullOrEmpty(_display))
            psi.Environment["DISPLAY"] = _display;

        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(2000);
        return output;
    }
}
