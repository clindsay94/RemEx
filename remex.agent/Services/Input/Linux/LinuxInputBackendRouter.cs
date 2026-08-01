using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Validation;
using Remex.Agent.Services.RemoteDesktop.Linux;

using Remex.Agent.Services.Input;

namespace Remex.Agent.Services.Input.Linux;

/// <summary>
/// Runs one <c>xdotool</c> invocation with an already-separated argument list.
/// </summary>
/// <remarks>
/// The single point every shell call in <see cref="LinuxInputBackendRouter"/> passes through, so a
/// test can assert the exact argv without a process or a display server (RemEx-n3z6). Mirrors
/// <c>InputToolLauncher</c> in <c>LinuxInputSimulationService</c>, which exists for the same reason
/// — the two classes are deliberately NOT sharing one, because they route to different backends and
/// the point of asserting each is that they might disagree.
/// </remarks>
internal delegate void XdotoolLauncher(string[] arguments);

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

    private readonly XdotoolLauncher _runXdotool;

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
        : this(capabilities, logger, launcher: null)
    {
    }

    /// <summary>
    /// Test seam: takes the process launcher instead of using the real one (RemEx-n3z6).
    /// </summary>
    /// <remarks>
    /// Every method here ends in an argument list handed to another program, and this repo has
    /// shipped that list wrong twice: RemEx-nb7c sent <c>ydotool click</c> a form it could not act
    /// on, and RemEx-r29r sent <c>ydotool mousemove</c> coordinates <c>getopt</c> silently discarded.
    /// Both were in the sibling class, both survived tests that covered the button MAPPING, and both
    /// were caught only once the argv itself became assertable. This class had the same shape and no
    /// such test.
    /// </remarks>
    internal LinuxInputBackendRouter(
        LinuxInputCapabilitySet capabilities,
        ILogger<LinuxInputBackendRouter>? logger,
        XdotoolLauncher? launcher)
    {
        _runXdotool = launcher ?? RunXdotoolArgs;
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
        _runXdotool(["mousemove", Arg(x), Arg(y)]);
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        if (_eis.IsAvailable) { _eis.SendPointerMotion(dx, dy); return; }
        _runXdotool(["mousemove_relative", "--", Arg(dx), Arg(dy)]);
    }

    public void MouseDown(int button)
    {
        if (_eis.IsAvailable) { _eis.SendButton(ButtonToLinuxCode(button), pressed: true); return; }
        _runXdotool(["mousedown", Arg(ButtonToXdotoolButton(button))]);
    }

    public void MouseUp(int button)
    {
        if (_eis.IsAvailable) { _eis.SendButton(ButtonToLinuxCode(button), pressed: false); return; }
        _runXdotool(["mouseup", Arg(ButtonToXdotoolButton(button))]);
    }

    public void MouseClick(int button) { MouseDown(button); MouseUp(button); }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (_eis.IsAvailable) { _eis.SendScroll(deltaX, deltaY); return; }

        if (deltaY != 0)
        {
            int btn = deltaY > 0 ? 4 : 5;
            int clicks = ClickCount(deltaY);
            for (int i = 0; i < clicks; i++) _runXdotool(["click", Arg(btn)]);
        }
        if (deltaX != 0)
        {
            int btn = deltaX > 0 ? 7 : 6;
            int clicks = ClickCount(deltaX);
            for (int i = 0; i < clicks; i++) _runXdotool(["click", Arg(btn)]);
        }
    }

    /// <summary>
    /// Wheel detents for a scroll delta, as a count of xdotool button presses.
    /// </summary>
    /// <remarks>
    /// Two things were wrong here and only one of them threw. Widening to <see cref="long"/> before
    /// taking the magnitude is because <c>Math.Abs(int.MinValue)</c> throws, and an escape from here
    /// ends the remote-desktop session's input thread permanently (RemEx-hnin). The ceiling is the
    /// other: this was <c>Math.Max(1, ...)</c> with no upper bound, so a large delta asked for one
    /// <c>xdotool</c> process per detent — millions of them for a delta near <c>int.MaxValue</c>.
    /// <para>
    /// Ten is written as a literal rather than derived from
    /// <see cref="CoordinateValidation.MaxScrollDelta"/> deliberately: this is a cap on how many
    /// processes this loop may spawn, which is a property of this loop, not of the wire. Deriving it
    /// would let a future widening of the wire bound silently raise the spawn ceiling here while
    /// <c>LinuxInputSimulationService.WheelDetents</c> stayed at ten.
    /// </para>
    /// </remarks>
    private static int ClickCount(int delta) =>
        (int)Math.Clamp(Math.Abs((long)delta) / 120, 1, 10);

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
        var xkbName = LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode) ?? Arg(keyCode);
        _runXdotool(["keydown", xkbName]);
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
        var xkbName = LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode) ?? Arg(keyCode);
        _runXdotool(["keyup", xkbName]);
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _runXdotool(["type", "--", text]);
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

    /// <summary>
    /// Formats a number for an argument list, invariantly.
    /// </summary>
    /// <remarks>
    /// <c>NumberFormatInfo.NegativeSign</c> is culture-dependent, and relative deltas are signed, so
    /// a culture rendering U+2212 MINUS SIGN would produce an argument xdotool cannot parse. Same
    /// reasoning as the sibling class's <c>Coordinate</c> helper (RemEx-r29r).
    /// </remarks>
    private static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The real launcher: one process per invocation, arguments passed as a LIST.
    /// </summary>
    /// <remarks>
    /// THE STRING-ARGUMENT OVERLOAD THIS REPLACED WAS A LATENT BUG, not merely untestable. It built
    /// one space-joined command line and handed it to <c>ProcessStartInfo(fileName, arguments)</c>,
    /// which re-splits and re-quotes it — so any argument containing a space or a quote would have
    /// been silently re-parsed into different arguments. Nothing sent through it happened to contain
    /// one, which is why it never failed; <c>type</c> was already using the list form precisely
    /// because its payload is attacker-chosen text. Now everything does, and there is one launcher
    /// rather than two that must agree (RemEx-n3z6).
    /// </remarks>
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

            // ONE TIMEOUT WHERE THERE WERE TWO, and it is the shorter one. The string-argument
            // launcher this replaced waited 1000ms and carried eight of the nine call sites; the list
            // one waited 2000ms and carried only `type`. Collapsing onto 2000 would have doubled the
            // worst-case block on the input path — MouseScroll spawns one process per detent, up to
            // ten, synchronously — so `type` moves to 1000 instead. The wait is backpressure, not a
            // kill: WaitForExit(t) returning false leaves the process running and Dispose does not
            // terminate it, so a long `xdotool type` still finishes; we just stop waiting on it.
            //
            // WHAT IT DOES COST, precisely. TypeText can now return while xdotool is still emitting,
            // so a following event's process can interleave keystrokes. That window is not new —
            // xdotool types at roughly 12ms per keystroke, so 2000ms already stopped covering text
            // beyond about 166 characters. It opens above about 83 instead (RemEx-n3z6).
            proc?.WaitForExit(1000);
        }
        catch { /* probing a backend that is not present on this system is the normal case, not an error - the router falls through to the next one */ }
    }
}
