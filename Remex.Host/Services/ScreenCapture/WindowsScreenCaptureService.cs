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
public class WindowsScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<WindowsScreenCaptureService> _logger;
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
    }

    public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);

        try
        {
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);
            int captureWidth = (int)(screenWidth * scale);
            int captureHeight = (int)(screenHeight * scale);

            using var screenBitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screenBitmap))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
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

    public (int Width, int Height) GetScreenSize()
    {
        return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    #endregion
}
