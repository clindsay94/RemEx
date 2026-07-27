namespace Remex.Core.Services;

/// <summary>
/// Abstraction over a Windows.Graphics.Capture (WGC) per-monitor frame source, kept WinRT-free so it
/// can live in <c>Remex.Core</c> and be consumed by the orchestrator (<c>WindowsScreenCaptureService</c>,
/// a plain <c>net10.0</c> assembly) without that assembly ever referencing a WinRT projection.
///
/// <para>
/// The single concrete implementation, <c>WgcDesktopCapture</c>, lives in the Windows-only
/// <c>Remex.Agent.Windows</c> project (TFM <c>net10.0-windows10.0.19041.0</c>) — the only place
/// WinRT types are referenced. It is registered as this interface under <c>#if WGC_CAPTURE</c> and
/// injected into the orchestrator as an optional dependency: <c>null</c> on Linux (project not
/// referenced) or whenever WGC initialization fails, in which case the orchestrator falls through to
/// its existing DXGI → GDI tiers unchanged.
/// </para>
///
/// <para>
/// Deliberately <b>frame-only</b>: it exposes no cursor capture. With WGC the session sets
/// <c>IsCursorCaptureEnabled = false</c> (no cursor baked into the frame), and the orchestrator's
/// existing GDI cursor-shape path (<c>WindowsCursorShapeCapture</c>) already serves the cursor whenever
/// DXGI is not the active tier — so the WGC source never needs to touch the cursor.
/// </para>
///
/// <para>
/// Frame liveness mirrors the DXGI contract: <c>TryCaptureRaw</c>'s <c>isLive</c> out-parameter is
/// <c>false</c> when a stale cached frame is replayed because the capture session was lost
/// (item closed / device removed), so the handler stops counting a frozen output as a successful
/// capture (RemEx-ltd). Implementations cache the last good frame and back off session re-creation,
/// exactly as <c>DxgiDesktopCapture</c> does for <c>DuplicateOutput</c>.
/// </para>
/// </summary>
public interface IWgcCaptureSource : IDisposable
{
    /// <summary><c>true</c> when a capture session is established for the selected monitor and frames can be pulled.</summary>
    bool IsAvailable { get; }

    /// <summary>Native pixel width of the selected monitor (pre-scale). Zero until a monitor is selected.</summary>
    int Width { get; }

    /// <summary>Native pixel height of the selected monitor (pre-scale). Zero until a monitor is selected.</summary>
    int Height { get; }

    /// <summary>Virtual-desktop X offset of the selected monitor's top-left (for cursor compositing / bounds).</summary>
    int DesktopLeft { get; }

    /// <summary>Virtual-desktop Y offset of the selected monitor's top-left.</summary>
    int DesktopTop { get; }

    /// <summary>
    /// Win32 device name of the monitor this source is currently capturing (e.g. <c>\\.\DISPLAY1</c>),
    /// or <c>null</c> when no monitor is selected. The orchestrator matches this against the active
    /// <c>DesktopCaptureTarget</c>'s display id to decide whether WGC can serve the current target.
    /// </summary>
    string? OutputDeviceName { get; }

    /// <summary>
    /// Points the source at the monitor identified by its Win32 device name (e.g. <c>\\.\DISPLAY1</c>),
    /// (re)creating the WGC capture item, frame pool, and session for it. The implementation resolves the
    /// <c>HMONITOR</c> from the device name itself, so the orchestrator need not expose monitor handles.
    /// Returns <c>false</c> and sets <paramref name="error"/> when the monitor cannot be resolved or the
    /// session cannot be created.
    /// </summary>
    bool TrySelectMonitor(string deviceName, out string? error);

    /// <summary>
    /// Captures the selected monitor and returns raw 32-bit <b>BGRA</b> bytes scaled to the even-aligned
    /// target size (<see cref="CaptureScaling.ScaledEven"/>) so the buffer matches what the H.264 encoder
    /// was started with. The cursor is never drawn (WGC captures cursor-less; <paramref name="drawCursor"/>
    /// is accepted for signature parity but ignored). <paramref name="isLive"/> is <c>false</c> when a stale
    /// cached frame was replayed because the session was lost. Returns <c>null</c> when nothing could be
    /// produced and there is no cached frame.
    /// </summary>
    byte[]? TryCaptureRaw(double scale, bool drawCursor, out bool isLive);

    /// <summary>
    /// Attempts to re-establish a lost capture session (item closed / device removed), rate-limited by an
    /// internal backoff so a sustained loss does not hammer session re-creation. No-op when already available.
    /// </summary>
    void TryRecover();
}
