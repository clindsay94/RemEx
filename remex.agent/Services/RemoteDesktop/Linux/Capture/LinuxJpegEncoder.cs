using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Remex.Agent.Services.RemoteDesktop.Linux.Capture;

/// <summary>
/// Encodes a <see cref="LinuxFrameSnapshot"/> to JPEG bytes using SkiaSharp.
/// Runs entirely in managed memory; no temp files, no subprocesses.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxJpegEncoder
{
    // SPA_VIDEO_FORMAT_* constants — what KDE Plasma / PipeWire portals report.
    // Numeric values from spa/param/video/raw.h (spa_video_format enum).
    private const uint SpaFormatRGBx = 7u;
    private const uint SpaFormatBGRx = 8u;
    private const uint SpaFormatRGBA = 11u;
    private const uint SpaFormatBGRA = 12u;
    private const uint SpaFormatNV12 = 22u;

    // DRM fourcc constants (ASCII-packed, little-endian uint32).
    // Included as a defensive secondary match for compositors that report
    // DRM fourccs instead of SPA format IDs.
    // DRM_FORMAT_BGRA8888 = 'B','G','R','A' = 0x41524742
    private const uint FourccBGRA = 0x41524742u;
    // DRM_FORMAT_RGBA8888 = 'R','G','B','A' = 0x41424752
    private const uint FourccRGBA = 0x41424752u;
    // DRM_FORMAT_BGRX8888 = 'B','G','R','X' = 0x58524742  (alpha ignored)
    private const uint FourccBGRx = 0x58524742u;
    // DRM_FORMAT_RGBX8888 = 'R','G','B','X' = 0x58424752
    private const uint FourccRGBx = 0x58424752u;

    /// <summary>
    /// Encodes a PipeWire frame snapshot to a JPEG byte array.
    /// </summary>
    /// <param name="frame">Source frame — must have Data or RawData set.</param>
    /// <param name="quality">JPEG quality 1–100.</param>
    /// <param name="scale">Output scale factor 0.0–1.0.</param>
    /// <param name="logger">Logger for warnings.</param>
    /// <param name="formatTag">Receives a short human-readable format name for diagnostics.</param>
    /// <returns>JPEG bytes, or an empty array on failure.</returns>
    public static byte[] Encode(
        LinuxFrameSnapshot frame,
        int quality,
        double scale,
        ILogger logger,
        out string formatTag,
        int cropX = 0,
        int cropY = 0,
        int cropW = 0,
        int cropH = 0)
    {
        formatTag = string.Empty;
        GCHandle pinHandle = default;
        try
        {
            var colorType = MapFormat(frame.Format, logger, out formatTag);
            if (colorType == SKColorType.Unknown)
                return Array.Empty<byte>();

            var info = new SKImageInfo(frame.Width, frame.Height, colorType, SKAlphaType.Premul);

            SKImage? image;
            if (frame.Data is not null)
            {
                pinHandle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
                var ptr = pinHandle.AddrOfPinnedObject();
                image = SKImage.FromPixels(info, ptr, frame.Stride);
            }
            else if (frame.RawData != IntPtr.Zero)
            {
                image = SKImage.FromPixels(info, frame.RawData, frame.Stride);
            }
            else
            {
                logger.LogWarning("LinuxJpegEncoder: frame has no accessible pixel data.");
                return Array.Empty<byte>();
            }

            if (image is null)
            {
                logger.LogWarning(
                    "LinuxJpegEncoder: SKImage.FromPixels returned null (format={Format}).", formatTag);
                return Array.Empty<byte>();
            }

            // Crop to the active monitor rect before scaling/encoding (cropW<=0 = full desktop). (RemEx-nadp)
            SKImage? cropped = (cropW > 0 && cropH > 0)
                ? image.Subset(SKRectI.Create(cropX, cropY, cropW, cropH))
                : null;

            using (image)
            using (cropped)
            {
                SKImage baseImage = cropped ?? image;

                if (scale >= 0.99)
                {
                    using var data = baseImage.Encode(SKEncodedImageFormat.Jpeg, quality);
                    return data?.ToArray() ?? Array.Empty<byte>();
                }

                var targetW = Math.Max(1, (int)(baseImage.Width * scale));
                var targetH = Math.Max(1, (int)(baseImage.Height * scale));
                var destInfo = new SKImageInfo(targetW, targetH, colorType, SKAlphaType.Premul);
                using var srcBitmap = SKBitmap.FromImage(baseImage);
                using var dstBitmap = new SKBitmap(destInfo);
                // SKFilterQuality is gone in SkiaSharp 3 (RemEx-jcma3). Linear + mipmap Linear is
                // what Medium meant: SkiaSharp's own docs describe Medium as "bilerp filter, and
                // mipmap when size is reduced", and every call here IS a reduction. Picking plain
                // Linear instead would drop the mipmap and alias the downscale — visible as shimmer
                // on a scaled remote desktop, with nothing failing to say so.
                if (!srcBitmap.ScalePixels(dstBitmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                {
                    logger.LogWarning(
                        "LinuxJpegEncoder: ScalePixels failed; encoding at original resolution.");
                    using var fallback = baseImage.Encode(SKEncodedImageFormat.Jpeg, quality);
                    return fallback?.ToArray() ?? Array.Empty<byte>();
                }

                using var scaledData = dstBitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
                return scaledData?.ToArray() ?? Array.Empty<byte>();
            }
        }
        catch (OutOfMemoryException oom)
        {
            logger.LogError(oom, "LinuxJpegEncoder: out of memory encoding frame.");
            return Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LinuxJpegEncoder: encode threw; returning empty.");
            return Array.Empty<byte>();
        }
        finally
        {
            if (pinHandle.IsAllocated)
                pinHandle.Free();
        }
    }

    internal static SKColorType MapFormat(uint format, ILogger logger, out string tag)
    {
        switch (format)
        {
            // SPA format IDs (primary — what KDE Plasma portal reports)
            case SpaFormatBGRA:
                tag = "BGRA(SPA:12)";
                return SKColorType.Bgra8888;

            case SpaFormatBGRx:
                tag = "BGRx(SPA:8)";
                return SKColorType.Bgra8888;

            case SpaFormatRGBA:
                tag = "RGBA(SPA:11)";
                return SKColorType.Rgba8888;

            case SpaFormatRGBx:
                tag = "RGBx(SPA:7)";
                return SKColorType.Rgba8888;

            // DRM fourcc (defensive fallback for compositors reporting DRM codes)
            case FourccBGRx:
            case FourccBGRA:
                tag = "BGRA(DRM)";
                return SKColorType.Bgra8888;

            case FourccRGBx:
            case FourccRGBA:
                tag = "RGBA(DRM)";
                return SKColorType.Rgba8888;

            // Format 0: native bridge stub (format field is memset-to-zero).
            // Treat as BGRA — the most common 32-bpp format on KDE Plasma 6.
            case 0u:
                tag = "BGRA(assumed)";
                return SKColorType.Bgra8888;

            case SpaFormatNV12:
                tag = string.Empty;
                logger.LogWarning(
                    "LinuxJpegEncoder: NV12 pixel format is not supported; frame will be dropped.");
                return SKColorType.Unknown;

            default:
                tag = string.Empty;
                var fourcc = new string([
                    (char)(format & 0xFF),
                    (char)((format >> 8) & 0xFF),
                    (char)((format >> 16) & 0xFF),
                    (char)((format >> 24) & 0xFF),
                ]);
                logger.LogWarning(
                    "LinuxJpegEncoder: unsupported pixel format fourcc={Fourcc} (0x{Code:X8}); frame will be dropped.",
                    fourcc, format);
                return SKColorType.Unknown;
        }
    }
}
