using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Host.Services.ScreenCapture;

[SupportedOSPlatform("linux")]
public class LinuxScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<LinuxScreenCaptureService> _logger;
    private int _screenWidth;
    private int _screenHeight;

    public LinuxScreenCaptureService(ILogger<LinuxScreenCaptureService> logger)
    {
        _logger = logger;
        DetectScreenSize();
    }

    public async Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, int monitorIndex = 0, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"remex_capture_{Guid.NewGuid():N}.jpg");
        try
        {
            int captureWidth = (int)(_screenWidth * scale);
            int captureHeight = (int)(_screenHeight * scale);

            // Try scrot first, fall back to grim (Wayland)
            var args = $"-z -q {quality} -t {(int)(scale * 100)} \"{tmpFile}\"";
            var result = await RunProcessAsync("scrot", args, ct);

            if (result != 0)
            {
                // Fallback: use ffmpeg to capture a single frame
                args = $"-f x11grab -video_size {_screenWidth}x{_screenHeight} -i :0 -frames:v 1 -q:v {Math.Max(1, 31 - quality * 31 / 100)} -vf scale={captureWidth}:{captureHeight} -y \"{tmpFile}\"";
                result = await RunProcessAsync("ffmpeg", args, ct);
            }

            if (result != 0 || !File.Exists(tmpFile))
            {
                _logger.LogWarning("Screen capture failed on Linux.");
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

    public (int Width, int Height) GetScreenSize(int monitorIndex = 0) => (_screenWidth, _screenHeight);

    public List<MonitorInfo> GetMonitors()
    {
        return new List<MonitorInfo>
        {
            new MonitorInfo
            {
                Index = 0,
                Name = "Primary",
                Left = 0,
                Top = 0,
                Width = _screenWidth,
                Height = _screenHeight,
                IsPrimary = true,
            }
        };
    }

    private void DetectScreenSize()
    {
        try
        {
            var psi = new ProcessStartInfo("xdpyinfo")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) { SetDefaultSize(); return; }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Parse "dimensions:    1920x1080 pixels"
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("dimensions:"))
                {
                    var parts = line.Split(':')[1].Trim().Split(' ')[0].Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        _screenWidth = w;
                        _screenHeight = h;
                        return;
                    }
                }
            }
        }
        catch { /* fall through */ }

        SetDefaultSize();
    }

    private void SetDefaultSize()
    {
        _screenWidth = 1920;
        _screenHeight = 1080;
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            // Drain both stdout and stderr to prevent deadlocks with verbose processes.
            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            await Task.WhenAll(proc.WaitForExitAsync(ct), stdOutTask, stdErrTask);
            return proc.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
