using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Remex.Desktop.Services;

/// <summary>
/// Applies Windows 11's Mica system backdrop directly through DWM (RemEx-rq0xl, re-probe of
/// RemEx-z94c7). On Windows 11 26200 with Avalonia 12.1.1, <c>TransparencyLevelHint = Mica</c>
/// makes <c>ActualTransparencyLevel</c> report Mica, but Avalonia never sets
/// <c>DWMWA_SYSTEMBACKDROP_TYPE</c> (it reads back 0 — the legacy <c>DWMWA_MICA_EFFECT</c>, 1029,
/// no longer exists on this OS) and paints its own flat layer instead. Real Mica renders only when
/// the window asks for <see cref="Avalonia.Controls.WindowTransparencyLevel.Transparent"/> and this
/// class calls <c>DwmSetWindowAttribute</c> itself once the window has a handle — the mica-spike
/// (task 1, 2026-09-16) measured a wallpaper-derived <c>#23151A</c> tint against a flat <c>#111418</c>
/// base doing exactly that.
/// </summary>
internal static class MicaBackdrop
{
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_AUTO = 0;
    private const int DWMSBT_MAINWINDOW = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int value, int size);

    /// <summary>
    /// True on Windows 11 22H2 (build 22621) and later — the first build where DWM honours
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE</c>. Below this build the attribute either fails or is a no-op.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>What an apply in Mica has to ask DWM for, given what last landed.</summary>
    internal enum Request
    {
        /// <summary>The backdrop already landed with this light/dark answer: no DWM call.</summary>
        None,

        /// <summary>First request since Mica was selected (or none has landed yet).</summary>
        Apply,

        /// <summary>
        /// The light/dark answer flipped while Mica stayed selected: clear the backdrop, then
        /// request it again (live-check B4).
        /// </summary>
        Reapply,
    }

    /// <summary>
    /// Decides the DWM work for one apply. <paramref name="appliedDark"/> is the light/dark answer
    /// the backdrop last landed with, or null when it has not landed since Mica was selected.
    /// </summary>
    /// <remarks>
    /// A THEME FLIP IS A CLEAR PLUS A FRESH REQUEST, NOT A SECOND IDENTICAL REQUEST (live-check B4).
    /// After perf P3-68 made the DWM call conditional, a Windows dark→light→dark flip issued exactly
    /// one <see cref="TryApply"/> per flip, writing <c>DWMWA_SYSTEMBACKDROP_TYPE = MAINWINDOW</c> over
    /// an attribute that already held MAINWINDOW, and Mica did not come back. Mica→Acrylic→Mica, which
    /// did repaint, goes through <see cref="Clear"/> first — a real AUTO→MAINWINDOW transition. A
    /// flip now takes that same path. Unchanged state still costs nothing, which is what P3-68 is for.
    /// </remarks>
    internal static Request Plan(bool? appliedDark, bool dark) =>
        appliedDark is null ? Request.Apply
        : appliedDark.Value == dark ? Request.None
        : Request.Reapply;

    /// <summary>
    /// Sets the immersive-dark-mode flag (so the caption/frame matches <paramref name="dark"/>),
    /// then requests <c>DWMSBT_MAINWINDOW</c>. Returns true only when the backdrop call itself
    /// succeeds (S_OK); returns false — it never throws — when the window has no platform handle
    /// yet or <see cref="IsSupported"/> is false, so a caller can simply retry once the handle
    /// exists (e.g. from <c>Window.Opened</c>).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool TryApply(Window window, bool dark)
    {
        if (!IsSupported) return false;

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return false;

        var darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

        var backdrop = DWMSBT_MAINWINDOW;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        return hr == 0;
    }

    /// <summary>
    /// Resets the backdrop to <c>DWMSBT_AUTO</c> — called once when a window leaves Mica for
    /// another material, so the DWM backdrop does not linger underneath Acrylic's own compositor
    /// request or Solid's opaque fill.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Clear(Window window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var backdrop = DWMSBT_AUTO;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }
}
