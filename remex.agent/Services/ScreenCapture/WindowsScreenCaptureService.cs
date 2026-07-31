using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
public class WindowsScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly ILogger<WindowsScreenCaptureService> _logger;
    private readonly DxgiDesktopCapture _dxgi;
    private readonly WindowsDisplayPowerMonitor _displayPower;
    private readonly object _displayLock = new();

    // WGC (Windows.Graphics.Capture) is the preferred backend when present. Injected as an optional
    // dependency: null on Linux (the Windows-only capture project isn't referenced) or when WGC init
    // failed — in which case capture falls through to the existing DXGI → GDI tiers unchanged. The
    // backend preference (HKLM) decides where in the WGC → DXGI → GDI ladder we start.
    private readonly IWgcCaptureSource? _wgc;
    private readonly CaptureBackend _backend;

    // WGC participates only when present and not pinned below it by the HKLM preference (Dxgi/Gdi).
    private bool WgcEnabled => _wgc is not null && _backend is not (CaptureBackend.Dxgi or CaptureBackend.Gdi);

    private List<DisplayDefinition> _displays = [];
    private DesktopCaptureTarget _activeTarget = new() { CaptureMode = DesktopCaptureMode.VirtualDesktop };
    private int _displayListVersion = 1;

    // Throttle for capture-failure error logs. Without this, a sustained outage (locked desktop,
    // disconnected session) logs one error + stack trace per frame at the target FPS, flooding logs.
    private DateTime _lastCaptureErrorLogUtc = DateTime.MinValue;
    private static readonly TimeSpan CaptureErrorLogInterval = TimeSpan.FromSeconds(5);

    private bool ShouldLogCaptureError()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCaptureErrorLogUtc < CaptureErrorLogInterval)
            return false;
        _lastCaptureErrorLogUtc = now;
        return true;
    }

    public WindowsScreenCaptureService(ILogger<WindowsScreenCaptureService> logger)
        : this(logger, wgc: null)
    {
    }

    public WindowsScreenCaptureService(ILogger<WindowsScreenCaptureService> logger, IWgcCaptureSource? wgc)
    {
        _logger = Guard.NotNull(logger);
        _wgc = wgc;
        // Machine-wide backend preference, stored in HKLM (never HKCU) so it is shared regardless of
        // which user is signed in and survives across logins. Fails open to Auto.
        _backend = OperatingSystem.IsWindows() ? CaptureBackendPreference.Read(logger) : CaptureBackend.Auto;
        if (_wgc is not null)
        {
            _logger.LogInformation("WGC capture backend available; preference = {Backend}.", _backend);
        }

        // Watch the console display power state so the stream loop can pause while the monitor is off
        // (RemEx-960). Fails open — if it can't register, IsDisplayPoweredOff stays false.
        _displayPower = new WindowsDisplayPowerMonitor(logger);

        // Initialize DXGI Desktop Duplication. Falls back gracefully to GDI if unavailable.
        _dxgi = new DxgiDesktopCapture(logger);
        if (_dxgi.IsAvailable)
        {
            _logger.LogInformation(
                "DXGI Desktop Duplication initialized ({W}x{H}). MPO/overlay planes will be captured correctly.",
                _dxgi.Width,
                _dxgi.Height);
        }
        else
        {
            _logger.LogWarning("DXGI Desktop Duplication unavailable — falling back to GDI CopyFromScreen. Windows Terminal focus bug may occur.");
        }

        RefreshDisplays();
        var primaryDisplay = _displays.FirstOrDefault(static display => display.IsPrimary);
        if (primaryDisplay is not null)
        {
            _activeTarget = new DesktopCaptureTarget
            {
                CaptureMode = DesktopCaptureMode.Monitor,
                DisplayId = primaryDisplay.DisplayId,
            };
        }
    }

    public string? BackendName => WgcServesActiveTarget() ? "wgc" : CanUseDxgiForCurrentTarget() ? "dxgi" : "gdi";
    public bool IsDisplayPoweredOff => _displayPower.IsDisplayOff;

    // Win32 device name (e.g. \\.\DISPLAY1) of the active per-monitor target, or null for virtual-desktop
    // mode or an unresolved display. WGC and DXGI both capture per-monitor; virtual-desktop stays on GDI.
    private string? ActiveMonitorDeviceName()
    {
        lock (_displayLock)
        {
            if (_activeTarget.CaptureMode != DesktopCaptureMode.Monitor || string.IsNullOrWhiteSpace(_activeTarget.DisplayId))
            {
                return null;
            }

            var display = _displays.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayId, _activeTarget.DisplayId, StringComparison.Ordinal));
            return string.IsNullOrWhiteSpace(display?.DeviceName) ? null : display.DeviceName;
        }
    }

    // Pure capability check (no side effects) — safe to call from the BackendName getter and the hot path.
    private bool WgcServesActiveTarget()
    {
        if (!WgcEnabled || _wgc is null || !_wgc.IsAvailable)
        {
            return false;
        }

        var deviceName = ActiveMonitorDeviceName();
        return deviceName is not null &&
               string.Equals(_wgc.OutputDeviceName, deviceName, StringComparison.OrdinalIgnoreCase);
    }

    // De-dupes the per-frame WGC selection outcome log (RemEx-hvqv) so a persistent state is logged once,
    // not at the capture frame rate. Holds the last logged outcome key; logging fires only when it changes.
    private string? _lastWgcSelectionStateKey;

    // Points WGC at the active monitor when the target changed. TrySelectMonitor (re)creates the WGC
    // session only when the device name differs, so this is cheap on the steady-state capture path.
    //
    // Diagnostics (RemEx-hvqv): WGC silently losing to DXGI/GDI used to be invisible — the only failure log
    // here was at Debug, and the in-memory sink (InMemoryLogSink) plus the Windows Event Log both floor above
    // Debug. We now log the selection OUTCOME at Information/Warning, de-duplicated via _lastWgcSelectionStateKey
    // so this per-frame hot path never floods, while making "why WGC isn't serving" visible in both sinks.
    private void EnsureWgcSelectedForActiveTarget()
    {
        if (!WgcEnabled || _wgc is null)
        {
            return;
        }

        var deviceName = ActiveMonitorDeviceName();

        // No per-monitor Win32 device name resolved. Expected in virtual-desktop mode (WGC is per-monitor
        // only); a real fault in Monitor mode — the active display's name didn't resolve, so WGC can't target.
        if (deviceName is null)
        {
            if (_activeTarget.CaptureMode == DesktopCaptureMode.Monitor)
            {
                var key = $"unresolved:{_activeTarget.DisplayId}";
                if (_lastWgcSelectionStateKey != key)
                {
                    _lastWgcSelectionStateKey = key;
                    _logger.LogWarning(
                        "WGC not selected: no Win32 device name resolved for active monitor target (displayId={DisplayId}). Falling back to DXGI/GDI.",
                        _activeTarget.DisplayId ?? "<null>");
                }
            }
            return;
        }

        // Already targeting this monitor — steady state, nothing to do.
        if (string.Equals(_wgc.OutputDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_wgc.TrySelectMonitor(deviceName, out var error))
        {
            var key = $"ok:{deviceName}";
            if (_lastWgcSelectionStateKey != key)
            {
                _lastWgcSelectionStateKey = key;
                _logger.LogInformation("WGC now targeting {Device}.", deviceName);
            }
        }
        else
        {
            var key = $"fail:{deviceName}:{error}";
            if (_lastWgcSelectionStateKey != key)
            {
                _lastWgcSelectionStateKey = key;
                _logger.LogWarning(
                    "WGC could not target {Device}: {Error}. Falling back to DXGI/GDI.",
                    deviceName, error ?? "unknown error");
            }
        }
    }
    public bool IsDxgiAvailable => _dxgi.IsAvailable;
    public string? DxgiUnavailableReason => _dxgi.UnavailableReason;
    public string? LastCaptureFailureReason { get; private set; }

    public DesktopDisplayCatalog GetDisplayCatalog()
    {
        lock (_displayLock)
        {
            RefreshDisplays();
            return new DesktopDisplayCatalog
            {
                DisplayListVersion = _displayListVersion,
                SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop, DesktopCaptureMode.Monitor],
                Displays = _displays
                    .Select(display => new DesktopDisplayInfo
                    {
                        DisplayId = display.DisplayId,
                        PersistentDisplayKey = display.PersistentDisplayKey,
                        Name = display.Name,
                        IsPrimary = display.IsPrimary,
                        Left = display.Bounds.Left,
                        Top = display.Bounds.Top,
                        Width = display.Bounds.Width,
                        Height = display.Bounds.Height,
                    })
                    .ToArray(),
            };
        }
    }

    public bool TrySetCaptureTarget(DesktopCaptureTarget target, out string? error)
    {
        lock (_displayLock)
        {
            RefreshDisplays();

            if (target.CaptureMode == DesktopCaptureMode.VirtualDesktop)
            {
                _activeTarget = new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.VirtualDesktop };
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(target.DisplayId))
            {
                error = "A display ID is required for monitor capture.";
                return false;
            }

            var selectedDisplay = _displays.FirstOrDefault(display =>
                string.Equals(display.DisplayId, target.DisplayId, StringComparison.Ordinal));
            if (selectedDisplay is null)
            {
                error = $"Display '{target.DisplayId}' is no longer available.";
                return false;
            }

            _activeTarget = new DesktopCaptureTarget
            {
                CaptureMode = DesktopCaptureMode.Monitor,
                DisplayId = selectedDisplay.DisplayId,
            };

            error = null;
            return true;
        }
    }

    public Task<byte[]?> CaptureRawScreenAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => Task.FromResult(CaptureRawCore(scale, drawCursor, ct).Pixels);

    public Task<ScreenCaptureResult> CaptureRawScreenLiveAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => Task.FromResult(CaptureRawCore(scale, drawCursor, ct));

    private ScreenCaptureResult CaptureRawCore(double scale, bool drawCursor, CancellationToken ct)
    {
        scale = Math.Clamp(scale, 0.25, 1.0);
        ct.ThrowIfCancellationRequested();

        // Tier 1 — WGC (preferred, per-monitor). Driver-robust replacement for DXGI Desktop Duplication;
        // returns BGRA already scaled to CaptureScaling.ScaledEven so it matches the encoder's input size.
        if (WgcEnabled)
        {
            EnsureWgcSelectedForActiveTarget();
            if (WgcServesActiveTarget())
            {
                _wgc!.TryRecover();
                var wgcFrame = _wgc.TryCaptureRaw(scale, drawCursor, out var wgcLive);
                if (wgcFrame is { Length: > 0 })
                {
                    LastCaptureFailureReason = null;
                    return new ScreenCaptureResult(wgcFrame, wgcLive);
                }
            }
        }

        _dxgi.TryRecover();

        if (CanUseDxgiForCurrentTarget())
        {
            var dxgiFrame = _dxgi.TryCaptureRaw(scale, drawCursor, out var dxgiLive);
            if (dxgiFrame is { Length: > 0 })
            {
                LastCaptureFailureReason = null;
                // dxgiLive is false when DXGI replayed a stale cached frame (ACCESS_LOST etc.); the handler
                // uses it to stop counting a frozen output as a successful capture (RemEx-ltd).
                return new ScreenCaptureResult(dxgiFrame, dxgiLive);
            }
        }

        try
        {
            var bounds = GetActiveBounds();
            using var screenBitmap = CaptureBitmap(bounds, drawCursor);
            using var outputBitmap = ScaleBitmap(screenBitmap, scale);
            var bitmap = outputBitmap ?? screenBitmap;

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytesCount = Math.Abs(bmpData.Stride) * bitmap.Height;
                byte[] bgraValues = new byte[bytesCount];
                Marshal.Copy(bmpData.Scan0, bgraValues, 0, bytesCount);
                LastCaptureFailureReason = null;
                // GDI BitBlt produces a fresh frame whenever it succeeds.
                return new ScreenCaptureResult(bgraValues, isLive: true);
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }
        catch (Exception ex)
        {
            LastCaptureFailureReason = BuildCaptureFailureReason(ex.Message);
            if (ShouldLogCaptureError())
            {
                _logger.LogError(ex, "Failed to capture raw screen.");
            }
            return new ScreenCaptureResult(null, isLive: false);
        }
    }

    public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => Task.FromResult(CaptureScreenCore(quality, scale, drawCursor, ct).Pixels ?? Array.Empty<byte>());

    public Task<ScreenCaptureResult> CaptureScreenLiveAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => Task.FromResult(CaptureScreenCore(quality, scale, drawCursor, ct));

    private ScreenCaptureResult CaptureScreenCore(int quality, double scale, bool drawCursor, CancellationToken ct)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);
        ct.ThrowIfCancellationRequested();

        // Tier 1 — WGC (preferred). WGC delivers raw BGRA; JPEG-encode it for the MJPEG fallback path.
        if (WgcEnabled)
        {
            EnsureWgcSelectedForActiveTarget();
            if (WgcServesActiveTarget())
            {
                _wgc!.TryRecover();
                var wgcRaw = _wgc.TryCaptureRaw(scale, drawCursor, out var wgcLive);
                if (wgcRaw is { Length: > 0 })
                {
                    var jpeg = EncodeBgraToJpeg(
                        wgcRaw,
                        CaptureScaling.ScaledEven(_wgc.Width, scale),
                        CaptureScaling.ScaledEven(_wgc.Height, scale),
                        quality);
                    if (jpeg is { Length: > 0 })
                    {
                        LastCaptureFailureReason = null;
                        return new ScreenCaptureResult(jpeg, wgcLive);
                    }
                }
            }
        }

        _dxgi.TryRecover();

        if (CanUseDxgiForCurrentTarget())
        {
            var dxgiFrame = _dxgi.TryCapture(quality, scale, GetJpegEncoder(), drawCursor, out var dxgiLive);
            if (dxgiFrame is { Length: > 0 })
            {
                LastCaptureFailureReason = null;
                return new ScreenCaptureResult(dxgiFrame, dxgiLive);
            }
        }

        try
        {
            var bounds = GetActiveBounds();
            using var screenBitmap = CaptureBitmap(bounds, drawCursor);
            using var outputBitmap = ScaleBitmap(screenBitmap, scale);
            var bitmap = outputBitmap ?? screenBitmap;

            using var ms = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(ms, jpegEncoder, encoderParams);
            LastCaptureFailureReason = null;
            // GDI BitBlt produces a fresh frame whenever it succeeds.
            return new ScreenCaptureResult(ms.ToArray(), isLive: true);
        }
        catch (Exception ex)
        {
            LastCaptureFailureReason = BuildCaptureFailureReason(ex.Message);
            if (ShouldLogCaptureError())
            {
                _logger.LogError(ex,
                    "Failed to capture screen. Ensure the desktop is not locked and the process has screen access.");
            }
            return new ScreenCaptureResult(Array.Empty<byte>(), isLive: false);
        }
    }

    public void WarmUpCapture()
    {
        // DXGI is deferred until the first capture to avoid holding a Desktop Duplication
        // consumer slot while idle (RemEx-hmj). A client is now connecting, so initialize
        // it here so GetScreenSize() reports real hardware dims at bootstrap (RemEx-6my).
        _dxgi.TryRecover();

        // Prime the per-monitor backend too. WGC is what actually serves a single-monitor
        // target, and GraphicsCaptureItem.Size is populated the instant the monitor is selected
        // (before the first frame is pumped). Without this, GetScreenSize() at bootstrap falls
        // through to GetActiveBounds()/DXGI dims that can disagree with the WGC frame — mis-framing
        // the initial view and offsetting the cursor until a monitor switch forces re-selection
        // (RemEx-4k4). Selecting here makes the bootstrap dimensions match the streamed pixels.
        if (WgcEnabled)
        {
            EnsureWgcSelectedForActiveTarget();
        }
    }

    public (int Width, int Height, int Left, int Top) GetScreenSize()
    {
        // Report the dimensions of the backend that ACTUALLY serves the active target, so the size
        // Android uses to letterbox the video and map the cursor always matches the pixels it
        // receives (RemEx-4k4). Order mirrors the capture path: WGC (per-monitor) -> DXGI -> GDI.
        // Previously this ignored WGC and fell through to GetActiveBounds(), which on first connect
        // (before WGC had been selected) reported probe dims that raced capture initialization.
        if (WgcServesActiveTarget())
        {
            var (left, top) = ActiveMonitorOrigin();
            return (_wgc!.Width, _wgc.Height, left, top);
        }

        if (CanUseDxgiForCurrentTarget())
        {
            return (_dxgi.Width, _dxgi.Height, _dxgi.DesktopLeft, _dxgi.DesktopTop);
        }

        var bounds = GetActiveBounds();
        return (bounds.Width, bounds.Height, bounds.Left, bounds.Top);
    }

    // Virtual-desktop origin (left/top) of the active per-monitor target, resolved from the display
    // catalog. WGC reports the monitor SIZE (GraphicsCaptureItem.Size) but not its position; absolute
    // cursor mapping needs the origin so a monitor left of/above the primary maps to negative coords.
    private (int Left, int Top) ActiveMonitorOrigin()
    {
        lock (_displayLock)
        {
            var deviceName = _wgc?.OutputDeviceName;
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                var display = _displays.FirstOrDefault(candidate =>
                    string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                if (display is not null)
                {
                    return (display.Bounds.Left, display.Bounds.Top);
                }
            }

            var bounds = GetActiveBounds();
            return (bounds.Left, bounds.Top);
        }
    }

    internal bool TryCaptureCurrentCursorShape(out CursorShapeSnapshot? snapshot, bool forceRefresh = false)
    {
        snapshot = null;

        if (CanUseDxgiForCurrentTarget() && _dxgi.TryCaptureCurrentCursorShape(out snapshot, forceRefresh) && snapshot is not null)
        {
            return true;
        }

        return WindowsCursorShapeCapture.TryCaptureCurrentShape(out snapshot) && snapshot is not null;
    }

    public void Dispose()
    {
        _displayPower.Dispose();
        _dxgi.Dispose();
        _wgc?.Dispose();
    }

    private Rectangle GetActiveBounds()
    {
        lock (_displayLock)
        {
            RefreshDisplays();

            if (_activeTarget.CaptureMode == DesktopCaptureMode.Monitor &&
                !string.IsNullOrWhiteSpace(_activeTarget.DisplayId))
            {
                var display = _displays.FirstOrDefault(candidate =>
                    string.Equals(candidate.DisplayId, _activeTarget.DisplayId, StringComparison.Ordinal));
                if (display is not null)
                {
                    return display.Bounds;
                }
            }

            return GetVirtualDesktopBounds();
        }
    }

    private bool CanUseDxgiForCurrentTarget()
    {
        if (!_dxgi.IsAvailable || string.IsNullOrWhiteSpace(_dxgi.OutputDeviceName))
        {
            return false;
        }

        lock (_displayLock)
        {
            if (_activeTarget.CaptureMode != DesktopCaptureMode.Monitor || string.IsNullOrWhiteSpace(_activeTarget.DisplayId))
            {
                return false;
            }

            var display = _displays.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayId, _activeTarget.DisplayId, StringComparison.Ordinal));
            return display is not null &&
                   string.Equals(display.DeviceName, _dxgi.OutputDeviceName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private Bitmap CaptureBitmap(Rectangle bounds, bool drawCursor)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        if (drawCursor)
        {
            DrawCursorOnBitmap(graphics, bounds.Left, bounds.Top);
        }

        return bitmap;
    }

    private static Bitmap? ScaleBitmap(Bitmap sourceBitmap, double scale)
    {
        // Even-aligned target size — MUST match CaptureScaling.ScaledEven so the delivered frame size
        // equals what the H.264 encoder was started with. Returning null means "source is already the
        // correct size" (caller uses it as-is), which only holds when the even-aligned size == source.
        var captureWidth = CaptureScaling.ScaledEven(sourceBitmap.Width, scale);
        var captureHeight = CaptureScaling.ScaledEven(sourceBitmap.Height, scale);

        if (captureWidth == sourceBitmap.Width && captureHeight == sourceBitmap.Height)
        {
            return null;
        }

        var outputBitmap = new Bitmap(captureWidth, captureHeight);
        using var graphics = Graphics.FromImage(outputBitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        // SourceCopy so the alpha-0 GDI-drawn cursor pixels survive scaling instead of being dropped
        // by alpha blending (which makes the cursor invisible while the opaque desktop survives).
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImage(sourceBitmap, 0, 0, captureWidth, captureHeight);
        return outputBitmap;
    }

    private void RefreshDisplays()
    {
        var displays = EnumerateDisplays();
        _displays = displays
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.Name, StringComparer.Ordinal)
            .ToList();

        var version = new HashCode();
        foreach (var display in _displays)
        {
            version.Add(display.DisplayId, StringComparer.Ordinal);
            version.Add(display.Bounds.Left);
            version.Add(display.Bounds.Top);
            version.Add(display.Bounds.Width);
            version.Add(display.Bounds.Height);
            version.Add(display.IsPrimary);
        }

        _displayListVersion = _displays.Count == 0 ? 1 : version.ToHashCode();

        if (_activeTarget.CaptureMode == DesktopCaptureMode.Monitor &&
            !string.IsNullOrWhiteSpace(_activeTarget.DisplayId) &&
            _displays.All(display => !string.Equals(display.DisplayId, _activeTarget.DisplayId, StringComparison.Ordinal)))
        {
            var primaryDisplay = _displays.FirstOrDefault(static display => display.IsPrimary);
            _activeTarget = primaryDisplay is null
                ? new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.VirtualDesktop }
                : new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.Monitor, DisplayId = primaryDisplay.DisplayId };
        }
    }

    private static List<DisplayDefinition> EnumerateDisplays()
    {
        var displays = new List<DisplayDefinition>();
        var callback = new MonitorEnumProc((IntPtr monitorHandle, IntPtr _, ref RECT monitorRect, IntPtr __) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (!GetMonitorInfo(monitorHandle, ref info))
            {
                return true;
            }

            var bounds = Rectangle.FromLTRB(
                monitorRect.Left,
                monitorRect.Top,
                monitorRect.Right,
                monitorRect.Bottom);

            var deviceName = info.szDevice ?? string.Empty;
            var displayId = string.IsNullOrWhiteSpace(deviceName)
                ? $"monitor-{displays.Count + 1}"
                : deviceName.Trim();
            var name = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                ? $"Primary Display ({displayId})"
                : $"Display {displays.Count + 1} ({displayId})";

            displays.Add(new DisplayDefinition(
                displayId,
                displayId,
                deviceName,
                name,
                bounds,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));

            return true;
        });

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return displays;
    }

    private static Rectangle GetVirtualDesktopBounds()
        => new(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));

    // JPEG-encodes a tightly-packed BGRA buffer (as produced by the WGC backend) for the MJPEG path.
    // BGRA byte order matches Format32bppArgb's in-memory layout, and 32bpp has no row padding, so the
    // width*height*4 bytes copy directly into the locked bitmap. Returns null if the buffer is undersized.
    private static byte[]? EncodeBgraToJpeg(byte[] bgra, int width, int height, int quality)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return null;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bgra, 0, data.Scan0, width * height * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(ms, GetJpegEncoder(), encoderParams);
        return ms.ToArray();
    }

    private static ImageCodecInfo? _cachedJpegEncoder;

    private static ImageCodecInfo GetJpegEncoder()
    {
        if (_cachedJpegEncoder != null)
        {
            return _cachedJpegEncoder;
        }

        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.MimeType == "image/jpeg")
            {
                return _cachedJpegEncoder = codec;
            }
        }

        throw new InvalidOperationException("JPEG encoder not found.");
    }

    private static string BuildCaptureFailureReason(string message)
        => $"Screen capture failed ({message}). The desktop may be locked, showing a secure prompt, or denying screen capture access.";

    #region P/Invoke

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // ── Cursor drawing P/Invoke ───────────────────────────────────────────────

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Draws the system cursor at its current position onto the given Graphics surface.
    /// Uses GetCursorInfo + DrawIconEx for reliable cursor rendering including animated cursors.
    /// </summary>
    private static void DrawCursorOnBitmap(Graphics graphics, int captureLeft, int captureTop)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
        {
            return;
        }

        if (!GetIconInfo(ci.hCursor, out var iconInfo))
        {
            return;
        }

        var drawX = ci.ptScreenPos.X - captureLeft - iconInfo.xHotspot;
        var drawY = ci.ptScreenPos.Y - captureTop - iconInfo.yHotspot;

        if (iconInfo.hbmMask != IntPtr.Zero)
        {
            DeleteObject(iconInfo.hbmMask);
        }
        if (iconInfo.hbmColor != IntPtr.Zero)
        {
            DeleteObject(iconInfo.hbmColor);
        }

        var hdc = graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, drawX, drawY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    #endregion

    private sealed record DisplayDefinition(
        string DisplayId,
        string PersistentDisplayKey,
        string DeviceName,
        string Name,
        Rectangle Bounds,
        bool IsPrimary);
}
