using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;

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
    /// Captures the primary screen and returns the raw 32-bit BGRA pixel bytes.
    /// Used by video encoders (e.g. H.264) to bypass JPEG overhead.
    /// </summary>
    Task<byte[]?> CaptureRawScreenAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// Gets the native screen dimensions and virtual desktop offsets for the captured area.
    /// </summary>
    (int Width, int Height, int Left, int Top) GetScreenSize();

    /// <summary>
    /// Human-readable name of the active capture backend for diagnostics and DesktopMeta.
    /// Returns null when not available.
    /// </summary>
    string? BackendName => null;

    /// <summary>
    /// Returns the currently available remote desktop targets for this runtime.
    /// Default implementations expose a single virtual desktop surface for legacy backends.
    /// </summary>
    DesktopDisplayCatalog GetDisplayCatalog()
    {
        var (width, height, left, top) = GetScreenSize();
        return new DesktopDisplayCatalog
        {
            DisplayListVersion = 1,
            SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop],
            Displays =
            [
                new DesktopDisplayInfo
                {
                    DisplayId = "default",
                    PersistentDisplayKey = "default",
                    Name = "Display",
                    IsPrimary = true,
                    Left = left,
                    Top = top,
                    Width = width,
                    Height = height,
                },
            ],
        };
    }

    /// <summary>
    /// Applies an explicit capture target for subsequent captures.
    /// Default implementations only support the virtual desktop surface.
    /// </summary>
    bool TrySetCaptureTarget(DesktopCaptureTarget target, out string? error)
    {
        if (target.CaptureMode == DesktopCaptureMode.VirtualDesktop)
        {
            error = null;
            return true;
        }

        error = "The current host runtime does not support per-monitor capture selection.";
        return false;
    }
}
