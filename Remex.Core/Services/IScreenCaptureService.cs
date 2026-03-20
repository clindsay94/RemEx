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
    Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, CancellationToken ct = default);

    /// <summary>
    /// Gets the native screen dimensions for the primary monitor.
    /// </summary>
    (int Width, int Height) GetScreenSize();
}
