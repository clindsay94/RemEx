using Remex.Core.Models;

namespace Remex.Core.Services;

/// <summary>
/// Result of a single screen-capture attempt: the pixel bytes plus a liveness signal.
/// </summary>
/// <remarks>
/// <see cref="IsLive"/> distinguishes a genuinely-produced frame (or a cached frame returned because the
/// desktop was unchanged on a HEALTHY output) from a STALE cached replay returned because the capture
/// output was lost (DXGI_ERROR_ACCESS_LOST / session disconnect / secure desktop). Without it, a frozen
/// output that keeps replaying its last good frame is indistinguishable from a live static screen, so the
/// stream silently freezes instead of reporting a coded error (RemEx-ltd). This is host-internal data flow
/// only — it never crosses the wire, so no protocol/envelope change is implied. NativeAOT-safe: a plain
/// readonly struct, never reflected over or serialized.
/// </remarks>
public readonly struct ScreenCaptureResult
{
    public ScreenCaptureResult(byte[]? pixels, bool isLive)
    {
        Pixels = pixels;
        IsLive = isLive;
    }

    /// <summary>Encoded (JPEG) or raw (BGRA) pixel bytes; <c>null</c> or empty when capture produced nothing.</summary>
    public byte[]? Pixels { get; }

    /// <summary>
    /// <c>true</c> when the capture pipeline is confirmed healthy this tick (a new frame, or a cached frame on
    /// an unchanged but healthy output). <c>false</c> when a cached frame was replayed because the output was
    /// lost/errored — a sustained run of which must trip the caller's consecutive-failure / coded-error path.
    /// </summary>
    public bool IsLive { get; }
}

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
    /// Liveness-aware raw (BGRA) capture. Returns the pixels plus <see cref="ScreenCaptureResult.IsLive"/>,
    /// which is <c>false</c> when the backend replayed a STALE cached frame because its capture output was
    /// lost (e.g. DXGI_ERROR_ACCESS_LOST / session disconnect) rather than because the desktop was simply
    /// unchanged. Callers use this to stop counting a frozen output as a successful capture (RemEx-ltd).
    /// The default wraps <see cref="CaptureRawScreenAsync"/> and reports <c>IsLive = true</c>; a backend
    /// that cannot detect stale replays is assumed always-live, preserving today's behavior.
    /// </summary>
    Task<ScreenCaptureResult> CaptureRawScreenLiveAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => AsLiveResult(CaptureRawScreenAsync(scale, drawCursor, ct));

    /// <summary>
    /// Liveness-aware JPEG capture. See <see cref="CaptureRawScreenLiveAsync"/> for the meaning of
    /// <see cref="ScreenCaptureResult.IsLive"/>. The default wraps <see cref="CaptureScreenAsync"/> and
    /// reports <c>IsLive = true</c>.
    /// </summary>
    Task<ScreenCaptureResult> CaptureScreenLiveAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => AsLiveResultNonNull(CaptureScreenAsync(quality, scale, drawCursor, ct));

    /// <summary>
    /// Gets the native screen dimensions and virtual desktop offsets for the captured area.
    /// </summary>
    (int Width, int Height, int Left, int Top) GetScreenSize();

    /// <summary>
    /// Primes the capture backend for immediate use. Call once per client connection,
    /// before the first <see cref="GetScreenSize"/> call, so dimensions reflect live
    /// hardware state rather than a stale fallback (RemEx-6my).
    /// Default: no-op — backends that initialize eagerly need no warm-up.
    /// </summary>
    void WarmUpCapture() { }

    /// <summary>
    /// Human-readable name of the active capture backend for diagnostics and DesktopMeta.
    /// Returns null when not available.
    /// </summary>
    string? BackendName => null;

    /// <summary>
    /// <c>true</c> when the host's display is powered off (monitor sleep), so callers should PAUSE
    /// capture entirely rather than poke a powered-off output — defense-in-depth against the
    /// Desktop Duplication re-init storm that can wedge the GPU driver (RemEx-960, on top of RemEx-crk).
    /// Default <c>false</c>: backends that can't observe display power (Linux, fakes) never pause.
    /// </summary>
    bool IsDisplayPoweredOff => false;

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
                    // No persistent key: this branch could not enumerate displays at all, so the
                    // host has no stable identity to offer and must not invent one. Empty is the
                    // documented way to say that; "default" claimed an identity nobody established.
                    // Costs nothing — there is exactly one display here, and the client selects the
                    // primary when it remembers nothing. (RemEx-kiy1)
                    PersistentDisplayKey = string.Empty,
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

    // Wraps a legacy byte[] capture as a liveness-aware result, reporting IsLive=true (the always-live
    // default for backends that don't detect stale replays). private static interface members are C# 8+.
    // Two distinctly-named helpers (overloading on byte[]? vs byte[] alone is a CS0111 error since they
    // differ only by nullability annotation) keep the call sites warning-free under nullable reference types.
    private static async Task<ScreenCaptureResult> AsLiveResult(Task<byte[]?> capture)
        => new ScreenCaptureResult(await capture, isLive: true);

    private static async Task<ScreenCaptureResult> AsLiveResultNonNull(Task<byte[]> capture)
        => new ScreenCaptureResult(await capture, isLive: true);
}
