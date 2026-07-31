using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Remex.Core.Models;

namespace Remex.Agent.Services.Input;

/// <summary>
/// Win32 desktop window control — the Windows counterpart to <c>LinuxDesktopWindowControlService</c>,
/// so advanced window control (list / activate / raise / minimize / close / resize) works the same on
/// both platforms. Virtual-desktop moves are not supported (the Windows virtual-desktop COM API is
/// undocumented); that action returns a clear, non-fatal error.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopWindowControlService : IDesktopWindowControlService
{
    private const string BackendName = "win32";

    public DesktopWindowResult QueryWindows(DesktopWindowQuery query)
    {
        try
        {
            return new DesktopWindowResult
            {
                RequestId = query.RequestId,
                Success = true,
                Backend = BackendName,
                Windows = EnumerateWindows(query),
            };
        }
        catch (Exception ex)
        {
            return new DesktopWindowResult
            {
                RequestId = query.RequestId,
                Success = false,
                Backend = BackendName,
                ErrorText = ex.Message,
            };
        }
    }

    public DesktopWindowResult ExecuteAction(DesktopWindowAction action)
    {
        try
        {
            if (action.Action == DesktopWindowActionTypes.MoveToDesktop)
            {
                return Fail(action, "Moving windows between virtual desktops is not supported on Windows.");
            }

            if (!TryParseHandle(action.WindowId, out var hwnd))
            {
                return Fail(action, "A valid windowId is required for this action.");
            }

            bool ok;
            switch (action.Action)
            {
                case DesktopWindowActionTypes.Activate:
                case DesktopWindowActionTypes.Raise:
                    ShowWindow(hwnd, SW_RESTORE);
                    ok = SetForegroundWindow(hwnd);
                    break;

                case DesktopWindowActionTypes.Minimize:
                    ok = ShowWindow(hwnd, SW_MINIMIZE);
                    break;

                case DesktopWindowActionTypes.Close:
                    ok = PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    break;

                case DesktopWindowActionTypes.Resize:
                    if (action.Width is not int width || action.Height is not int height || width <= 0 || height <= 0)
                    {
                        return Fail(action, "Resize requires positive width and height.");
                    }

                    ok = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER);
                    break;

                default:
                    return Fail(action, $"Unsupported action '{action.Action}'.");
            }

            return new DesktopWindowResult
            {
                RequestId = action.RequestId,
                Action = action.Action,
                Success = ok,
                Backend = BackendName,
                ErrorText = ok ? null : "The window manager rejected the action.",
            };
        }
        catch (Exception ex)
        {
            return Fail(action, ex.Message);
        }
    }

    private static DesktopWindowResult Fail(DesktopWindowAction action, string error) => new()
    {
        RequestId = action.RequestId,
        Action = action.Action,
        Success = false,
        Backend = BackendName,
        ErrorText = error,
    };

    private static List<DesktopWindowInfo> EnumerateWindows(DesktopWindowQuery query)
    {
        var foreground = GetForegroundWindow();
        var search = query.SearchText ?? string.Empty;
        var results = new List<DesktopWindowInfo>();

        // EnumWindows is synchronous, so the delegate stays alive for the duration of the call.
        EnumWindows((hwnd, _) =>
        {
            if (results.Count >= query.Limit)
            {
                return false; // stop enumerating
            }

            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var titleLength = GetWindowTextLength(hwnd);
            if (titleLength == 0)
            {
                return true; // skip windows without a title (tool windows, etc.)
            }

            var titleBuffer = new StringBuilder(titleLength + 1);
            GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
            var title = titleBuffer.ToString();

            if (search.Length > 0 && title.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            var classBuffer = new StringBuilder(256);
            GetClassName(hwnd, classBuffer, classBuffer.Capacity);

            GetWindowThreadProcessId(hwnd, out var pid);
            GetWindowRect(hwnd, out var rect);

            results.Add(new DesktopWindowInfo
            {
                Id = hwnd.ToInt64().ToString(),
                Title = title,
                ClassName = classBuffer.ToString(),
                ProcessId = pid == 0 ? null : pid,
                X = rect.Left,
                Y = rect.Top,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top,
                IsActive = hwnd == foreground,
            });

            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static bool TryParseHandle(string? windowId, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(windowId) || !long.TryParse(windowId, out var raw))
        {
            return false;
        }

        hwnd = new IntPtr(raw);
        return hwnd != IntPtr.Zero;
    }

    // ── Win32 interop ──────────────────────────────────────────────────────────
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
