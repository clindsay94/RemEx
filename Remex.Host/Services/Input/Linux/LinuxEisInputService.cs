using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Remex.Host.Services.Input.Linux;

/// <summary>
/// P/Invoke surface for EIS sender functions in libremex_linux_bridge.so.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxNativeEis
{
    private const string LibName = "remex_linux_bridge";

    [DllImport(LibName, EntryPoint = "remex_eis_sender_create")]
    public static extern int SenderCreate(
        [MarshalAs(UnmanagedType.LPStr)] string eisSocketPath,
        out IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_eis_send_pointer_motion")]
    public static extern int SendPointerMotion(IntPtr handle, double dx, double dy);

    [DllImport(LibName, EntryPoint = "remex_eis_send_pointer_motion_absolute")]
    public static extern int SendPointerMotionAbsolute(IntPtr handle, double x, double y);

    [DllImport(LibName, EntryPoint = "remex_eis_send_button")]
    public static extern int SendButton(IntPtr handle, uint button, int pressed);

    [DllImport(LibName, EntryPoint = "remex_eis_send_scroll")]
    public static extern int SendScroll(IntPtr handle, double dx, double dy);

    [DllImport(LibName, EntryPoint = "remex_eis_send_key")]
    public static extern int SendKey(IntPtr handle, uint keycode, int pressed);

    [DllImport(LibName, EntryPoint = "remex_eis_sender_destroy")]
    public static extern void SenderDestroy(IntPtr handle);
}

/// <summary>
/// Managed wrapper for libei EIS sender input injection.
/// Preferred Wayland input path when libei is available and an EIS socket
/// is provided by the portal RemoteDesktop session.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxEisInputService : IDisposable
{
    private readonly ILogger<LinuxEisInputService> _logger;
    private IntPtr _handle = IntPtr.Zero;
    private bool _available;
    private bool _disposed;

    public bool IsAvailable => _available;

    public LinuxEisInputService(ILogger<LinuxEisInputService>? logger = null)
    {
        _logger = logger ?? NullLogger<LinuxEisInputService>.Instance;
    }

    public bool TryOpen(string eisSocketPath)
    {
        if (_disposed) return false;
        try
        {
            int rc = LinuxNativeEis.SenderCreate(eisSocketPath, out _handle);
            if (rc != 0)
            {
                _logger.LogWarning("remex_eis_sender_create returned {Code}.", rc);
                return false;
            }
            _available = true;
            _logger.LogInformation("EIS sender opened on {Socket}.", eisSocketPath);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex, "libremex_linux_bridge.so not found; EIS unavailable.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open EIS sender.");
            return false;
        }
    }

    public void SendPointerMotion(double dx, double dy)
    {
        if (!_available) return;
        LinuxNativeEis.SendPointerMotion(_handle, dx, dy);
    }

    public void SendPointerMotionAbsolute(double x, double y)
    {
        if (!_available) return;
        LinuxNativeEis.SendPointerMotionAbsolute(_handle, x, y);
    }

    public void SendButton(uint button, bool pressed)
    {
        if (!_available) return;
        LinuxNativeEis.SendButton(_handle, button, pressed ? 1 : 0);
    }

    public void SendScroll(double dx, double dy)
    {
        if (!_available) return;
        LinuxNativeEis.SendScroll(_handle, dx, dy);
    }

    public void SendKey(uint keycode, bool pressed)
    {
        if (!_available) return;
        LinuxNativeEis.SendKey(_handle, keycode, pressed ? 1 : 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { LinuxNativeEis.SenderDestroy(_handle); }
            catch { /* best-effort */ }
            _handle = IntPtr.Zero;
        }
        _available = false;
    }
}
