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
    private int _portalStartFired; // 0 = not started, 1 = start fired (Interlocked)

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
        bool isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                         && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        if (isWayland)
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
        // Portal path: absolute motion.
        if (_portalInjector is not null)
        {
            EnsurePortalStarted();
            if (_portalInjector.IsActive)
            {
                _portalInjector.NotifyPointerMotionAbsolute(x, y);
                return;
            }
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("mousemove", "--absolute", x.ToString(), y.ToString());
        else
            RunTool("mousemove", x.ToString(), y.ToString());
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        // Portal path: relative motion works on Wayland without needing xdotool.
        if (_portalInjector is not null)
        {
            EnsurePortalStarted();
            if (_portalInjector.IsActive)
            {
                _portalInjector.NotifyPointerMotionRelative(dx, dy);
                return;
            }
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("mousemove", dx.ToString(), dy.ToString());
        else
            RunTool("mousemove_relative", "--", dx.ToString(), dy.ToString());
    }

    public void MouseDown(int button)
    {
        if (_portalInjector?.IsActive == true)
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
        if (_portalInjector?.IsActive == true)
        {
            _portalInjector.NotifyPointerButton(MapButtonLinux(button), pressed: false);
            return;
        }
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("click", $"0x{MapButtonYdotool(button):X5}U");
        else
            RunTool("mouseup", MapButtonXdotool(button).ToString());
    }

    public void MouseClick(int button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (_portalInjector?.IsActive == true)
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
    /// Fires a background task to start the portal input session if not already started.
    /// The session start prompts the user once; subsequent calls are no-ops.
    /// </summary>
    private void EnsurePortalStarted()
    {
        if (_portalInjector is null || _portalInjector.IsActive) return;
        if (Interlocked.CompareExchange(ref _portalStartFired, 1, 0) != 0) return;

        // Fire-and-forget; the injector serialises concurrent start attempts internally.
        _ = _portalInjector.EnsureStartedAsync();
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
