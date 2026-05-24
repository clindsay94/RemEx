using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
public class WindowsScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly ILogger<WindowsScreenCaptureService> _logger;
    private readonly DxgiDesktopCapture _dxgi;
    private bool _session0Warned;

    public WindowsScreenCaptureService(ILogger<WindowsScreenCaptureService> logger)
    {
        _logger = logger;

        // Detect Session 0 early — screen capture will never work there.
        var sessionId = Process.GetCurrentProcess().SessionId;
        if (sessionId == 0)
        {
            _logger.LogWarning(
                "Running in Session 0 (non-interactive). Screen capture will NOT work. " +
                "Run the Remex Desktop app interactively, or configure the service to log on as a user.");
        }

        // Initialize DXGI Desktop Duplication. Falls back gracefully to GDI if unavailable.
        _dxgi = new DxgiDesktopCapture(logger);
        if (_dxgi.IsAvailable)
            _logger.LogInformation("DXGI Desktop Duplication initialized ({W}x{H}). MPO/overlay planes will be captured correctly.", _dxgi.Width, _dxgi.Height);
        else
            _logger.LogWarning("DXGI Desktop Duplication unavailable — falling back to GDI CopyFromScreen. Windows Terminal focus bug may occur.");
    }

    public string? BackendName => _dxgi.IsAvailable ? "dxgi" : "gdi";
    public bool IsDxgiAvailable => _dxgi.IsAvailable;
    public string? DxgiUnavailableReason => _dxgi.UnavailableReason;
    public string? LastCaptureFailureReason { get; private set; }

    public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);

        ct.ThrowIfCancellationRequested();

        // ── Primary path: DXGI Desktop Duplication ──────────────────────────────
        // Correctly captures GPU-composited content including hardware overlay planes
        // (MPO) used by Windows Terminal, Chrome GPU compositing, and DirectX apps —
        // which GDI BitBlt/CopyFromScreen cannot capture.
        if (_dxgi.IsAvailable)
        {
            var dxgiFrame = _dxgi.TryCapture(quality, scale, GetJpegEncoder());
            if (dxgiFrame is { Length: > 0 })
            {
                LastCaptureFailureReason = null;
                return Task.FromResult(dxgiFrame);
            }
        }

        // ── Fallback path: GDI CopyFromScreen ───────────────────────────────────
        // Cannot capture MPO planes but works for standard windows when DXGI is unavailable.
        try
        {
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);
            int captureWidth = (int)(screenWidth * scale);
            int captureHeight = (int)(screenHeight * scale);

            using var screenBitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screenBitmap))
            {
                try
                {
                    // Add CaptureBlt to include layered windows and bypass some DWM single-window MPO exclusions
                    g.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("CopyFromScreen failed (likely due to MPO or terminal focus). Attempting fallback without CaptureBlt. Error: {Msg}", ex.Message);
                    // Fallback to standard copy if CaptureBlt caused issues
                    g.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
                }

                // Draw the system cursor onto the captured bitmap
                DrawCursorOnBitmap(g);
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
                LastCaptureFailureReason = null;
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
            LastCaptureFailureReason = BuildCaptureFailureReason(Process.GetCurrentProcess().SessionId, ex.Message);
            if (!_session0Warned)
            {
                var sessionId = Process.GetCurrentProcess().SessionId;
                _logger.LogError(ex, "Failed to capture screen (Session {SessionId}). {Hint}",
                    sessionId,
                    sessionId == 0
                        ? "Session 0 cannot capture the desktop. Run the Remex Desktop app interactively."
                        : "Ensure the desktop is not locked and the process has screen access.");
                _session0Warned = sessionId == 0;
            }
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    public (int Width, int Height, int Left, int Top) GetScreenSize()
    {
        if (_dxgi.IsAvailable && _dxgi.Width > 0 && _dxgi.Height > 0)
            return (_dxgi.Width, _dxgi.Height, _dxgi.DesktopLeft, _dxgi.DesktopTop);
        
        // GDI captures primary monitor (always at 0,0 in Windows virtual space if it's the anchor)
        // Actually, primary monitor is not ALWAYS at 0,0 if virtual desk is weird, but it usually is.
        return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN), 0, 0);
    }

    public void Dispose() => _dxgi.Dispose();

    private static ImageCodecInfo? _cachedJpegEncoder;

    private static ImageCodecInfo GetJpegEncoder()
    {
        if (_cachedJpegEncoder != null) return _cachedJpegEncoder;
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.MimeType == "image/jpeg")
                return _cachedJpegEncoder = codec;
        }
        throw new InvalidOperationException("JPEG encoder not found.");
    }

    private static string BuildCaptureFailureReason(int sessionId, string message)
        => sessionId == 0
            ? $"Screen capture failed in Session 0 ({message}). Run Remex Desktop interactively or configure the Windows service to log on as the signed-in user."
            : $"Screen capture failed ({message}). The desktop may be locked, showing a secure prompt, or denying screen capture access.";

    #region P/Invoke

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // ── Cursor drawing P/Invoke ───────────────────────────────────────────────

    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint DI_NORMAL = 0x0003;

    /// <summary>
    /// Draws the system cursor at its current position onto the given Graphics surface.
    /// Uses GetCursorInfo + DrawIconEx for reliable cursor rendering including animated cursors.
    /// </summary>
    private static void DrawCursorOnBitmap(Graphics g)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
            return;

        // Get hotspot offset so the cursor is drawn at the correct position
        if (GetIconInfo(ci.hCursor, out var iconInfo))
        {
            int drawX = ci.ptScreenPos.X - iconInfo.xHotspot;
            int drawY = ci.ptScreenPos.Y - iconInfo.yHotspot;

            // Clean up GDI bitmap handles from GetIconInfo
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);

            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, drawX, drawY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
    }

    #endregion
}
