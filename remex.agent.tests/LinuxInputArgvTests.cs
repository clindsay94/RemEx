using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Input;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the exact argument vector each input action hands to <c>xdotool</c> / <c>ydotool</c>
/// (RemEx-fu9n).
///
/// THE GAP THIS CLOSES IS ONE THIS REPO HAS ALREADY FALLEN INTO, TWICE OVER. RemEx-nb7c found
/// <c>ydotool click</c> being given <c>0x00110D</c> to press and <c>0x00110U</c> to release — an
/// evdev button code with a letter appended. Neither was rejected — ydotool parses with strtol and
/// never checks an endptr — but neither carried an action bit either, so clicking did nothing at all
/// on that backend. The fix moved the
/// string into a pure function and tested it thoroughly. It still was not enough: re-introducing the
/// original broken interpolation at the CALL SITE failed no test, because everything under test was
/// the function the call site had stopped using correctly. The lesson is that the unit worth pinning
/// is not the mapping but the argv, because the argv is the only thing the other program sees.
///
/// WHY THE BACKEND IS INJECTED RATHER THAN DETECTED. Every method here branches on which tool was
/// probed, and the two want different words for the same action — <c>mousedown 1</c> against
/// <c>click 0x40</c>. Detecting instead would mean covering whichever tool the build machine happens
/// to have, which is neither on Windows and one of two on Linux, chosen by accident.
///
/// This never starts a process. <c>InputToolLauncher</c> stands in for the launcher, so these run
/// identically on every platform despite the class being <c>[SupportedOSPlatform("linux")]</c>
/// (CA1416 is in the repo-wide NoWarn, and the sibling Linux suites already rely on that).
/// </summary>
public sealed class LinuxInputArgvTests
{
    private sealed class Recorder
    {
        public List<string[]> Calls { get; } = [];

        public InputToolLauncher Launcher => (_, _, arguments) =>
        {
            Calls.Add(arguments);
            return string.Empty;
        };

        public string[] Single() => Assert.Single(Calls);
    }

    /// <summary>
    /// A probe result naming <paramref name="tool"/> as the input backend and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>IsWaylandSession: false</c> is load bearing rather than incidental: the constructor builds a
    /// real <c>LinuxPortalInputInjector</c> when it is true, and every action then tries the portal
    /// before the shell tool. These tests are about the shell argv, so the portal must be absent by
    /// construction rather than by hoping it fails to start.
    /// </remarks>
    private static LinuxDesktopBackendStatus Backend(LinuxDesktopTool tool) => new(
        DesktopEnvironment: "test",
        IsWaylandSession: false,
        IsKdePlasma: false,
        HasDisplayServer: true,
        InputTool: tool,
        InputToolPath: "/usr/bin/tool",
        CursorQueryTool: tool,
        CursorQueryToolPath: "/usr/bin/tool",
        WindowControlTool: LinuxDesktopTool.None,
        WindowControlToolPath: null);

    private static (LinuxInputSimulationService Service, Recorder Recorder) New(
        LinuxDesktopTool tool,
        int originLeft = 0,
        int originTop = 0)
    {
        var recorder = new Recorder();
        var service = new LinuxInputSimulationService(
            NullLogger<LinuxInputSimulationService>.Instance,
            Backend(tool),
            recorder.Launcher,
            captureLifetime: null,
            virtualDesktopOrigin: () => (originLeft, originTop));
        return (service, recorder);
    }

    // ── ydotool click: the argv that was wrong for as long as the backend existed ───────────────

    [Fact]
    public void YdotoolMouseDownSendsClickWithThePressBitSet()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MouseDown(MouseButtons.Right);

