using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Remex.Desktop.Views;

/// <summary>
/// Asks DWM to round an undecorated window's corners on Windows 11.
/// </summary>
/// <remarks>
/// Belt-and-braces for the OS-drawn edge. The visible rounding comes from the inner
/// <c>Border.CornerRadius</c>; this stops Windows compositing a square edge behind it.
/// <para>
/// Fails silently and deliberately. The attribute does not exist before Windows 11 build 22000, so
/// <c>DwmSetWindowAttribute</c> returns a non-zero HRESULT there — which is the expected outcome on
/// Windows 10, not an error worth logging on every window creation.
/// </para>
/// </remarks>
internal static class TrayWindowCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [SupportedOSPlatform("windows")]
    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    internal static void ApplyRounded(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == nint.Zero)
            return;

        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
