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

    private static (LinuxInputSimulationService Service, Recorder Recorder) New(LinuxDesktopTool tool)
    {
        var recorder = new Recorder();
        var service = new LinuxInputSimulationService(
            NullLogger<LinuxInputSimulationService>.Instance,
            Backend(tool),
            recorder.Launcher);
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
    public void AnAbsoluteTargetOnAMonitorLeftOfPrimaryAlsoSurvives()
    {
        // The half of RemEx-r29r that was worse and easier to miss. Virtual-desktop coordinates are
        // legitimately negative for a display positioned left of or above the primary — this repo
        // says so itself in CoordinateValidation.ClampToRange, whose summary records that flooring at
        // zero "makes left/top monitors unreachable" — so the same getopt behaviour meant the cursor
        // could not be placed anywhere on such a monitor.
        //
        // WHAT THIS DOES NOT PROVE, because the distinction cost real effort to find: surviving
        // getopt is not the same as landing in the right place. ydotool has no absolute mode; it
        // emulates one by slamming the pointer to the REL minimum and then moving by the operands,
        // so those operands are an offset from the pointer space's top-left, NOT a virtual-desktop
        // coordinate. On any desktop whose origin is not (0,0) the cursor therefore lands off by the
        // origin — for a positive coordinate too. That is RemEx-dyvd, and it needs a translation
        // this method does not currently have the inputs for.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MoveMouse(-1920, 100);

        Assert.Equal(["mousemove", "--absolute", "-x", "-1920", "-y", "100"], recorder.Single());
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
