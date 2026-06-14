using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Host.Services.RemoteDesktop.Linux;

namespace Remex.Host.Services.Input.Linux;

/// <summary>
/// P/Invoke surface for uinput tablet functions in libremex_linux_bridge.so.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxNativeUinput
{
    private const string LibName = "remex_linux_bridge";

    static LinuxNativeUinput()
    {
        LinuxNativeBridgeLocator.EnsureDllImportResolverRegistered();
    }

    [DllImport(LibName, EntryPoint = "remex_uinput_tablet_create")]
    public static extern int TabletCreate(
        [MarshalAs(UnmanagedType.LPStr)] string deviceName,
        int supportsPressure,
        int supportsTilt,
        int supportsDistance,
        out IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_uinput_tablet_send_stylus_event")]
    public static extern int TabletSendStylusEvent(
        IntPtr handle,
        int absX, int absY,
        int pressure, int tiltX, int tiltY, int distance,
        uint buttonMask, int toolPen, int toolRubber);

    [DllImport(LibName, EntryPoint = "remex_uinput_tablet_reset")]
    public static extern int TabletReset(IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_uinput_tablet_destroy")]
    public static extern void TabletDestroy(IntPtr handle);
}

/// <summary>
/// Managed wrapper for the uinput virtual tablet device.
/// Provides stylus/pen event injection via /dev/uinput.
///
/// Coordinate system:
///   Incoming normalized coordinates (0.0–1.0) from the Android S-Pen are
///   scaled to the evdev ABS range (0–65535) before sending to the kernel.
///
/// Axis mapping from Android MotionEvent to evdev:
///   X position   → ABS_X
///   Y position   → ABS_Y
///   Pressure      → ABS_PRESSURE (0–1 → 0–65535)
///   Tilt X/Y      → ABS_TILT_X/Y (-64 to +63 degrees)
///   Distance      → ABS_DISTANCE (0–1 → 0–65535)
///   Eraser flag   → BTN_TOOL_RUBBER / BTN_TOOL_PEN
///   Button clicks → BTN_STYLUS / BTN_STYLUS2
///   Contact       → BTN_TOUCH
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxUinputTabletService : IDisposable
{
    private const int AbsMax = 65535;
    private const int TiltMin = -64;
    private const int TiltMax = 63;

    private readonly ILogger<LinuxUinputTabletService> _logger;
    private IntPtr _handle = IntPtr.Zero;
    private bool _available;
    private bool _disposed;

    public bool IsAvailable => _available;

    public LinuxUinputTabletService(ILogger<LinuxUinputTabletService>? logger = null)
    {
        _logger = logger ?? NullLogger<LinuxUinputTabletService>.Instance;
    }

    public bool TryCreate(string deviceName = "RemEx Virtual Tablet")
    {
        if (_disposed) return false;
        try
        {
            int rc = LinuxNativeUinput.TabletCreate(
                deviceName,
                supportsPressure: 1,
                supportsTilt: 1,
                supportsDistance: 1,
                out _handle);

            if (rc != 0)
            {
                _logger.LogWarning("remex_uinput_tablet_create returned {Code}.", rc);
                return false;
            }
            _available = true;
            _logger.LogInformation("uinput virtual tablet '{Name}' created.", deviceName);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex, "libremex_linux_bridge.so not found; uinput tablet unavailable.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create uinput tablet device.");
            return false;
        }
    }

    /// <summary>
    /// Sends a complete stylus frame. All coordinate inputs are normalized 0.0–1.0.
    /// </summary>
    public void SendStylusFrame(
        double normX, double normY,
        double pressure,
        double tiltX, double tiltY,
        double distance,
        bool touch,
        bool button1, bool button2,
        bool isEraser)
    {
        if (!_available) return;

        int absX = (int)(normX * AbsMax);
        int absY = (int)(normY * AbsMax);
        int press = (int)(pressure * AbsMax);
        int tx = (int)(tiltX * (TiltMax - TiltMin) + TiltMin);
        int ty = (int)(tiltY * (TiltMax - TiltMin) + TiltMin);
        int dist = (int)(distance * AbsMax);

        uint buttonMask = 0;
        if (touch) buttonMask |= 0x01;
        if (button1) buttonMask |= 0x02;
        if (button2) buttonMask |= 0x04;

        LinuxNativeUinput.TabletSendStylusEvent(
            _handle,
            absX: absX, absY: absY,
            pressure: press, tiltX: tx, tiltY: ty, distance: dist,
            buttonMask: buttonMask,
            toolPen: isEraser ? 0 : 1,
            toolRubber: isEraser ? 1 : 0);
    }

    /// <summary>
    /// Releases all button/tool state. Call on disconnect or session teardown
    /// to prevent stuck stylus state.
    /// </summary>
    public void Reset()
    {
        if (!_available) return;
        try { LinuxNativeUinput.TabletReset(_handle); }
        catch (Exception ex) { _logger.LogWarning(ex, "uinput tablet reset failed."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { LinuxNativeUinput.TabletDestroy(_handle); }
            catch { /* best-effort */ }
            _handle = IntPtr.Zero;
        }
        _available = false;
    }
}
