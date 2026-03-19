using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.Input;

[SupportedOSPlatform("linux")]
public class LinuxInputSimulationService : IInputSimulationService
{
    private readonly ILogger<LinuxInputSimulationService> _logger;

    public LinuxInputSimulationService(ILogger<LinuxInputSimulationService> logger)
    {
        _logger = logger;
    }

    public void MoveMouse(int x, int y)
    {
        RunXdotool($"mousemove {x} {y}");
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        RunXdotool($"mousemove_relative -- {dx} {dy}");
    }

    public void MouseDown(int button)
    {
        RunXdotool($"mousedown {MapButton(button)}");
    }

    public void MouseUp(int button)
    {
        RunXdotool($"mouseup {MapButton(button)}");
    }

    public void MouseClick(int button)
    {
        RunXdotool($"click {MapButton(button)}");
    }

    public void MouseScroll(int deltaX, int deltaY)
    {
        // xdotool: button 4=scroll up, 5=scroll down, 6=scroll left, 7=scroll right
        if (deltaY > 0)
            for (int i = 0; i < Math.Min(deltaY / 120, 10); i++)
                RunXdotool("click 4");
        else if (deltaY < 0)
            for (int i = 0; i < Math.Min(-deltaY / 120, 10); i++)
                RunXdotool("click 5");

        if (deltaX > 0)
            for (int i = 0; i < Math.Min(deltaX / 120, 10); i++)
                RunXdotool("click 7");
        else if (deltaX < 0)
            for (int i = 0; i < Math.Min(-deltaX / 120, 10); i++)
                RunXdotool("click 6");
    }

    public void KeyDown(int keyCode)
    {
        RunXdotool($"keydown {keyCode}");
    }

    public void KeyUp(int keyCode)
    {
        RunXdotool($"keyup {keyCode}");
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Sanitize: xdotool type expects the text directly
        RunXdotool($"type -- \"{text.Replace("\"", "\\\"")}\"");
    }

    private static int MapButton(int button) => button switch
    {
        0 => 1, // left
        1 => 2, // middle
        2 => 3, // right
        _ => 1
    };

    private void RunXdotool(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "xdotool command failed: {Args}", arguments);
        }
    }
}
