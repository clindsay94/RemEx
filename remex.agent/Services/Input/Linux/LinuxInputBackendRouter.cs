using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Agent.Services.RemoteDesktop.Linux;

using Remex.Agent.Services.Input;

namespace Remex.Agent.Services.Input.Linux;

/// <summary>
/// Routes Linux input events to the best available backend based on the
/// runtime capability set:
///
///   Tier          | Backend priority
///   --------------|--------------------------------------------------------------
///   WaylandNative | libei (EIS) → xdotool fallback
///   PortalNoPen   | portal-notify (keyboard + pointer, no pen)
///   X11Degraded   | xdotool / legacy shell tool
///   Unsupported   | no-op
///
/// Pen/stylus events from <see cref="EnqueuePointerSample"/> are routed to the
/// uinput tablet whenever it is available, regardless of tier.
///
/// This class owns the EIS and uinput sub-services and manages their lifecycle.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxInputBackendRouter : IInputSimulationService, IDisposable
{
    private readonly ILogger<LinuxInputBackendRouter> _logger;
    private readonly LinuxInputCapabilitySet _capabilities;
    private readonly LinuxEisInputService _eis;
    private readonly LinuxUinputTabletService _uinputTablet;

    private bool _disposed;

    public string? BackendName => _capabilities.Tier switch
    {
        LinuxRemoteDesktopTier.WaylandNative =>
            _eis.IsAvailable ? "libei" : "portal-notify",
        LinuxRemoteDesktopTier.PortalNoPen => "portal-notify",
        LinuxRemoteDesktopTier.X11Degraded => "xdotool",
        _ => null,
    };

    public LinuxInputBackendRouter(
        LinuxInputCapabilitySet capabilities,
        ILogger<LinuxInputBackendRouter>? logger = null)
    {
        _capabilities = capabilities;
        _logger = logger ?? NullLogger<LinuxInputBackendRouter>.Instance;
        _eis = new LinuxEisInputService(null);
        _uinputTablet = new LinuxUinputTabletService(null);

        InitializeSubServices();
    }

    private void InitializeSubServices()
    {
        if (_capabilities.EisAvailable)
            _logger.LogInformation("libei available; EIS sender will open when portal socket is ready.");

        if (_capabilities.UinputTabletAvailable)
        {
            bool ok = _uinputTablet.TryCreate("RemEx Virtual Tablet");
            if (!ok)
                _logger.LogWarning("uinput tablet creation failed despite /dev/uinput appearing writable.");
        }
    }

    /// <summary>
    /// Opens the EIS sender after the portal session provides the EIS socket path.
    /// Call this once the portal session has started successfully.
    /// </summary>
    public void OpenEisSender(string eisSocketPath)
    {
        if (_capabilities.EisAvailable)
            _eis.TryOpen(eisSocketPath);
    }

    // ── IInputSimulationService implementation ──────────────────────────

    public void MoveMouse(int x, int y)
    {
        if (_eis.IsAvailable) { _eis.SendPointerMotionAbsolute(x, y); return; }
        RunXdotool($"mousemove {x} {y}");
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        if (_eis.IsAvailable) { _eis.SendPointerMotion(dx, dy); return; }
        RunXdotool($"mousemove_relative -- {dx} {dy}");
    }

    public void MouseDown(int button)
    {
        if (_eis.IsAvailable) { _eis.SendButton(ButtonToLinuxCode(button), pressed: true); return; }
        RunXdotool($"mousedown {ButtonToXdotoolButton(button)}");
    }

    public void MouseUp(int button)
    {
        if (_eis.IsAvailable) { _eis.SendButton(ButtonToLinuxCode(button), pressed: false); return; }
        RunXdotool($"mouseup {ButtonToXdotoolButton(button)}");
    }

    public void MouseClick(int button) { MouseDown(button); MouseUp(button); }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (_eis.IsAvailable) { _eis.SendScroll(deltaX, deltaY); return; }

        if (deltaY != 0)
        {
            int btn = deltaY > 0 ? 4 : 5;
            int clicks = Math.Max(1, Math.Abs(deltaY) / 120);
            for (int i = 0; i < clicks; i++) RunXdotool($"click {btn}");
        }
        if (deltaX != 0)
        {
            int btn = deltaX > 0 ? 7 : 6;
            int clicks = Math.Max(1, Math.Abs(deltaX) / 120);
            for (int i = 0; i < clicks; i++) RunXdotool($"click {btn}");
        }
    }

    public void KeyDown(int keyCode)
    {
        int linuxKeyCode = LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(keyCode);
        if (_eis.IsAvailable)
        {
            if (linuxKeyCode >= 0)
            {
                _eis.SendKey((uint)linuxKeyCode, pressed: true);
            }
            return;
        }
        var xkbName = LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode) ?? keyCode.ToString();
        RunXdotool($"keydown {xkbName}");
    }

    public void KeyUp(int keyCode)
    {
        int linuxKeyCode = LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(keyCode);
        if (_eis.IsAvailable)
        {
            if (linuxKeyCode >= 0)
            {
                _eis.SendKey((uint)linuxKeyCode, pressed: false);
            }
            return;
        }
        var xkbName = LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode) ?? keyCode.ToString();
        RunXdotool($"keyup {xkbName}");
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        RunXdotoolArgs("type", "--", text);
    }

    public (int X, int Y) GetCursorPosition() => (0, 0);

    /// <summary>
    /// Routes a high-fidelity <see cref="DesktopPointerSample"/> from the Android client.
    /// Pen/eraser events are sent to the uinput tablet; regular pointer events use EIS or xdotool.
    /// </summary>
    public void EnqueuePointerSample(DesktopPointerSample sample)
    {
        bool isPen = sample.ToolKind is PointerToolKind.Pen or PointerToolKind.Eraser;

        if (isPen && _uinputTablet.IsAvailable)
        {
            _uinputTablet.SendStylusFrame(
                (double)sample.LogicalX,
                (double)sample.LogicalY,
                (double)sample.Pressure,
                (double)(sample.TiltX ?? 0f),
                (double)(sample.TiltY ?? 0f),
                (double)(sample.HoverDistance ?? 0f),
                sample.Phase is PointerPhase.ContactStart or PointerPhase.ContactMove,
                (sample.ButtonMask & 0x02) != 0,
                (sample.ButtonMask & 0x04) != 0,
                sample.ToolKind == PointerToolKind.Eraser);
            return;
        }

        if (_eis.IsAvailable)
        {
            bool relative = sample.Dx != 0f || sample.Dy != 0f;
            if (relative)
                _eis.SendPointerMotion((int)sample.Dx, (int)sample.Dy);
            else
                _eis.SendPointerMotionAbsolute((int)sample.LogicalX, (int)sample.LogicalY);
        }
    }

    /// <summary>Releases all input state. Call on client disconnect to prevent stuck buttons.</summary>
    public void Reset()
    {
        _uinputTablet.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        _eis.Dispose();
        _uinputTablet.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>evdev BTN_* code for the EIS backend. Single-sourced (RemEx-upxn).</summary>
    private static uint ButtonToLinuxCode(int button) => MouseButtonCodes.ToEvdev(button);

    /// <summary>xdotool's 1-based button number. Single-sourced (RemEx-upxn).</summary>
    private static int ButtonToXdotoolButton(int button) => MouseButtonCodes.ToXdotool(button);

    private static void RunXdotool(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(1000);
        }
        catch { /* xdotool not installed — best-effort */ }
    }

    private static void RunXdotoolArgs(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch { /* probing a backend that is not present on this system is the normal case, not an error - the router falls through to the next one */ }
    }
}
