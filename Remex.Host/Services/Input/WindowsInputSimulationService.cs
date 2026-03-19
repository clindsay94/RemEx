using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.Input;

[SupportedOSPlatform("windows")]
public class WindowsInputSimulationService : IInputSimulationService
{
    private readonly ILogger<WindowsInputSimulationService> _logger;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    public WindowsInputSimulationService(ILogger<WindowsInputSimulationService> logger)
    {
        _logger = logger;
        _screenWidth = GetSystemMetrics(SM_CXSCREEN);
        _screenHeight = GetSystemMetrics(SM_CYSCREEN);
    }

    public void MoveMouse(int x, int y)
    {
        x = Math.Clamp(x, 0, _screenWidth - 1);
        y = Math.Clamp(y, 0, _screenHeight - 1);

        // Convert to absolute coordinates (0-65535 range)
        int absX = (int)((x * 65535.0) / (_screenWidth - 1));
        int absY = (int)((y * 65535.0) / (_screenHeight - 1));

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        GetCursorPos(out var pt);
        MoveMouse(pt.X + dx, pt.Y + dy);
    }

    public void MouseDown(int button)
    {
        uint flag = button switch
        {
            0 => MOUSEEVENTF_LEFTDOWN,
            1 => MOUSEEVENTF_MIDDLEDOWN,
            2 => MOUSEEVENTF_RIGHTDOWN,
            _ => MOUSEEVENTF_LEFTDOWN
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void MouseUp(int button)
    {
        uint flag = button switch
        {
            0 => MOUSEEVENTF_LEFTUP,
            1 => MOUSEEVENTF_MIDDLEUP,
            2 => MOUSEEVENTF_RIGHTUP,
            _ => MOUSEEVENTF_LEFTUP
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void MouseClick(int button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (deltaY != 0)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = deltaY } }
            };
            SendInput(1, [input], Marshal.SizeOf<INPUT>());
        }

        if (deltaX != 0)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_HWHEEL, mouseData = deltaX } }
            };
            SendInput(1, [input], Marshal.SizeOf<INPUT>());
        }
    }

    public void KeyDown(int keyCode)
    {
        if (keyCode < 0 || keyCode > 255) return;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)keyCode,
                    dwFlags = 0,
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void KeyUp(int keyCode)
    {
        if (keyCode < 0 || keyCode > 255) return;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)keyCode,
                    dwFlags = KEYEVENTF_KEYUP,
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (char c in text)
        {
            var downInput = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                    }
                }
            };
            var upInput = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    }
                }
            };
            SendInput(2, [downInput, upInput], Marshal.SizeOf<INPUT>());
        }
    }

    #region P/Invoke constants and structs

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    #endregion
}
