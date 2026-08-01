using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Agent.Services.Input.Linux;
using Remex.Agent.Services.RemoteDesktop.Linux;

namespace Remex.Agent.Services.Input;

/// <summary>
/// Runs one input-tool invocation and returns its standard output.
/// </summary>
/// <remarks>
/// The single point every shell-tool call in <see cref="LinuxInputSimulationService"/> passes
/// through, so a test can assert the exact argv that would have reached <c>xdotool</c> or
/// <c>ydotool</c> without a process, a display server, or Linux (RemEx-fu9n).
/// </remarks>
internal delegate string InputToolLauncher(LinuxDesktopTool backend, string toolPath, string[] arguments);

/// <summary>
/// Top-left corner of the whole virtual desktop, in the same coordinate space
/// <see cref="LinuxInputSimulationService.MoveMouse"/> is given.
/// </summary>
/// <remarks>
/// A delegate rather than an <c>IScreenCaptureService</c> dependency because the value wanted is
/// <c>GetVirtualDesktopBounds</c>, which is on the concrete Linux capture service and not on the
/// shared interface — and putting it there would need a default implementation that is wrong on
/// Windows, where the active capture region and the virtual desktop genuinely differ. The composition
/// root already knows the concrete type, so the one type test lives there (RemEx-dyvd).
/// </remarks>
internal delegate (int Left, int Top) VirtualDesktopOrigin();

[SupportedOSPlatform("linux")]
public class LinuxInputSimulationService : IInputSimulationService
{
    private readonly ILogger<LinuxInputSimulationService> _logger;
    private readonly LinuxDesktopBackendStatus _backendStatus;
    private readonly string? _display;
    private readonly InputToolLauncher _launch;
    private readonly VirtualDesktopOrigin _virtualDesktopOrigin;

    // Stage 5 router. ALWAYS NULL IN PRODUCTION: nothing calls SetRouter, so the legacy portal /
    // xdotool / ydotool paths below are what actually run on every Linux tier (RemEx-7tkg). The
    // previous wording here — "router is set when a WaylandNative/PortalNoPen session is active" —
    // described an intention in the present tense and read as a description of behaviour.
    private LinuxInputBackendRouter? _router;

    // Portal-based Wayland input injector.  Created on Wayland when the portal is
    // available; null on X11 or when portal is absent.  Started lazily on first use
    // so that the permission dialog is shown only when a remote desktop session
    // actually begins sending input events.
    private readonly LinuxPortalInputInjector? _portalInjector;
    private int _portalStartAttempted; // 0 = not yet attempted, 1 = attempted (Interlocked)
    private readonly object _portalStartLock = new();

    // Virtual cursor tracking for absolute → relative conversion.
    // The portal RemoteDesktop API's NotifyPointerMotionAbsolute requires a PipeWire
    // stream id from a unified ScreenCast session; without that, only relative motion
    // works. We treat the first received absolute target as the cursor baseline and
    // dispatch every subsequent absolute target as a delta from the prior one.
    private double _lastVirtualX;
    private double _lastVirtualY;
    private bool _haveVirtualPosition;

    public string? BackendName
    {
        get
        {
            if (_router is not null) return _router.BackendName;
            if (_portalInjector?.IsActive == true) return "portal-notify";
            return _backendStatus.InputBackendName;
        }
    }

    // Unified ScreenCast + RemoteDesktop portal session owned by the capture lifetime. While a
    // remote desktop stream is active this enables ABSOLUTE pointer injection — drift-free and
    // compositor-clamped — instead of the relative-delta emulation below. Null in tests or when
    // no capture lifetime is registered. (RemEx-lq6h)
    private readonly Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime? _captureLifetime;

    public LinuxInputSimulationService(
        ILogger<LinuxInputSimulationService> logger,
        Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime? captureLifetime = null)
        : this(logger, captureLifetime, virtualDesktopOrigin: null)
    {
    }

