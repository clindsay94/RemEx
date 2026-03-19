using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

    public WindowsScreenCaptureService(ILogger<WindowsScreenCaptureService> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);

        try
        {
            var (screenWidth, screenHeight) = GetScreenSize();
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
            _logger.LogError(ex, "Failed to capture screen.");
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    public (int Width, int Height) GetScreenSize()
    {
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        return (width, height);
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

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
