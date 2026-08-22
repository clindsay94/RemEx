using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using SkiaSharp;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// Generates small JPEG thumbnails for the file-manager UI (plan §1.2, WP3). Uses SkiaSharp — which is a
/// PC-host-only dependency; <c>Remex.Core</c> stays NativeAOT-safe because the wire surface only ever sees
/// a base64 string. v1 supports <b>images only</b>: the source is decoded with SkiaSharp, scaled to fit the
/// requested maximum edge, and re-encoded as JPEG under the <see cref="Remex.Core.Models.FileTransferLimits.ThumbnailMaxBytes"/>
/// byte budget. Videos/documents are out of scope for v1 and yield <c>null</c> (no thumbnail), never an error.
///
/// <para>
/// This is a plain object constructed by <see cref="FileTransferService"/> rather than a DI-registered
/// service, so it adds no host bootstrap wiring. It is still trivially unit-testable by newing it with a
/// <c>NullLogger</c>.
/// </para>
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>
    /// Source files larger than this are never decoded — a thumbnail is not worth loading a huge blob into
    /// memory, and an over-large "image" is almost certainly not a real thumbnail candidate.
    /// </summary>
    private const long MaxSourceBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Extensions SkiaSharp can reliably decode. Kept as an allowlist so we never attempt to decode an
    /// arbitrary (potentially large) non-image file. Videos are intentionally excluded in v1.
    /// </summary>
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>JPEG qualities tried in order until the encoded size fits the byte budget.</summary>
    private static readonly int[] QualityLadder = [82, 65, 50, 38];

    private readonly ILogger _logger;

    public ThumbnailService(ILogger logger)
    {
        _logger = Guard.NotNull(logger);
    }

    /// <summary>
    /// Returns a base64-encoded JPEG thumbnail for <paramref name="absoluteFilePath"/>, or <c>null</c> when
    /// the file is not a supported image, is too large, cannot be decoded, or cannot be squeezed under
    /// <paramref name="maxBytes"/>. Never throws for an unsupported/undecodable input — a missing thumbnail
    /// is a normal outcome, not an error.
    /// </summary>
    /// <param name="absoluteFilePath">Absolute path to an already root-validated file.</param>
    /// <param name="maxDim">Maximum output edge length in pixels (longest side).</param>
    /// <param name="maxBytes">Maximum encoded JPEG size in bytes.</param>
    public async Task<string?> TryCreateThumbnailBase64Async(
        string absoluteFilePath, int maxDim, int maxBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath) || !File.Exists(absoluteFilePath))
            return null;

        if (!IsSupportedImageExtension(absoluteFilePath))
            return null;

        if (maxDim <= 0)
            maxDim = Remex.Core.Models.FileTransferLimits.ThumbnailDefaultMaxDim;
        if (maxBytes <= 0)
            maxBytes = Remex.Core.Models.FileTransferLimits.ThumbnailMaxBytes;

        try
        {
            var length = new FileInfo(absoluteFilePath).Length;
            if (length <= 0 || length > MaxSourceBytes)
                return null;

            var bytes = await File.ReadAllBytesAsync(absoluteFilePath, ct);
            ct.ThrowIfCancellationRequested();

            using var source = SKBitmap.Decode(bytes);
            if (source is null || source.Width <= 0 || source.Height <= 0)
                return null;

            using var scaled = ScaleToFit(source, maxDim);
            var toEncode = scaled ?? source;

            var base64 = Encode(toEncode, maxBytes);
            if (base64 is null)
            {
                // The scaled image still overflowed the byte budget at the lowest quality; try one more
                // aggressive downscale before giving up so a pathological source still yields a thumbnail.
                using var half = ScaleToFit(toEncode, Math.Max(16, maxDim / 2));
                if (half is not null)
                    base64 = Encode(half, maxBytes);
            }

            return base64;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A decode/encode failure means "no thumbnail available", not a transfer failure.
            _logger.LogDebug(ex, "Thumbnail generation failed for {Path}; returning no thumbnail.", absoluteFilePath);
            return null;
        }
    }

    private static bool IsSupportedImageExtension(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return false;

        foreach (var candidate in ImageExtensions)
        {
            if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a new bitmap scaled so its longest edge is at most <paramref name="maxDim"/>, preserving
    /// aspect ratio. Returns <c>null</c> when the source already fits (caller encodes the source directly).
    /// </summary>
    private static SKBitmap? ScaleToFit(SKBitmap source, int maxDim)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxDim)
            return null;

        var scale = (double)maxDim / longest;
        var targetW = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetH = Math.Max(1, (int)Math.Round(source.Height * scale));

        var dst = new SKBitmap(new SKImageInfo(targetW, targetH, source.ColorType, source.AlphaType));
        // Medium's SkiaSharp 3 equivalent: bilinear filter with mipmapping on reduction
        // (RemEx-jcma3). Thumbnails are always a reduction, which is exactly the case the mipmap
        // half covers.
        if (source.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
            return dst;

        dst.Dispose();
        return null;
    }

    /// <summary>
    /// Encodes <paramref name="bitmap"/> as JPEG, walking the quality ladder until the result fits within
    /// <paramref name="maxBytes"/>. Returns the base64 string, or <c>null</c> if even the lowest quality
    /// overflows the budget.
    /// </summary>
    private static string? Encode(SKBitmap bitmap, int maxBytes)
    {
        using var image = SKImage.FromBitmap(bitmap);
        if (image is null)
            return null;

        foreach (var quality in QualityLadder)
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is null)
                continue;
            if (data.Size <= maxBytes)
                return Convert.ToBase64String(data.ToArray());
        }

        return null;
    }
}
