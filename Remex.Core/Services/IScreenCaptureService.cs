using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services;

public interface IScreenCaptureService
{
    /// <summary>
    /// Captures a specific monitor and returns JPEG-encoded bytes.
    /// </summary>
    /// <param name="quality">JPEG quality 1-100.</param>
    /// <param name="scale">Resolution scale factor 0.25-1.0.</param>
    /// <param name="monitorIndex">Monitor index (0 = primary).</param>
    Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, int monitorIndex = 0, CancellationToken ct = default);

    /// <summary>
    /// Gets the native screen dimensions for a specific monitor.
    /// </summary>
    (int Width, int Height) GetScreenSize(int monitorIndex = 0);

    /// <summary>
    /// Returns metadata about all connected monitors.
    /// </summary>
    List<MonitorInfo> GetMonitors();
}
