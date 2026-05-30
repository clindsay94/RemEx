using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services;

public interface IScreenCaptureService
{
    /// <summary>
    /// Captures the primary screen and returns JPEG-encoded bytes.
    /// </summary>
    /// <param name="quality">JPEG quality 1-100.</param>
    /// <param name="scale">Resolution scale factor 0.25-1.0.</param>
    /// <param name="drawCursor">Whether to draw the cursor onto the bitmap.</param>
    Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default);

    /// <summary>
    /// Gets the native screen dimensions and virtual desktop offsets for the captured area.
    /// </summary>
    (int Width, int Height, int Left, int Top) GetScreenSize();

    /// <summary>
    /// Human-readable name of the active capture backend for diagnostics and DesktopMeta.
    /// Returns null when not available.
    /// </summary>
    string? BackendName => null;
}