        // 0x41 is ydotool's own worked example for "right button down". The old code sent
        // "0x00110D" here, which is why this asserts the whole vector rather than just the argument.
        Assert.Equal(["click", "0x41"], recorder.Single());
    }

    [Fact]
    public void YdotoolMouseUpSendsClickWithTheReleaseBitSet()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MouseUp(MouseButtons.Middle);

        // "0x82: middle button up", again upstream's example. The old code sent "0x00110U", which
        // ydotool did not reject either — it parses with strtol and no endptr check, so it silently
        // truncated to 0x110 and, carrying no action bit, did nothing.
        Assert.Equal(["click", "0x82"], recorder.Single());
    }

    [Fact]
    public void TheTwoBackendsDisagreeAboutEverythingExceptWhichButtonWasPressed()
    {
        // The reason the backend is a parameter. Same call, same button, two entirely different
        // vectors — and xdotool's is 1-based where ydotool's is a bitmask, so an argument built for
        // one is silently meaningful to the other.
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.MouseDown(MouseButtons.Left);
        xdotool.MouseDown(MouseButtons.Left);

        Assert.Equal(["click", "0x40"], ydotoolCalls.Single());
        Assert.Equal(["mousedown", "1"], xdotoolCalls.Single());
    }

    // ── scroll: the same defect, found later, in the same file ──────────────────────────────────

    [Fact]
    public void YdotoolScrollUsesMousemoveWheelRatherThanAWheelButtonThatDoesNotExist()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MouseScroll(0, 240);

        // This used to be `click 4` — an X11 wheel button number sent to a tool with no wheel button,
        // which masked to a real button index carrying no action bit and therefore did nothing, ten
        // processes at a time. --wheel takes signed detent counts instead, so one call replaces the
        // loop. (RemEx-nb7c)
        Assert.Equal(["mousemove", "--wheel", "-x", "0", "-y", "2"], recorder.Single());
    }

    [Fact]
    public void XdotoolScrollStillClicksTheWheelButtonsBecauseThereItIsCorrect()
    {
        var (service, recorder) = New(LinuxDesktopTool.Xdotool);

        service.MouseScroll(0, -240);

        // X11 really does model the wheel as buttons 4/5 (and 6/7 horizontally), so the shape that
        // was wrong for ydotool is right here. Two clicks for two detents.
        Assert.Equal(2, recorder.Calls.Count);
        Assert.All(recorder.Calls, c => Assert.Equal(["click", "5"], c));
    }

    // ── movement, keys and text: the rest of the surface, so this is a sweep and not a spot check ──

    [Fact]
    public void YdotoolAbsoluteMovesCarryTheAbsoluteFlagAndXdotoolsDoNot()
    {
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.MoveMouse(300, 400);
        xdotool.MoveMouse(300, 400);

        // ydotool's mousemove is RELATIVE by default, so omitting --absolute here would turn every
        // absolute pointer target into a displacement — the cursor would walk off the screen rather
        // than land anywhere. The -x/-y flags are the other half: they are what stops getopt eating
        // a negative coordinate (RemEx-r29r), and they are harmless for a positive one.
        Assert.Equal(["mousemove", "--absolute", "-x", "300", "-y", "400"], ydotoolCalls.Single());
        Assert.Equal(["mousemove", "300", "400"], xdotoolCalls.Single());
    }

    [Fact]
    public void RelativeMovesUseTheOppositeDefaultOnEachBackend()
    {
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.MouseMoveRelative(5, 7);
        xdotool.MouseMoveRelative(5, 7);

        // The mirror image of the test above, and the reason both are worth having: ydotool's plain
        // mousemove is the relative one, while xdotool needs a different SUBCOMMAND entirely.
        Assert.Equal(["mousemove", "-x", "5", "-y", "7"], ydotoolCalls.Single());
        Assert.Equal(["mousemove_relative", "--", "5", "7"], xdotoolCalls.Single());
    }

    [Fact]
    public void ANegativeDeltaSurvivesGetoptOnBothBackends()
    {
        // WAS A DELIBERATE PIN ON A BUG UNTIL RemEx-r29r, and it is worth knowing why it changed
        // rather than just that it did. The moment the argv became visible it showed that on ydotool
        // a bare "-5" is not a coordinate: upstream's Client/tool_mousemove.c calls getopt_long with
        // the optstring "hawx:y:" — no leading '-' or '+' — so "-5" is read as an option cluster,
        // none of those characters are in the optstring, each is discarded by `case '?': break;`, and
        // only "7" reaches the positional loop. That left i == 1, the `if (i == 2)` guard failed, and
        // the tool printed its help and emitted NOTHING. Every leftward or upward drag silently did
        // nothing while rightward and downward ones worked.
        //
        // -x/-y is the fix because getopt takes the NEXT argv as a required option's argument
        // unconditionally, dash or no dash, so the value is never re-parsed as flags. It is also the
        // shape the scroll path already uses.
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.MouseMoveRelative(-5, 7);
        xdotool.MouseMoveRelative(-5, 7);

        Assert.Equal(["mousemove", "-x", "-5", "-y", "7"], ydotoolCalls.Single());

        // xdotool was correct all along, by a different route: the end-of-options marker. Same
        // hazard, two answers, which is why the sweep asserts both rather than assuming they agree.
        Assert.Equal(["mousemove_relative", "--", "-5", "7"], xdotoolCalls.Single());
    }

    [Fact]
    public void AnAbsoluteTargetIsTranslatedToAnOffsetFromTheDesktopsTopLeft()
    {
        // WHAT THIS TEST ASSERTED CHANGED TWICE, and the sequence is the point. RemEx-fu9n pinned the
        // broken argv (the coordinate never reached the tool); RemEx-r29r made it reach the tool but
        // said in capitals that reaching it is not the same as landing right; RemEx-dyvd is the
        // landing. ydotool has no absolute mode — --absolute slams the pointer to the minimum of the
        // RELATIVE space and then moves by the operands — so the operands are an offset from the
        // desktop's top-left, and passing a virtual-desktop coordinate is only correct when that
        // origin happens to be (0,0).
        //
        // With the desktop starting at x = -1920, the target -1920 is its LEFT EDGE, so the offset is
        // 0 and no motion follows the homing. Passing -1920 through untranslated instead homed to the
        // edge and then moved a further -1920, which the compositor clamps — the cursor never leaves
        // the corner.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool, originLeft: -1920);

        service.MoveMouse(-1920, 100);

        Assert.Equal(["mousemove", "--absolute", "-x", "0", "-y", "100"], recorder.Single());
    }

    [Fact]
    public void AnAbsoluteOperandCanStillGoNegativeAndMustStillSurviveGetopt()
    {
        // COVERAGE THAT NEARLY VANISHED WITH THE FIX, spotted in review. For an in-bounds target the
        // translation makes the operand non-negative by construction, so once this method started
        // subtracting the origin, no absolute-path test exercised a negative operand any more — and a
        // negative operand on the absolute path is exactly what RemEx-r29r existed to make survivable.
        // Out-of-bounds targets are reachable: PingPongHandler dispatches mouseMove with no clamp at
        // all, unlike the remote-desktop handler.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool, originLeft: -1920, originTop: -50);

        service.MoveMouse(-4000, -300);

        Assert.Equal(["mousemove", "--absolute", "-x", "-2080", "-y", "-250"], recorder.Single());
    }

    [Fact]
    public void APositiveAbsoluteTargetIsTranslatedToo()
    {
        // THE HALF THAT MAKES THIS NOT A NEGATIVE-NUMBER BUG. RemEx-r29r fixed getopt eating negative
        // operands; this is a different defect that survives it, and the giveaway is that it bites
        // perfectly ordinary positive coordinates. On a desktop starting at x = -1920, a target of 300
        // sits on the primary monitor — but untranslated it homed to -1920 and moved +300, landing at
        // -1620, on the OTHER monitor entirely.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool, originLeft: -1920);

        service.MoveMouse(300, 400);

        Assert.Equal(["mousemove", "--absolute", "-x", "2220", "-y", "400"], recorder.Single());
    }

    [Fact]
    public void AZeroOriginLeavesTheCoordinateExactlyAsItWas()
    {
        // The no-op case, which is every X11 desktop: the root window is 0-based by construction, so
        // the translation subtracts nothing. Pinned because it is the reason this defect went
        // unnoticed, and because a translation that quietly changed the common case would be a
        // regression dressed as a fix.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MoveMouse(300, 400);

        Assert.Equal(["mousemove", "--absolute", "-x", "300", "-y", "400"], recorder.Single());
    }

    [Fact]
    public void XdotoolIsDeliberatelyNotTranslated()
    {
        // NOT an oversight, and the sign would be wrong if it were copied. xdotool takes X11 screen
        // coordinates, and on an X11 session the coordinates arriving here ARE X11 screen
        // coordinates — the same space, so subtracting an origin would introduce the error it fixes
        // for ydotool. Asserted with a non-zero origin precisely so the two branches cannot be
        // "unified" later without failing something.
        var (service, recorder) = New(LinuxDesktopTool.Xdotool, originLeft: -1920);

        service.MoveMouse(300, 400);

        Assert.Equal(["mousemove", "300", "400"], recorder.Single());
    }

    [Fact]
    public void RelativeMovesAreNeverTranslatedBecauseADeltaHasNoOrigin()
    {
        // The boundary of the fix. A relative delta is a displacement, not a position, so an origin
        // does not apply to it — translating one would turn every drag into a jump to somewhere near
        // the desktop corner.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool, originLeft: -1920, originTop: -50);

        service.MouseMoveRelative(-5, 7);

        Assert.Equal(["mousemove", "-x", "-5", "-y", "7"], recorder.Single());
    }

    [Fact]
    public void ScrollIsNeverTranslatedEither()
    {
        // Same reason as relative motion, and worth its own assertion because the scroll path shares
        // both the mousemove verb and the -x/-y flags with the translated one — the only thing
        // separating them is --wheel.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool, originLeft: -1920);

        service.MouseScroll(0, 240);

        Assert.Equal(["mousemove", "--wheel", "-x", "0", "-y", "2"], recorder.Single());
    }

    [Fact]
    public void KeysAreEvdevCodesWithAPressSuffixOnYdotoolAndNamesOnXdotool()
    {
        const int vkEscape = 0x1B;
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.KeyDown(vkEscape);
        xdotool.KeyDown(vkEscape);

        // Two different encodings of one protocol key code: ydotool wants "<evdev>:1", xdotool wants
        // an XKB keysym name. Nothing about the wire value hints at either.
        Assert.Equal(["key", "1:1"], ydotoolCalls.Single());
        Assert.Equal(["keydown", "Escape"], xdotoolCalls.Single());
    }

    [Fact]
    public void KeyUpIsTheSameEncodingWithTheOppositeSuffix()
    {
        const int vkEscape = 0x1B;
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.KeyUp(vkEscape);
        xdotool.KeyUp(vkEscape);

        Assert.Equal(["key", "1:0"], ydotoolCalls.Single());
        Assert.Equal(["keyup", "Escape"], xdotoolCalls.Single());
    }

    [Fact]
    public void TypedTextIsPassedAfterAnEndOfOptionsMarkerOnBothBackends()
    {
        // A Fact taking both backends rather than a Theory over them: LinuxDesktopTool is internal,
        // and an [InlineData] of one would force this method to be non-public, which xUnit does not
        // discover. Not worth widening a production enum's visibility over.
        var (ydotool, ydotoolCalls) = New(LinuxDesktopTool.Ydotool);
        var (xdotool, xdotoolCalls) = New(LinuxDesktopTool.Xdotool);

        ydotool.TypeText("--version");
        xdotool.TypeText("--version");

        // The "--" is a security property, not a formatting one: text arrives from the network, and
        // without the marker a message beginning with a dash is read as flags by whichever tool is
        // installed. Asserted for both backends because the guard has to hold on both, and text is
        // the one input that is attacker-chosen in full.
        Assert.Equal(["type", "--", "--version"], ydotoolCalls.Single());
        Assert.Equal(["type", "--", "--version"], xdotoolCalls.Single());
    }

    [Fact]
    public void TheCursorQueryAsksXdotoolForShellParseableOutput()
    {
        // The other invocation the seam covers, and the only one whose OUTPUT is consumed rather
        // than discarded. "--shell" is what makes the response key=value lines; without it the tool
        // prints a prose form the parser does not read, so the cursor position would silently stop
        // updating.
        var (service, recorder) = New(LinuxDesktopTool.Xdotool);

        service.GetCursorPosition();

        Assert.Equal(["getmouselocation", "--shell"], recorder.Single());
    }

    [Fact]
    public void AnEmptyStringIsNotWorthAProcess()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.TypeText(string.Empty);

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public void NoToolMeansNoInvocationRatherThanAnInvocationWithNoProgram()
    {
        // The guard in RunTool. Worth pinning because the failure it prevents is passing a null path
        // to Process.Start, and the branch is invisible on any machine that has a tool installed.
        var recorder = new Recorder();
        var service = new LinuxInputSimulationService(
            NullLogger<LinuxInputSimulationService>.Instance,
            new LinuxDesktopBackendStatus(
                DesktopEnvironment: "test",
                IsWaylandSession: false,
                IsKdePlasma: false,
                HasDisplayServer: false,
                InputTool: LinuxDesktopTool.None,
                InputToolPath: null,
                CursorQueryTool: LinuxDesktopTool.None,
                CursorQueryToolPath: null,
                WindowControlTool: LinuxDesktopTool.None,
                WindowControlToolPath: null),
            recorder.Launcher);

        service.MouseDown(MouseButtons.Left);
        service.KeyDown(0x1B);
        service.TypeText("hello");

        Assert.Empty(recorder.Calls);
    }
}
