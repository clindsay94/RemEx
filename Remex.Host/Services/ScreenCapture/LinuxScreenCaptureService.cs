using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.ScreenCapture;

[SupportedOSPlatform("linux")]
public class LinuxScreenCaptureService : IScreenCaptureService
{
    private static readonly Regex XrandrGeometryRegex = new(
        @"^(?<width>\d+)x(?<height>\d+)(?<x>[+-]\d+)(?<y>[+-]\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<LinuxScreenCaptureService> _logger;
    private int _screenWidth;
    private int _screenHeight;
    // Primary monitor geometry — used to crop multi-monitor captures
    private int _primaryX;
    private int _primaryY;
    private int _primaryWidth;
    private int _primaryHeight;

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

    public (int Width, int Height, int Left, int Top) GetScreenSize() =>
        _primaryWidth > 0 
            ? (_primaryWidth, _primaryHeight, _primaryX, _primaryY) 
            : (_screenWidth, _screenHeight, 0, 0);

    private async Task<int> CaptureWaylandAsync(string tool, string tmpFile, int quality,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        var toolName = Path.GetFileName(tool);

        if (toolName == "spectacle")
            return await CaptureWithSpectacleAsync(tool, tmpFile, quality, captureWidth, captureHeight, ct);

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

    private async Task<int> CaptureWithSpectacleAsync(string tool, string tmpFile, int quality,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        // spectacle -b (background) -n (no notification) -o (output file)
        // Capture to PNG first, then convert with ffmpeg for quality/scale control.
        var pngFile = tmpFile + ".png";
        try
        {
            var env = new Dictionary<string, string>();
            foreach (var key in new[] { "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "XDG_CURRENT_DESKTOP", "KDE_FULL_SESSION" })
            {
                var val = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(val)) env[key] = val;
            }

            // --cursor/-p includes the OS cursor in the capture (KDE Plasma 6 supports this).
            var result = await RunProcessAsync(tool, $"-b -n -p -o \"{pngFile}\"", ct, env);
            if (result != 0 || !File.Exists(pngFile))
            {
                _logger.LogWarning("spectacle capture failed (exit={Code}). Is spectacle installed?", result);
                return result;
            }

            // Crop to primary monitor first, then scale. If no primary detected, scale the full capture.
            string vf;
            if (_primaryWidth > 0)
                vf = $"crop={_primaryWidth}:{_primaryHeight}:{_primaryX}:{_primaryY},scale={captureWidth}:{captureHeight}";
            else
                vf = $"scale={captureWidth}:{captureHeight}";

            var ffmpegArgs = $"-i \"{pngFile}\" -vf \"{vf}\" -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{tmpFile}\"";
            return await RunProcessAsync("ffmpeg", ffmpegArgs, ct);
        }
        finally
        {
            try { if (File.Exists(pngFile)) File.Delete(pngFile); } catch { /* best effort */ }
        }
    }

    private async Task<int> CaptureX11Async(string tool, string tmpFile, int quality, double scale,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        var toolName = Path.GetFileName(tool);
        if (toolName == "scrot")
        {
            var env = new Dictionary<string, string> { ["DISPLAY"] = _display };
            // -q sets JPEG quality. -z would suppress the cursor, so we omit it
            // to include the OS cursor in the captured frame.
            var args = $"-q {quality} \"{tmpFile}\"";
            var result = await RunProcessAsync(tool, args, ct, env);
            if (result != 0 || scale >= 0.99) return result;

            // Post-process with ffmpeg to scale down if needed
            var scaledFile = tmpFile + ".scaled.jpg";
            try
            {
                var ffmpegArgs = $"-i \"{tmpFile}\" -vf scale={captureWidth}:{captureHeight} -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{scaledFile}\"";
                var scaleResult = await RunProcessAsync("ffmpeg", ffmpegArgs, ct, env);
                if (scaleResult == 0 && File.Exists(scaledFile))
                {
                    File.Move(scaledFile, tmpFile, overwrite: true);
                }
                return scaleResult;
            }
            finally
            {
                try { if (File.Exists(scaledFile)) File.Delete(scaledFile); } catch { /* best effort */ }
            }
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
            // On KDE Plasma, grim requires zwlr_screencopy_manager_v1 which KDE does not implement.
            // Prefer spectacle on KDE; fall back to grim for wlroots compositors (Hyprland, Sway).
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            var isKde = desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

            if (isKde)
                primary = FindExecutable("spectacle") ?? FindExecutable("grim");
            else
                primary = FindExecutable("grim") ?? FindExecutable("spectacle");

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
                "No screen capture tools found. Install spectacle or grim (Wayland), scrot (X11), or ffmpeg as a fallback.");
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

            var lines = SplitLines(output);
            if (TryGetVirtualDesktopBounds(lines, out var width, out var height, out var left, out var top))
            {
                _primaryX = left;
                _primaryY = top;
                _primaryWidth = width;
                _primaryHeight = height;
                _screenWidth = width;
                _screenHeight = height;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "xrandr screen-size detection failed.");
        }
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
        _primaryWidth = 0; // 0 = no primary detected, use full capture
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

    private static string[] SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool TryGetVirtualDesktopBounds(
        string[] lines,
        out int width,
        out int height,
        out int left,
        out int top)
    {
        width = 0;
        height = 0;
        left = 0;
        top = 0;

        int minX = 0;
        int minY = 0;
        bool foundOffset = false;

        foreach (var line in lines)
        {
            if (!line.StartsWith("Screen ", StringComparison.Ordinal) || !line.Contains("current ", StringComparison.Ordinal))
                continue;

            var currentIndex = line.IndexOf("current ", StringComparison.Ordinal);
            if (currentIndex < 0)
                continue;

            var dimsSection = line[(currentIndex + "current ".Length)..];
            var commaIndex = dimsSection.IndexOf(',');
            if (commaIndex >= 0)
                dimsSection = dimsSection[..commaIndex];

            var dims = dimsSection.Split(" x ", StringSplitOptions.TrimEntries);
            if (dims.Length != 2 ||
                !int.TryParse(dims[0], out width) ||
                !int.TryParse(dims[1], out height))
            {
                continue;
            }

            foreach (var innerLine in lines)
            {
                if (!TryParseXrandrGeometry(innerLine, out _, out _, out var x, out var y))
                    continue;

                minX = foundOffset ? Math.Min(minX, x) : x;
                minY = foundOffset ? Math.Min(minY, y) : y;
                foundOffset = true;
            }

            left = foundOffset ? minX : 0;
            top = foundOffset ? minY : 0;
            return true;
        }

        int maxX = 0;
        int maxY = 0;
        bool foundAny = false;

        foreach (var line in lines)
        {
            if (!TryParseXrandrGeometry(line, out var currentWidth, out var currentHeight, out var x, out var y))
                continue;

            if (!foundAny)
            {
                minX = x;
                minY = y;
                maxX = x + currentWidth;
                maxY = y + currentHeight;
                foundAny = true;
            }
            else
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + currentWidth);
                maxY = Math.Max(maxY, y + currentHeight);
            }
        }

        if (!foundAny)
            return false;

        left = minX;
        top = minY;
        width = maxX - minX;
        height = maxY - minY;
        return true;
    }

    private static bool TryParseXrandrGeometry(
        string line,
        out int width,
        out int height,
        out int x,
        out int y)
    {
        width = 0;
        height = 0;
        x = 0;
        y = 0;

        if (!line.Contains(" connected", StringComparison.Ordinal) || !line.Contains('x'))
            return false;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!part.Contains('x'))
                continue;

            var match = XrandrGeometryRegex.Match(part);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["width"].Value, out width) ||
                !int.TryParse(match.Groups["height"].Value, out height) ||
                !int.TryParse(match.Groups["x"].Value, out x) ||
                !int.TryParse(match.Groups["y"].Value, out y))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