    /// <summary>
    /// Production constructor for the composition root, which is the only thing that knows where the
    /// virtual-desktop origin comes from (RemEx-dyvd).
    /// </summary>
    /// <remarks>
    /// Internal rather than an extra public parameter so <see cref="VirtualDesktopOrigin"/> stays off
    /// the public surface: it exists to serve one Linux backend's coordinate quirk, and nothing
    /// outside this assembly has any business supplying one.
    /// </remarks>
    internal LinuxInputSimulationService(
        ILogger<LinuxInputSimulationService> logger,
        Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime? captureLifetime,
        VirtualDesktopOrigin? virtualDesktopOrigin)
        : this(logger, LinuxDesktopBackendProbe.Probe(), launcher: null, captureLifetime, virtualDesktopOrigin)
    {
    }

    /// <summary>
    /// Test seam: takes the probe result and the process launcher instead of discovering both
    /// (RemEx-fu9n).
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS EXISTS TO CATCH IS A BUG THIS REPO HAS ALREADY SHIPPED. Every method here ends in an
    /// argument list handed to another program, and RemEx-nb7c was two of those lists being wrong:
    /// <c>0x00110D</c> to press and <c>0x00110U</c> to release, neither a form <c>ydotool click</c>
    /// accepts, so clicking did nothing on that backend for as long as it existed. The fix moved the
    /// string into a testable function — and the tests written for it still could not see the call
    /// sites, so re-introducing the broken interpolation failed nothing. The defect class is not
    /// "the mapping is wrong", it is "the argv is wrong", and only the argv is worth pinning.
    /// </para>
    /// <para>
    /// Both parameters have to be injectable together, not just the launcher: which branch runs is
    /// decided by <see cref="LinuxDesktopBackendStatus.InputTool"/>, and the two backends want
    /// genuinely different words for the same action — <c>mousedown 1</c> against <c>click 0x40</c>.
    /// A test that could only observe whichever tool happened to be installed on the build machine
    /// would silently cover one branch on Linux and neither on Windows.
    /// </para>
    /// <para>
    /// The launcher stays a delegate rather than becoming an interface because there is exactly one
    /// operation and one real implementation; an interface would add a file and a name without
    /// removing a decision.
    /// </para>
    /// </remarks>
    internal LinuxInputSimulationService(
        ILogger<LinuxInputSimulationService> logger,
        LinuxDesktopBackendStatus backendStatus,
        InputToolLauncher? launcher = null,
        Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime? captureLifetime = null,
        VirtualDesktopOrigin? virtualDesktopOrigin = null)
    {
        _logger = logger;
        _captureLifetime = captureLifetime;
        _display = Environment.GetEnvironmentVariable("DISPLAY");
        _backendStatus = backendStatus;
        _launch = launcher ?? RunToolWithOutput;

        // (0,0) is the right default rather than a placeholder: it is what an X11 desktop always
        // reports, and it makes the translation below a no-op wherever the origin is already zero.
        _virtualDesktopOrigin = virtualDesktopOrigin ?? DesktopOriginAtZero;

        // On Wayland, create a portal input injector so that pointer events work even
        // when xdotool / ydotool are not available or cannot inject into the compositor.
        // Use the backend probe's detection (which considers XDG_SESSION_TYPE) rather
        // than a naive WAYLAND_DISPLAY-only check: KDE/GNOME Wayland sessions always
        // also set DISPLAY=:0 for XWayland, so requiring DISPLAY to be empty hides
        // the Wayland path on every real Wayland desktop.
        if (_backendStatus.IsWaylandSession)
        {
            _portalInjector = new LinuxPortalInputInjector(logger);
            _logger.LogInformation(
                "Wayland session detected — portal input injector created (will prompt for " +
                "permission on first input event).");
        }

        _logger.LogInformation(
            "Linux input backend: {InputBackend} ({InputPath}); cursor query backend: {CursorBackend} ({CursorPath})",
            _backendStatus.InputBackendName ?? "none",
            _backendStatus.InputToolPath ?? "n/a",
            _backendStatus.CursorQueryBackendName ?? "none",
            _backendStatus.CursorQueryToolPath ?? "n/a");
    }

