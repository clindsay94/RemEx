using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// <c>win32.lastinput</c> (routines spec §8.5.2): <c>GetLastInputInfo</c> against <c>GetTickCount64</c>.
/// </summary>
/// <remarks>
/// Valid because the agent runs inside the user's session. Input injected by a Remote Desktop stream resets
/// it, which is what "idle" should mean for a PC someone is driving from their phone.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class Win32LastInputIdleSource : IIdleSource
{
    public const string SourceId = "win32.lastinput";

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public Task<TimeSpan?> GetIdleAsync(CancellationToken ct)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return Task.FromResult<TimeSpan?>(null);
        }

        // dwTime is a 32-bit tick count; compare in the same 32-bit space so the 49.7-day wrap of the
        // low word cannot produce a negative or enormous idle time.
        var nowLow = unchecked((uint)GetTickCount64());
        var idleMs = unchecked(nowLow - info.Time);
        return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(idleMs));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}

/// <summary>
/// <c>win32.wts</c> (routines spec §8.5.3): lock and unlock from <c>WM_WTSSESSION_CHANGE</c>, delivered to a
/// message-only window this source creates and pumps on its own thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE SOURCE OWNS ITS OWN MESSAGE-ONLY WINDOW (docs/REGRESSION-GUARDS.md).</b> The Avalonia main
/// window may never be constructed at a <c>--minimized</c> logon start, and a hook on it would register
/// nothing; the tray-only start is exactly when "lock → sleep" routines matter. So this creates an
/// <c>HWND_MESSAGE</c> window on a dedicated background thread, registers it with
/// <c>WTSRegisterSessionNotification(NOTIFY_FOR_THIS_SESSION)</c> and runs a <c>GetMessage</c> loop there.
/// </para>
/// <para>
/// The initial state comes from <c>WTSQuerySessionInformation(WTSSessionInfoEx)</c>, so a routine armed
/// while the PC is already locked knows it (an initial reading is never an edge).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WtsSessionStateSource : ISessionStateSource
{
    public const string SourceId = "win32.wts";

    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const uint NotifyForThisSession = 0;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly ILogger _logger;
    private readonly WndProc _wndProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private volatile object? _locked;

    public WtsSessionStateSource(ILogger<WtsSessionStateSource> logger)
    {
        _logger = logger;
        // Held in a field for the life of the window: a collected delegate behind a registered window
        // procedure crashes the process on the next message.
        _wndProc = WindowProcedure;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public bool? IsLocked => _locked as bool?;

    /// <inheritdoc />
    public event Action<bool>? Changed;

    /// <summary>True while the pump thread owns a live window. For tests.</summary>
    public bool HasWindow => _hwnd != IntPtr.Zero;

    /// <inheritdoc />
    public Task<bool> StartAsync(CancellationToken ct)
    {
        if (_thread is not null)
        {
            return Task.FromResult(_hwnd != IntPtr.Zero);
        }

        _locked = QueryInitialLocked();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Pump(ready))
        {
            IsBackground = true,
            Name = "RemEx routines session source",
        };
        _thread.Start();
        return ready.Task;
    }

    public void Dispose()
    {
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Pump(TaskCompletionSource<bool> ready)
    {
        (string Name, IntPtr Instance)? classToUnregister = null;
        try
        {
            // ONE WINDOW CLASS PER SOURCE, NEVER SHARED. A class carries the window procedure of whoever
            // registered it: a second source reusing a per-process name would get the FIRST source's
            // delegate, and once that source was collected every message would call into freed memory
            // (measured: "callback on a garbage collected delegate", test host terminated). A unique name
            // registered and unregistered on this thread ties the procedure to this instance's lifetime.
            var className = "RemExRoutineSession_" + Guid.NewGuid().ToString("N");
            var instance = GetModuleHandleW(null);
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = instance,
                ClassName = className,
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                _logger.LogWarning("Could not register the routines session window class (error {Error}).", Marshal.GetLastWin32Error());
                ready.TrySetResult(false);
                return;
            }

            classToUnregister = (className, instance);
            var hwnd = CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("Could not create the routines session window (error {Error}).", Marshal.GetLastWin32Error());
                ready.TrySetResult(false);
                return;
            }

            if (!WTSRegisterSessionNotification(hwnd, NotifyForThisSession))
            {
                _logger.LogWarning("WTSRegisterSessionNotification failed (error {Error}).", Marshal.GetLastWin32Error());
                DestroyWindow(hwnd);
                ready.TrySetResult(false);
                return;
            }

            _hwnd = hwnd;
            ready.TrySetResult(true);

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The routines session window thread failed.");
            ready.TrySetResult(false);
        }
        finally
        {
            _hwnd = IntPtr.Zero;
            if (classToUnregister is { } registered)
            {
                UnregisterClassW(registered.Name, registered.Instance);
            }

            // The procedure must outlive every message the window could still receive.
            GC.KeepAlive(_wndProc);
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmWtsSessionChange:
                var code = wParam.ToInt32();
                if (code is WtsSessionLock or WtsSessionUnlock)
                {
                    var locked = code == WtsSessionLock;
                    _locked = locked;
                    try
                    {
                        Changed?.Invoke(locked);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "A session edge handler failed.");
                    }
                }

                return IntPtr.Zero;

            case WmClose:
                WTSUnRegisterSessionNotification(hwnd);
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>The session's lock state from <c>WTSINFOEX_LEVEL1.SessionFlags</c>, or null when unknown.</summary>
    private bool? QueryInitialLocked()
    {
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsSessionInfoEx, out var buffer, out var bytes)
                || buffer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // WTSINFOEXW { DWORD Level; WTSINFOEX_LEVEL1_W { ULONG SessionId; WTS_CONNECTSTATE_CLASS
                // SessionState; LONG SessionFlags; ... } }: SessionFlags sits at byte 12.
                if (bytes < 16)
                {
                    return null;
                }

                var flags = Marshal.ReadInt32(buffer, 12);
                return flags switch
                {
                    0 => true,   // WTS_SESSIONSTATE_LOCK
                    1 => false,  // WTS_SESSIONSTATE_UNLOCK
                    _ => null,
                };
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "WTSQuerySessionInformation unavailable.");
            return null;
        }
    }

    private const uint WtsCurrentSession = 0xFFFFFFFF;
    private const int WtsSessionInfoEx = 25;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
