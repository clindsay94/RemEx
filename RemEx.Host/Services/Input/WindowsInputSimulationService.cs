using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services.Input;

[SupportedOSPlatform("windows")]
public class WindowsInputSimulationService : IInputSimulationService
{
    private readonly ILogger<WindowsInputSimulationService> _logger;

    public WindowsInputSimulationService(ILogger<WindowsInputSimulationService> logger)
    {
        _logger = logger;
    }

    public string? BackendName => "sendinput";
    public string? LastInputFailureReason { get; private set; }

    public void MoveMouse(int x, int y)
    {
        // Map to the virtual desktop to support multiple monitors correctly.
        // Coordinate (0,0) in Win32 SendInput with VIRTUALDESK is the top-left of the entire desktop.
        // Normalized coordinates 0-65535 map across the virtual screen bounds.
        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // Normalize X and Y to the 0-65535 range across the virtual screen
        int absX = (int)(((x - vLeft) * 65535.0) / Math.Max(1, vWidth - 1));
        int absY = (int)(((y - vTop) * 65535.0) / Math.Max(1, vHeight - 1));

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                }
            }
        };
        SendOrThrow("absolute mouse move", input);
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        // Use relative mouse_event so delta movement isn't affected by monitor layout.
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = MOUSEEVENTF_MOVE,
                }
            }
        };
        SendOrThrow("relative mouse move", input);
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
        SendOrThrow("mouse button down", input);
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
        SendOrThrow("mouse button up", input);
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
            SendOrThrow("vertical mouse wheel", input);
        }

        if (deltaX != 0)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_HWHEEL, mouseData = deltaX } }
            };
            SendOrThrow("horizontal mouse wheel", input);
        }
    }

    public void KeyDown(int keyCode)
    {
        var virtualKey = MapKeyCodeToVirtualKey(keyCode);
        if (virtualKey == null)
        {
            _logger.LogDebug("KeyDown: Unmapped key code {KeyCode}", keyCode);
            return;
        }

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey.Value,
                    dwFlags = 0,
                }
            }
        };
        SendOrThrow("key down", input);
    }

    public void KeyUp(int keyCode)
    {
        var virtualKey = MapKeyCodeToVirtualKey(keyCode);
        if (virtualKey == null)
        {
            _logger.LogDebug("KeyUp: Unmapped key code {KeyCode}", keyCode);
            return;
        }

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey.Value,
                    dwFlags = KEYEVENTF_KEYUP,
                }
            }
        };
        SendOrThrow("key up", input);
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Each group is one code point's events: two for a BMP character, four for a surrogate
        // pair (both key-downs then both key-ups). Sending each group in a single SendInput batch
        // keeps the surrogate key-downs consecutive so Windows composes non-BMP code points (emoji)
        // correctly instead of garbling them. See UnicodeTextInput for the full rationale.
        foreach (var group in UnicodeTextInput.BuildKeyEventGroups(text))
        {
            var inputs = new INPUT[group.Length];
            for (int j = 0; j < group.Length; j++)
            {
                inputs[j] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wScan = group[j].ScanCode,
                            dwFlags = KEYEVENTF_UNICODE | (group[j].IsKeyUp ? KEYEVENTF_KEYUP : 0u),
                        }
                    }
                };
            }

            SendOrThrow("unicode text input", inputs);
        }
    }

    private void SendOrThrow(string operation, params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            LastInputFailureReason = null;
            return;
        }

        var error = Marshal.GetLastWin32Error();
        LastInputFailureReason = error != 0
            ? $"Windows SendInput failed during {operation} (Win32 {error}: {new Win32Exception(error).Message}). The desktop may be locked, showing a UAC/credential prompt, or Remex may not be running in the active user session."
            : $"Windows SendInput was blocked during {operation}. The desktop may be locked, showing a UAC/credential prompt, or Remex may not be running in the active user session.";

        _logger.LogWarning("Input injection failed during {Operation}: {Reason}", operation, LastInputFailureReason);

        if (error != 0)
        {
            throw new Win32Exception(error, LastInputFailureReason);
        }

        throw new InvalidOperationException(LastInputFailureReason);
    }

    #region P/Invoke constants and structs

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

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
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
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

    public (int X, int Y) GetCursorPosition()
    {
        if (GetCursorPos(out var point))
            return (point.X, point.Y);
        return (0, 0);
    }

    #endregion

    /// <summary>
    /// A local mirror of Avalonia.Input.Key values.
    /// Used so the Host project does not need to reference Avalonia just to map incoming integer key codes.
    /// </summary>
    private enum Key
    {
        Back = 2,
        Tab = 3,
        Enter = 6,
        Escape = 13,
        Space = 18,
        PageUp = 19,
        PageDown = 20,
        End = 21,
        Home = 22,
        Left = 23,
        Up = 24,
        Right = 25,
        Down = 26,
        Insert = 31,
        Delete = 32,
        D0 = 34,
        D1 = 35,
        D2 = 36,
        D3 = 37,
        D4 = 38,
        D5 = 39,
        D6 = 40,
        D7 = 41,
        D8 = 42,
        D9 = 43,
        A = 44,
        B = 45,
        C = 46,
        D = 47,
        E = 48,
        F = 49,
        G = 50,
        H = 51,
        I = 52,
        J = 53,
        K = 54,
        L = 55,
        M = 56,
        N = 57,
        O = 58,
        P = 59,
        Q = 60,
        R = 61,
        S = 62,
        T = 63,
        U = 64,
        V = 65,
        W = 66,
        X = 67,
        Y = 68,
        Z = 69,
        LWin = 70,
        RWin = 71,
        NumPad0 = 74,
        NumPad1 = 75,
        NumPad2 = 76,
        NumPad3 = 77,
        NumPad4 = 78,
        NumPad5 = 79,
        NumPad6 = 80,
        NumPad7 = 81,
        NumPad8 = 82,
        NumPad9 = 83,
        Multiply = 84,
        Add = 85,
        Separator = 86,
        Subtract = 87,
        Decimal = 88,
        Divide = 89,
        F1 = 90,
        F2 = 91,
        F3 = 92,
        F4 = 93,
        F5 = 94,
        F6 = 95,
        F7 = 96,
        F8 = 97,
        F9 = 98,
        F10 = 99,
        F11 = 100,
        F12 = 101,
        LeftShift = 114,
        RightShift = 115,
        LeftCtrl = 116,
        RightCtrl = 117,
        LeftAlt = 118,
        RightAlt = 119
    }

    /// <summary>
    /// Maps an incoming protocol-level key code to a Win32 virtual-key code suitable
    /// for KEYBDINPUT.wVk.
    ///
    /// Current RemEx clients send raw Win32-style virtual-key values, so prefer the
    /// 0-255 range first. Older protocol senders that used Avalonia key enum values
    /// can still fall back to the enum mapping below.
    /// </summary>
    internal static ushort? MapKeyCodeToVirtualKey(int keyCode)
    {
        if (keyCode >= 0 && keyCode <= 255)
        {
            return (ushort)keyCode;
        }

        if (keyCode >= 0)
        {
            var key = (Key)keyCode;
            switch (key)
            {
                // Digits
                case Key.D0: return 0x30; // '0'
                case Key.D1: return 0x31; // '1'
                case Key.D2: return 0x32; // '2'
                case Key.D3: return 0x33; // '3'
                case Key.D4: return 0x34; // '4'
                case Key.D5: return 0x35; // '5'
                case Key.D6: return 0x36; // '6'
                case Key.D7: return 0x37; // '7'
                case Key.D8: return 0x38; // '8'
                case Key.D9: return 0x39; // '9'

                // Letters
                case Key.A: return 0x41;
                case Key.B: return 0x42;
                case Key.C: return 0x43;
                case Key.D: return 0x44;
                case Key.E: return 0x45;
                case Key.F: return 0x46;
                case Key.G: return 0x47;
                case Key.H: return 0x48;
                case Key.I: return 0x49;
                case Key.J: return 0x4A;
                case Key.K: return 0x4B;
                case Key.L: return 0x4C;
                case Key.M: return 0x4D;
                case Key.N: return 0x4E;
                case Key.O: return 0x4F;
                case Key.P: return 0x50;
                case Key.Q: return 0x51;
                case Key.R: return 0x52;
                case Key.S: return 0x53;
                case Key.T: return 0x54;
                case Key.U: return 0x55;
                case Key.V: return 0x56;
                case Key.W: return 0x57;
                case Key.X: return 0x58;
                case Key.Y: return 0x59;
                case Key.Z: return 0x5A;

                // Function keys
                case Key.F1: return 0x70;
                case Key.F2: return 0x71;
                case Key.F3: return 0x72;
                case Key.F4: return 0x73;
                case Key.F5: return 0x74;
                case Key.F6: return 0x75;
                case Key.F7: return 0x76;
                case Key.F8: return 0x77;
                case Key.F9: return 0x78;
                case Key.F10: return 0x79;
                case Key.F11: return 0x7A;
                case Key.F12: return 0x7B;

                // Navigation keys
                case Key.Left: return 0x25;
                case Key.Up: return 0x26;
                case Key.Right: return 0x27;
                case Key.Down: return 0x28;
                case Key.Home: return 0x24;
                case Key.End: return 0x23;
                case Key.PageUp: return 0x21;
                case Key.PageDown: return 0x22;
                case Key.Insert: return 0x2D;
                case Key.Delete: return 0x2E;

                // System keys
                case Key.Escape: return 0x1B;
                case Key.Tab: return 0x09;
                case Key.Enter: return 0x0D;
                case Key.Space: return 0x20;
                case Key.Back: return 0x08;

                // Modifiers
                case Key.LeftShift: return 0xA0; // VK_LSHIFT
                case Key.RightShift: return 0xA1; // VK_RSHIFT
                case Key.LeftCtrl: return 0xA2; // VK_LCONTROL
                case Key.RightCtrl: return 0xA3; // VK_RCONTROL
                case Key.LeftAlt: return 0xA4; // VK_LMENU
                case Key.RightAlt: return 0xA5; // VK_RMENU
                case Key.LWin: return 0x5B; // VK_LWIN
                case Key.RWin: return 0x5C; // VK_RWIN

                // Numpad
                case Key.NumPad0: return 0x60;
                case Key.NumPad1: return 0x61;
                case Key.NumPad2: return 0x62;
                case Key.NumPad3: return 0x63;
                case Key.NumPad4: return 0x64;
                case Key.NumPad5: return 0x65;
                case Key.NumPad6: return 0x66;
                case Key.NumPad7: return 0x67;
                case Key.NumPad8: return 0x68;
                case Key.NumPad9: return 0x69;
                case Key.Multiply: return 0x6A;
                case Key.Add: return 0x6B;
                case Key.Separator: return 0x6C;
                case Key.Subtract: return 0x6D;
                case Key.Decimal: return 0x6E;
                case Key.Divide: return 0x6F;
            }
        }

        // Cannot map this key code.
        return null;
    }
}