    /// <summary>
    /// Sets the backend router for WaylandNative / PortalNoPen tiers. Pass null to revert to the
    /// legacy shell-tool path.
    /// </summary>
    /// <remarks>
    /// NO PRODUCTION CALLER, AND "ALL INPUT EVENTS" WAS NEVER TRUE (RemEx-7tkg). The router is
    /// consulted by exactly four members — <see cref="BackendName"/>,
    /// <see cref="EnqueuePointerSample"/>, <see cref="KeyDown"/> and <see cref="KeyUp"/>. Every mouse
    /// method checks the portal injector and then the shell tool, never the router, so setting one
    /// today would route the keyboard through it and silently leave the pointer on the old path.
    /// Whoever wires Stage 5 up has to extend the mouse methods as well; a test pins the current
    /// split so that discovery happens at compile-and-test time rather than on a device.
    /// </remarks>
    public void SetRouter(LinuxInputBackendRouter? router)
    {
        _router = router;
    }

    /// <summary>
    /// Routes a pointer sample from the Android client.
    /// Pen events are forwarded to the uinput tablet; regular events go through
    /// the router or legacy path.
    /// </summary>
    public void EnqueuePointerSample(DesktopPointerSample sample)
    {
        if (_router is not null)
        {
            _router.EnqueuePointerSample(sample);
            return;
        }
        // Legacy path: convert to absolute mouse move
        MoveMouse((int)sample.LogicalX, (int)sample.LogicalY);
    }

    public (int X, int Y) GetCursorPosition()
    {
        if (_backendStatus.CursorQueryTool is LinuxDesktopTool.Kdotool or LinuxDesktopTool.Xdotool &&
            _backendStatus.CursorQueryToolPath is not null)
        {
            try
            {
                var result = _launch(_backendStatus.CursorQueryTool, _backendStatus.CursorQueryToolPath, ["getmouselocation", "--shell"]);
                // Output format: X=123\nY=456\nSCREEN=0\nWINDOW=12345
                var lines = result.Split('\n');
                var x = 0;
                var y = 0;
                foreach (var line in lines)
                {
                    if (line.StartsWith("X=")) int.TryParse(line.Substring(2), out x);
                    if (line.StartsWith("Y=")) int.TryParse(line.Substring(2), out y);
                }
                return (x, y);
            }
            catch { return (0, 0); }
        }

        return (0, 0);
    }

