using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.Input;

[SupportedOSPlatform("linux")]
public class LinuxInputSimulationService : IInputSimulationService
{
    private readonly ILogger<LinuxInputSimulationService> _logger;

    private enum InputBackend { Xdotool, Ydotool, None }
    private readonly InputBackend _backend;
    private readonly string _toolPath;
    private readonly string? _display;

    public LinuxInputSimulationService(ILogger<LinuxInputSimulationService> logger)
    {
        _logger = logger;
        _display = Environment.GetEnvironmentVariable("DISPLAY");

        // Prefer xdotool on X11, ydotool on Wayland; cross-fallback if primary unavailable
        var isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                     || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

        if (isWayland)
        {
            var ydotoolPath = FindExecutable("ydotool");
            var xdotoolPath = FindExecutable("xdotool");
            if (ydotoolPath is not null) { _backend = InputBackend.Ydotool; _toolPath = ydotoolPath; }
            else if (xdotoolPath is not null) { _backend = InputBackend.Xdotool; _toolPath = xdotoolPath; }
            else { _backend = InputBackend.None; _toolPath = string.Empty; }
        }
        else
        {
            var xdotoolPath = FindExecutable("xdotool");
            var ydotoolPath = FindExecutable("ydotool");
            if (xdotoolPath is not null) { _backend = InputBackend.Xdotool; _toolPath = xdotoolPath; }
            else if (ydotoolPath is not null) { _backend = InputBackend.Ydotool; _toolPath = ydotoolPath; }
            else { _backend = InputBackend.None; _toolPath = string.Empty; }
        }

        _logger.LogInformation("Linux input backend: {Backend} ({Path})", _backend, _toolPath);
    }

    public (int X, int Y) GetCursorPosition()
    {
        // xdotool getmouselocation returns "x:123 y:456 screen:0 window:12345"
        if (_backend == InputBackend.Xdotool)
        {
            try
            {
                var result = RunToolWithOutput("getmouselocation", "--shell");
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
        // ydotool doesn't support querying cursor position
        return (0, 0);
    }

    public void MoveMouse(int x, int y)
    {
        if (_backend == InputBackend.Ydotool)
            RunTool("mousemove", "--absolute", x.ToString(), y.ToString());
        else
            RunTool("mousemove", x.ToString(), y.ToString());
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        if (_backend == InputBackend.Ydotool)
            RunTool("mousemove", dx.ToString(), dy.ToString());
        else
            RunTool("mousemove_relative", "--", dx.ToString(), dy.ToString());
    }

    public void MouseDown(int button)
    {
        if (_backend == InputBackend.Ydotool)
            RunTool("click", $"0x{MapButtonYdotool(button):X5}D");
        else
            RunTool("mousedown", MapButtonXdotool(button).ToString());
    }

    public void MouseUp(int button)
    {
        if (_backend == InputBackend.Ydotool)
            RunTool("click", $"0x{MapButtonYdotool(button):X5}U");
        else
            RunTool("mouseup", MapButtonXdotool(button).ToString());
    }

    public void MouseClick(int button)
    {
        if (_backend == InputBackend.Ydotool)
            RunTool("click", $"0x{MapButtonYdotool(button):X5}");
        else
            RunTool("click", MapButtonXdotool(button).ToString());
    }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (_backend == InputBackend.Ydotool)
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
        if (_backend == InputBackend.Ydotool)
            RunTool("key", $"{keyCode}:1"); // 1 = press
        else
            RunTool("keydown", keyCode.ToString());
    }

    public void KeyUp(int keyCode)
    {
        if (_backend == InputBackend.Ydotool)
            RunTool("key", $"{keyCode}:0"); // 0 = release
        else
            RunTool("keyup", keyCode.ToString());
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_backend == InputBackend.Ydotool)
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

    private void RunTool(params string[] arguments)
    {
        if (_backend == InputBackend.None)
        {
            _logger.LogWarning("No input simulation tool available (install xdotool or ydotool).");
            return;
        }

        try
        {
            var argList = new List<string>(arguments);
            var psi = new ProcessStartInfo(_toolPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in argList)
                psi.ArgumentList.Add(arg);

            // Set DISPLAY for xdotool
            if (_backend == InputBackend.Xdotool && !string.IsNullOrEmpty(_display))
                psi.Environment["DISPLAY"] = _display;

            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Backend} command failed: {Args}", _backend, string.Join(" ", arguments));
        }
    }

    private string RunToolWithOutput(params string[] arguments)
    {
        if (_backend == InputBackend.None)
            return string.Empty;

        try
        {
            var argList = new List<string>(arguments);
            var psi = new ProcessStartInfo(_toolPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in argList)
                psi.ArgumentList.Add(arg);

            // Set DISPLAY for xdotool
            if (_backend == InputBackend.Xdotool && !string.IsNullOrEmpty(_display))
                psi.Environment["DISPLAY"] = _display;

            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Backend} query command failed: {Args}", _backend, string.Join(" ", arguments));
            return string.Empty;
        }
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(path) ? path : null;
        }
        catch { return null; }
    }
}
