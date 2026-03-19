using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Host.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
public class WindowsScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<WindowsScreenCaptureService> _logger;
    private readonly object _monitorLock = new();
    private List<MonitorInfo> _cachedMonitors = new();
    private DateTime _lastMonitorRefresh = DateTime.MinValue;
    private static readonly TimeSpan MonitorRefreshInterval = TimeSpan.FromSeconds(30);

    public WindowsScreenCaptureService(ILogger<WindowsScreenCaptureService> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, int monitorIndex = 0, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);

        try
        {
            var monitors = GetMonitors();
            var monitor = monitorIndex >= 0 && monitorIndex < monitors.Count
                ? monitors[monitorIndex]
                : monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

            int screenWidth = monitor.Width;
            int screenHeight = monitor.Height;
            int captureWidth = (int)(screenWidth * scale);
            int captureHeight = (int)(screenHeight * scale);

            using var screenBitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screenBitmap))
            {
                g.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
            }

            Bitmap outputBitmap;
            if (scale < 1.0)
            {
                outputBitmap = new Bitmap(captureWidth, captureHeight);
                using var g = Graphics.FromImage(outputBitmap);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(screenBitmap, 0, 0, captureWidth, captureHeight);
            }
            else
            {
                outputBitmap = screenBitmap;
            }

            try
            {
                using var ms = new MemoryStream();
                var jpegEncoder = GetJpegEncoder();
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                outputBitmap.Save(ms, jpegEncoder, encoderParams);
                return Task.FromResult(ms.ToArray());
            }
            finally
            {
                if (scale < 1.0)
                    outputBitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture screen.");
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    public (int Width, int Height) GetScreenSize(int monitorIndex = 0)
    {
        var monitors = GetMonitors();
        if (monitorIndex >= 0 && monitorIndex < monitors.Count)
            return (monitors[monitorIndex].Width, monitors[monitorIndex].Height);

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return (primary.Width, primary.Height);
    }

    public List<MonitorInfo> GetMonitors()
    {
        lock (_monitorLock)
        {
            if ((DateTime.UtcNow - _lastMonitorRefresh) > MonitorRefreshInterval || _cachedMonitors.Count == 0)
            {
                _cachedMonitors = EnumerateMonitors();
                _lastMonitorRefresh = DateTime.UtcNow;
            }
            return _cachedMonitors;
        }
    }

    private List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int index = 0;

        MonitorEnumDelegate callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorInfo
                {
                    Index = index++,
                    Name = info.szDevice.TrimEnd('\0'),
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                });
            }
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // Fallback: use primary screen metrics
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            monitors.Add(new MonitorInfo
            {
                Index = 0,
                Name = "Primary",
                Left = 0,
                Top = 0,
                Width = w,
                Height = h,
                IsPrimary = true,
            });
        }

        return monitors;
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.MimeType == "image/jpeg")
                return codec;
        }
        throw new InvalidOperationException("JPEG encoder not found.");
    }

    #region P/Invoke

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    #endregion
}