    public void MoveMouse(int x, int y)
    {
        // Preferred path: ABSOLUTE motion through the unified ScreenCast + RemoteDesktop portal
        // session that the active remote desktop stream already holds. The compositor clamps the
        // position to the stream surface, so there is no cumulative drift and the cursor can
        // never escape or desync at screen edges. (RemEx-lq6h)
        if (_captureLifetime?.TryInjectPointerMotionAbsolute(x, y) == true)
        {
            // Keep the relative-emulation tracker in sync so a later fallback doesn't snap.
            _lastVirtualX = x;
            _lastVirtualY = y;
            _haveVirtualPosition = true;
            return;
        }

        // Portal path: input-only sessions cannot dispatch absolute motion (the portal
        // requires a PipeWire stream from a unified ScreenCast session). We convert
        // each absolute target into a delta from the prior absolute target and emit
        // relative motion instead. The first event of every drag is treated as the
        // baseline — the cursor stays put until subsequent samples produce a delta.
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            EmitPortalRelativeFromAbsolute(x, y);
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            // YDOTOOL HAS NO ABSOLUTE MODE, AND ITS EMULATION IS NOT A COORDINATE SYSTEM (RemEx-dyvd).
            // `--absolute` slams the pointer to the minimum of the RELATIVE pointer space and then
            // moves by the operands (tool_mousemove.c: two REL emits of INT32_MIN, then the values),
            // so what it wants is an OFFSET FROM THE DESKTOP'S TOP-LEFT, not a position. Those two
            // coincide only when the virtual desktop's origin is (0,0), which is the case on X11 by
            // construction — the root window is 0-based — and is why this went unnoticed. Where a
            // compositor reports a non-zero origin, every target landed off by exactly that origin,
            // POSITIVE COORDINATES INCLUDED: with the desktop starting at x = -1920, a target of 300
            // homed to -1920 and then moved +300, landing at -1620, on the wrong monitor.
            var (originLeft, originTop) = _virtualDesktopOrigin();
            RunTool(
                "mousemove",
                "--absolute",
                "-x", Arg(x - originLeft),
                "-y", Arg(y - originTop));
        }
        else
        {
            // NOT translated, and that is not an oversight. xdotool takes X11 screen coordinates, and
            // the coordinates arriving here ARE X11 screen coordinates on an X11 session, so the two
            // spaces are the same one. Subtracting an origin here would break the case it fixes above.
            RunTool("mousemove", Arg(x), Arg(y));
        }
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerMotionRelative(dx, dy);
            // Keep the virtual cursor in sync so absolute samples after a relative
            // move don't snap.
            if (_haveVirtualPosition)
            {
                _lastVirtualX += dx;
                _lastVirtualY += dy;
            }
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("mousemove", "-x", Arg(dx), "-y", Arg(dy));
        else
            RunTool("mousemove_relative", "--", Arg(dx), Arg(dy));
    }

    public void MouseDown(int button)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerButton(MapButtonLinux(button), pressed: true);
            return;
        }
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("click", MouseButtonCodes.YdotoolClickArgument(button, pressed: true));
        else
            RunTool("mousedown", Arg(MapButtonXdotool(button)));
    }

    public void MouseUp(int button)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerButton(MapButtonLinux(button), pressed: false);
            // Release ends a contact; the next absolute target should re-baseline
            // so the cursor does not jump when the user taps somewhere else.
            _haveVirtualPosition = false;
            return;
        }
        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
            RunTool("click", MouseButtonCodes.YdotoolClickArgument(button, pressed: false));
        else
            RunTool("mouseup", Arg(MapButtonXdotool(button)));
    }

    /// <summary>
    /// Resets the virtual cursor baseline. Call when a new touch/pen contact starts
    /// so the cursor doesn't jump when the user lifts and re-places their finger.
    /// </summary>
    public void ResetVirtualCursor()
    {
        _haveVirtualPosition = false;
    }

    private void EmitPortalRelativeFromAbsolute(int x, int y)
    {
        if (_portalInjector is null) return;
        if (_haveVirtualPosition)
        {
            var dx = x - _lastVirtualX;
            var dy = y - _lastVirtualY;
            if (dx != 0 || dy != 0)
                _portalInjector.NotifyPointerMotionRelative(dx, dy);
        }
        _lastVirtualX = x;
        _lastVirtualY = y;
        _haveVirtualPosition = true;
    }

    public void MouseClick(int button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    public void MouseScroll(int deltaX, int deltaY)
    {
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            _portalInjector.NotifyPointerScrollDiscrete(deltaX, deltaY);
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            // THE SAME BUG AS THE CLICK PATH, AND IT WAS ALSO A NO-OP (RemEx-nb7c). This used to send
            // X11's wheel buttons 4/5/6/7 to `ydotool click`. ydotool has no wheel button at all: it
            // masks the argument to a low nibble and ORs it onto BTN_MOUSE — Client/tool_click.c does
            // `keycode = (key & 0xf) | 0x110` — so 4/5/6/7 selected EXTR/FORWARD/BACK/TASK, and since
            // the argument carried neither the 0x40 nor the 0x80 action bit, the tool's `if (key &
            // 0x40)` / `if (key & 0x80)` guards both failed and it emitted NOTHING. Every scroll
            // spawned up to ten processes to do nothing.
            //
            // `mousemove --wheel` is the real mechanism, and it takes detent COUNTS rather than
            // repeated events (tool_mousemove.c emits REL_HWHEEL/REL_WHEEL with the values verbatim),
            // so one invocation replaces the whole loop. It is documented in `ydotool mousemove
            // --help` and present in the source; the man page's mouse section omits it, which is
            // presumably how the click-based workaround came to be written.
            int horizontal = WheelDetents(deltaX);
            int vertical = WheelDetents(deltaY);

            if (horizontal != 0 || vertical != 0)
            {
                // Signs match the buttons this replaces: REL_WHEEL is positive up (was button 4) and
                // REL_HWHEEL positive right (was button 7). --wheel and --absolute are mutually
                // exclusive in ydotool, so no position flag goes with this.
                RunTool(
                    "mousemove",
                    "--wheel",
                    "-x", Arg(horizontal),
                    "-y", Arg(vertical));
            }
        }
        else
        {
            // xdotool: button 4=scroll up, 5=scroll down, 6=scroll left, 7=scroll right
            if (deltaY > 0)
                for (int i = 0; i < Math.Clamp(deltaY / 120, 1, 10); i++)
                    RunTool("click", "4");
            else if (deltaY < 0)
                for (int i = 0; i < Math.Clamp(-deltaY / 120, 1, 10); i++)
                    RunTool("click", "5");

            if (deltaX > 0)
                for (int i = 0; i < Math.Clamp(deltaX / 120, 1, 10); i++)
                    RunTool("click", "7");
            else if (deltaX < 0)
                for (int i = 0; i < Math.Clamp(-deltaX / 120, 1, 10); i++)
                    RunTool("click", "6");
        }
    }

    public void KeyDown(int keyCode)
    {
        if (_router is not null)
        {
            _router.KeyDown(keyCode);
            return;
        }

        int linuxKeyCode = LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(keyCode);
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive && linuxKeyCode >= 0)
        {
            _portalInjector.NotifyKeyboardKeycode(linuxKeyCode, pressed: true);
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            RunTool("key", $"{Arg(linuxKeyCode >= 0 ? linuxKeyCode : keyCode)}:1");
            return;
        }

        RunTool("keydown", LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode) ?? Arg(keyCode));
    }

    public void KeyUp(int keyCode)
    {
        if (_router is not null)
        {
            _router.KeyUp(keyCode);
            return;
        }

        int linuxKeyCode = LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(keyCode);
        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive && linuxKeyCode >= 0)
        {
            _portalInjector.NotifyKeyboardKeycode(linuxKeyCode, pressed: false);
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            RunTool("key", $"{Arg(linuxKeyCode >= 0 ? linuxKeyCode : keyCode)}:0");
            return;
        }

        RunTool("keyup", LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode) ?? Arg(keyCode));
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_portalInjector is not null && EnsurePortalStarted() && _portalInjector.IsActive)
        {
            foreach (var keysym in LinuxInputEventTranslator.TextToPortalKeysyms(text))
            {
                _portalInjector.NotifyKeyboardKeysym(keysym, pressed: true);
                _portalInjector.NotifyKeyboardKeysym(keysym, pressed: false);
            }
            return;
        }

        if (_backendStatus.InputTool == LinuxDesktopTool.Ydotool)
        {
            // ydotool type takes text directly, safe via argument array
            RunTool("type", "--", text);
        }
        else
        {
            // xdotool type: pass text via argument array to avoid shell injection
            RunTool("type", "--", text);
        }
    }

    /// <summary>
    /// Formats a number for a shell tool's argument list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// INVARIANT CULTURE BECAUSE THESE VALUES CAN BE NEGATIVE. <c>NumberFormatInfo.NegativeSign</c> is
    /// culture-dependent — several locales define it as U+2212 MINUS SIGN rather than the ASCII
    /// hyphen — and neither xdotool nor ydotool can parse that. It is the same class of failure as
    /// the getopt one the coordinate sites were fixed for (RemEx-r29r), reached by a different route.
    /// </para>
    /// <para>
    /// USED FOR EVERY NUMBER THAT BECOMES AN ARGUMENT, not only the ones that can currently go
    /// negative (RemEx-hbma). A button index cannot, and neither can a clamped detent count, so those
    /// are invariant already by accident of their range — which is exactly why leaving them formatted
    /// differently is worse than pointless: the next reader cannot tell the deliberate cases from the
    /// overlooked ones, and the value that CAN go negative is the unvalidated <c>keyCode</c> straight
    /// off the wire. One rule, no exceptions to remember.
    /// </para>
    /// <para>
    /// Named to match <c>LinuxInputBackendRouter.Arg</c>, which exists for the same reason in the
    /// sibling class. Two names for one rule is how the rule gets applied to one of them.
    /// </para>
    /// </remarks>
    private static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The virtual-desktop origin when nobody supplied one: <c>(0,0)</c>, which is what an X11
    /// desktop always reports and what makes the ydotool translation a no-op.
    /// </summary>
    private static (int Left, int Top) DesktopOriginAtZero() => (0, 0);

    /// <summary>xdotool's 1-based button number. Single-sourced (RemEx-upxn).</summary>
    private static int MapButtonXdotool(int button) => MouseButtonCodes.ToXdotool(button);

    /// <summary>evdev BTN_* code for the portal's NotifyPointerButton. Single-sourced (RemEx-upxn).</summary>
    private static int MapButtonLinux(int button) => (int)MouseButtonCodes.ToEvdev(button);

    /// <summary>
    /// Converts a wheel delta in the protocol's 120-per-notch units to whole wheel detents, keeping
    /// the sign.
    /// </summary>
    /// <remarks>
    /// The clamp is inherited from the loop this replaced: any non-zero delta is worth at least one
    /// detent, so a sub-notch scroll does not silently vanish, and a runaway delta is capped so a
    /// single message cannot fling the page.
    /// </remarks>
    internal static int WheelDetents(int delta) =>
        delta == 0 ? 0 : Math.Sign(delta) * (int)Math.Clamp(Math.Abs((long)delta) / 120, 1, 10);

    /// <summary>
    /// Synchronously ensures the portal input session is active. The first caller blocks
    /// until the KDE permission dialog completes (up to 2 minutes); subsequent callers
    /// return immediately once the session is up. After a failed start we mark the
    /// attempt complete and fall through to the legacy shell path on the next event.
    /// </summary>
    /// <remarks>
    /// This runs on the input worker thread (no <see cref="SynchronizationContext"/>),
    /// so the sync-over-async <c>GetAwaiter().GetResult()</c> cannot deadlock.
    /// </remarks>
    /// <returns>True if the portal session is active.</returns>
    private bool EnsurePortalStarted()
    {
        if (_portalInjector is null) return false;
        if (_portalInjector.IsActive) return true;

        // Only one thread runs the start; others just observe IsActive afterwards.
        if (Interlocked.CompareExchange(ref _portalStartAttempted, 1, 0) == 0)
        {
            lock (_portalStartLock)
            {
                if (!_portalInjector.IsActive)
                {
                    try
                    {
                        _portalInjector.EnsureStartedAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Portal input session start raised an exception.");
                    }
                }
            }
        }
        else
        {
            // Another thread is starting; wait for it to publish IsActive (or give up).
            lock (_portalStartLock) { }
        }

        return _portalInjector.IsActive;
    }

    private void RunTool(params string[] arguments)
    {
        if (_backendStatus.InputTool == LinuxDesktopTool.None || _backendStatus.InputToolPath is null)
        {
            _logger.LogWarning("No input simulation tool available (install xdotool or ydotool).");
            return;
        }

        try
        {
            _ = _launch(_backendStatus.InputTool, _backendStatus.InputToolPath, arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Backend} command failed: {Args}", _backendStatus.InputBackendName ?? "none", string.Join(" ", arguments));
        }
    }

    private string RunToolWithOutput(LinuxDesktopTool backend, string toolPath, params string[] arguments)
    {
        var argList = new List<string>(arguments);
        var psi = new ProcessStartInfo(toolPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        if (backend == LinuxDesktopTool.Xdotool && !string.IsNullOrEmpty(_display))
            psi.Environment["DISPLAY"] = _display;

        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(2000);
        return output;
    }
}
