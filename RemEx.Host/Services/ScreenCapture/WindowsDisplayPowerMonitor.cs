using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.ScreenCapture;

/// <summary>
/// Tracks the console display power state (monitor on / off / dimmed) so the capture loop can pause
/// entirely while the display is powered off, rather than poking a powered-off Desktop Duplication
/// output. This is defense-in-depth on top of <see cref="DuplicationReinitThrottle"/> (RemEx-crk): the
/// throttle bounds re-init to a few attempts per backoff window; this drives that to zero while the
/// monitor is asleep (RemEx-960).
///
/// <para>
/// Windows delivers display-power changes only as the <c>GUID_CONSOLE_DISPLAY_STATE</c> power-setting
/// notification, which requires a window (or service) handle plus a running message pump. We host a
/// tiny message-only window (<c>HWND_MESSAGE</c>) on a dedicated background thread with its own pump —
/// self-contained, independent of the Avalonia dispatcher, and valid in the interactive session where
/// capture runs. Registration delivers an immediate notification with the current state, so the field
/// converges within milliseconds of construction.
/// </para>
///
/// <para>
/// Fails open: if the window/notification cannot be created, <see cref="IsDisplayOff"/> stays
/// <c>false</c> and capture behaves exactly as before (the feature simply no-ops).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDisplayPowerMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly Thread _pumpThread;
    private readonly ManualResetEventSlim _initialized = new(false);

    // Kept alive for the lifetime of the window: the native side holds a raw function pointer to this
    // delegate; letting it be collected would crash the message pump.
    private readonly WndProcDelegate _wndProc;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _powerNotify = IntPtr.Zero;
    private volatile bool _displayOff;
    private volatile bool _disposed;

    public WindowsDisplayPowerMonitor(ILogger logger)
    {
        _logger = logger;
        _wndProc = WndProcImpl;
        _pumpThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "RemEx-DisplayPowerMonitor",
        };
        _pumpThread.Start();
        // Bounded wait for the window + notification registration so a construction failure is logged
        // promptly; the pump keeps running on its own thread regardless.
        _initialized.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>True while the console display is powered off (monitor sleep). Dimmed counts as on.</summary>
    public bool IsDisplayOff => _displayOff;

    private void RunMessageLoop()
    {
        try
        {
            CreateMessageWindow();
            RegisterForDisplayPowerNotifications();
        }
        catch (Exception ex)
        {
            // Fail open: leave _displayOff false so capture is never paused on our account.
            _logger.LogWarning(ex, "Display power monitor unavailable; capture will not pause on display-off.");
            _initialized.Set();
            return;
        }
        finally
        {
            _initialized.Set();
        }

        // Standard message pump. GetMessage returns 0 on WM_QUIT (posted from WM_DESTROY on shutdown)
        // and -1 on error; either ends the loop.
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        CleanupWindow();
    }

    private void CreateMessageWindow()
    {
        var hInstance = GetModuleHandleW(null);
        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };

        // RegisterClassEx returns 0 (and sets last error) on failure. A duplicate-class error is fine
        // (e.g. a previous instance in the same process), so only a true zero with no usable class fails.
        var atom = RegisterClassExW(ref wndClass);
        if (atom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // ERROR_CLASS_ALREADY_EXISTS (1410) is benign — reuse the existing class.
            if (err != ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException($"RegisterClassEx failed (Win32 {err}).");
            }
        }

        _hwnd = CreateWindowExW(
            0, WindowClassName, "RemEx Display Power Monitor", 0,
            0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed (Win32 {Marshal.GetLastWin32Error()}).");
        }
    }

    private void RegisterForDisplayPowerNotifications()
    {
        var guid = GUID_CONSOLE_DISPLAY_STATE;
        _powerNotify = RegisterPowerSettingNotification(_hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_powerNotify == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"RegisterPowerSettingNotification failed (Win32 {Marshal.GetLastWin32Error()}).");
        }

        _logger.LogInformation("Display power monitor active (console display-state notifications registered).");
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_POWERBROADCAST:
                if ((int)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
                {
                    var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                    if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                    {
                        // Data: 0 = off, 1 = on, 2 = dimmed. Only a fully-off display should pause capture.
                        var wasOff = _displayOff;
                        _displayOff = setting.Data == DisplayStateOff;
                        if (_displayOff != wasOff)
                        {
                            _logger.LogInformation(
                                "Console display power state: {State}.",
                                setting.Data switch
                                {
                                    DisplayStateOff => "off (capture paused)",
                                    DisplayStateOn => "on (capture resumed)",
                                    DisplayStateDimmed => "dimmed",
                                    _ => $"unknown ({setting.Data})",
                                });
                        }
                    }
                }

                return (IntPtr)1; // TRUE

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void CleanupWindow()
    {
        if (_powerNotify != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_powerNotify);
            _powerNotify = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            // Already inside the pump thread after the loop exits; destroy is safe here.
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClassW(WindowClassName, GetModuleHandleW(null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ask the pump thread to tear down its own window (WM_CLOSE → WM_DESTROY → PostQuitMessage),
        // so window creation and destruction stay on the one thread that owns the message queue.
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        if (!_pumpThread.Join(TimeSpan.FromSeconds(2)))
        {
            _logger.LogDebug("Display power monitor pump thread did not exit within the timeout.");
        }

        _initialized.Dispose();
    }

    // ── Constants ─────────────────────────────────────────────────────────────
    private const string WindowClassName = "RemExDisplayPowerMonitorWnd";
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // GUID_CONSOLE_DISPLAY_STATE {6FE69556-704A-47A0-8F24-C28D936FDA47}
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
        new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private const byte DisplayStateOff = 0;
    private const byte DisplayStateOn = 1;
    private const byte DisplayStateDimmed = 2;

    // ── Structs ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // first byte of the variable-length Data[]; one byte for display state
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
