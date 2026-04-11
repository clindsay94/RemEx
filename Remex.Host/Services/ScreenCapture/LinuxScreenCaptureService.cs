using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.ScreenCapture;

[SupportedOSPlatform("linux")]
public class LinuxScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<LinuxScreenCaptureService> _logger;
    private int _screenWidth;
    private int _screenHeight;

    private enum DisplayServer { Unknown, X11, Wayland }
    private readonly DisplayServer _displayServer;
    private readonly string _display; // $DISPLAY for X11, $WAYLAND_DISPLAY for Wayland
    private readonly string? _captureToolPath;
    private readonly string? _fallbackToolPath;

    public LinuxScreenCaptureService(ILogger<LinuxScreenCaptureService> logger)
    {
        _logger = logger;
        _displayServer = DetectDisplayServer();
        _display = _displayServer switch
        {
            DisplayServer.Wayland => Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0",
            DisplayServer.X11 => Environment.GetEnvironmentVariable("DISPLAY") ?? ":0",
            _ => ":0"
        };

        (_captureToolPath, _fallbackToolPath) = DetectCaptureTools();
        DetectScreenSize();

        _logger.LogInformation(
            "Linux screen capture initialized: display={Display}, server={Server}, " +
            "resolution={W}x{H}, primaryTool={Tool}, fallback={Fallback}",
            _display, _displayServer, _screenWidth, _screenHeight,
            _captureToolPath ?? "none", _fallbackToolPath ?? "none");
    }

    public async Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"remex_capture_{Guid.NewGuid():N}.jpg");
        try
        {
            int captureWidth = (int)(_screenWidth * scale);
            int captureHeight = (int)(_screenHeight * scale);
            int result = -1;

            // Strategy 1: Use detected primary tool
            if (_captureToolPath is not null)
            {
                result = _displayServer switch
                {
                    DisplayServer.Wayland => await CaptureWaylandAsync(_captureToolPath, tmpFile, quality, captureWidth, captureHeight, ct),
                    _ => await CaptureX11Async(_captureToolPath, tmpFile, quality, scale, captureWidth, captureHeight, ct)
                };
            }

            // Strategy 2: Use fallback tool
            if (result != 0 && _fallbackToolPath is not null)
            {
                result = await CaptureWithFfmpegAsync(tmpFile, captureWidth, captureHeight, quality, ct);
            }

            // Strategy 3: Last resort — try gnome-screenshot (works on both X11 and Wayland under GNOME)
            if (result != 0)
            {
                result = await RunProcessAsync("gnome-screenshot", $"-f \"{tmpFile}\"", ct);
            }

            if (result != 0 || !File.Exists(tmpFile))
            {
                _logger.LogWarning("Screen capture failed on Linux (display={Display}, server={Server}).",
                    _display, _displayServer);
                return Array.Empty<byte>();
            }

            return await File.ReadAllBytesAsync(tmpFile, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture screen on Linux.");
            return Array.Empty<byte>();
        }
        finally
        {
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    public (int Width, int Height) GetScreenSize() => (_screenWidth, _screenHeight);

    private async Task<int> CaptureWaylandAsync(string tool, string tmpFile, int quality,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        var toolName = Path.GetFileName(tool);
        if (toolName == "grim")
        {
            // grim outputs PNG; use ffmpeg to convert to JPEG with quality/scale
            var pngFile = tmpFile + ".png";
            try
            {
                var grimResult = await RunProcessAsync(tool, $"\"{pngFile}\"", ct);
                if (grimResult != 0 || !File.Exists(pngFile))
                    return grimResult;

                // Convert PNG to JPEG with quality and scale
                var ffmpegArgs = $"-i \"{pngFile}\" -vf scale={captureWidth}:{captureHeight} -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{tmpFile}\"";
                return await RunProcessAsync("ffmpeg", ffmpegArgs, ct);
            }
            finally
            {
                try { if (File.Exists(pngFile)) File.Delete(pngFile); } catch { /* best effort */ }
            }
        }

        // Generic Wayland screenshot tool fallback
        return await RunProcessAsync(tool, $"\"{tmpFile}\"", ct);
    }

    private async Task<int> CaptureX11Async(string tool, string tmpFile, int quality, double scale,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        var toolName = Path.GetFileName(tool);
        if (toolName == "scrot")
        {
            var env = new Dictionary<string, string> { ["DISPLAY"] = _display };
            // scrot -z suppresses cursor, -q sets JPEG quality. 
            // Capture at full resolution and let ffmpeg handle scaling if needed.
            var args = $"-z -q {quality} \"{tmpFile}\"";
            var result = await RunProcessAsync(tool, args, ct, env);
            if (result != 0 || scale >= 0.99) return result;

            // Post-process with ffmpeg to scale down if needed
            var scaledFile = tmpFile + ".scaled.jpg";
            var ffmpegArgs = $"-i \"{tmpFile}\" -vf scale={captureWidth}:{captureHeight} -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{scaledFile}\"";
            var scaleResult = await RunProcessAsync("ffmpeg", ffmpegArgs, ct, env);
            if (scaleResult == 0 && File.Exists(scaledFile))
            {
                File.Move(scaledFile, tmpFile, overwrite: true);
            }
            return scaleResult;
        }

        // import (ImageMagick) fallback
        if (toolName == "import")
        {
            var env = new Dictionary<string, string> { ["DISPLAY"] = _display };
            var args = $"-window root -quality {quality} -resize {captureWidth}x{captureHeight} \"{tmpFile}\"";
            return await RunProcessAsync(tool, args, ct, env);
        }

        return -1;
    }

    private async Task<int> CaptureWithFfmpegAsync(string tmpFile, int captureWidth, int captureHeight,
        int quality, CancellationToken ct)
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var env = new Dictionary<string, string> { ["DISPLAY"] = display };
        var args = $"-f x11grab -video_size {_screenWidth}x{_screenHeight} -i {display} " +
                   $"-frames:v 1 -q:v {Math.Max(1, 31 - quality * 31 / 100)} " +
                   $"-vf scale={captureWidth}:{captureHeight} -y \"{tmpFile}\"";
        return await RunProcessAsync("ffmpeg", args, ct, env);
    }

    private static DisplayServer DetectDisplayServer()
    {
        // Check WAYLAND_DISPLAY first (if set, the session is Wayland)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return DisplayServer.Wayland;

        // XDG_SESSION_TYPE is set by most modern display managers
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            return DisplayServer.Wayland;
        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase))
            return DisplayServer.X11;

        // Fallback: if DISPLAY is set, assume X11
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return DisplayServer.X11;

        return DisplayServer.Unknown;
    }

    private (string? primary, string? fallback) DetectCaptureTools()
    {
        string? primary = null;
        string? fallback = null;

        if (_displayServer == DisplayServer.Wayland)
        {
            primary = FindExecutable("grim");
            fallback = FindExecutable("ffmpeg");
        }
        else
        {
            primary = FindExecutable("scrot") ?? FindExecutable("import");
            fallback = FindExecutable("ffmpeg");
        }

        if (primary is null && fallback is null)
        {
            _logger.LogWarning(
                "No screen capture tools found. Install scrot or grim (Wayland), or ffmpeg as a fallback.");
        }

        return (primary, fallback);
    }

    private void DetectScreenSize()
    {
        // Try xrandr first (works on both X11 and XWayland)
        if (TryDetectWithXrandr()) return;

        // Try xdpyinfo (X11 only)
        if (_displayServer != DisplayServer.Wayland && TryDetectWithXdpyinfo()) return;

        // Try wlr-randr (Wayland with wlroots compositors)
        if (_displayServer == DisplayServer.Wayland && TryDetectWithWlrRandr()) return;

        SetDefaultSize();
    }

    private bool TryDetectWithXrandr()
    {
        try
        {
            var env = new Dictionary<string, string>();
            var displayVar = Environment.GetEnvironmentVariable("DISPLAY");
            if (!string.IsNullOrEmpty(displayVar))
                env["DISPLAY"] = displayVar;

            var psi = new ProcessStartInfo("xrandr", "--current")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Parse "Screen 0: ... current 1920 x 1080" or connected output lines with resolution
            foreach (var line in output.Split('\n'))
            {
                // Look for primary/connected output with resolution
                if (line.Contains(" connected") && line.Contains('x'))
                {
                    // Format: "DP-1 connected primary 1920x1080+0+0 ..."
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (part.Contains('x') && part.Contains('+'))
                        {
                            var res = part.Split('+')[0].Split('x');
                            if (res.Length == 2 && int.TryParse(res[0], out int w) && int.TryParse(res[1], out int h))
                            {
                                _screenWidth = w;
                                _screenHeight = h;
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private bool TryDetectWithXdpyinfo()
    {
        try
        {
            var psi = new ProcessStartInfo("xdpyinfo")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var displayVar = Environment.GetEnvironmentVariable("DISPLAY");
            if (!string.IsNullOrEmpty(displayVar))
                psi.Environment["DISPLAY"] = displayVar;

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("dimensions:"))
                {
                    var parts = line.Split(':')[1].Trim().Split(' ')[0].Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        _screenWidth = w;
                        _screenHeight = h;
                        return true;
                    }
                }
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private bool TryDetectWithWlrRandr()
    {
        try
        {
            var psi = new ProcessStartInfo("wlr-randr")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Parse "current: 1920x1080"
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("current:"))
                {
                    var resPart = trimmed.Split("current:")[1].Trim().Split(' ')[0];
                    var res = resPart.Split('x');
                    if (res.Length == 2 && int.TryParse(res[0], out int w) && int.TryParse(res[1], out int h))
                    {
                        _screenWidth = w;
                        _screenHeight = h;
                        return true;
                    }
                }
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private void SetDefaultSize()
    {
        _screenWidth = 1920;
        _screenHeight = 1080;
        _logger.LogWarning("Could not detect screen size, defaulting to {W}x{H}.", _screenWidth, _screenHeight);
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

    private static async Task<int> RunProcessAsync(string fileName, string arguments,
        CancellationToken ct, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env is not null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(proc.WaitForExitAsync(ct), stdOutTask, stdErrTask);
            return proc.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
