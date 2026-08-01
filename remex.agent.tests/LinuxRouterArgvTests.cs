using Remex.Agent.Services.Input.Linux;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the exact argument vector <see cref="LinuxInputBackendRouter"/> hands to <c>xdotool</c>
/// (RemEx-n3z6).
///
/// THE SIBLING CLASS HAS SHIPPED THIS BUG TWICE. RemEx-nb7c sent <c>ydotool click</c> a form the tool
/// could not act on; RemEx-r29r sent <c>ydotool mousemove</c> coordinates <c>getopt</c> silently
/// discarded. Both lived in an interpolated string at a call site, both survived tests that covered
/// the button MAPPING rather than the argv, and both were caught only once the argv itself became
/// assertable (RemEx-fu9n). This class had the identical shape — <c>RunXdotool($"mousedown {x}")</c>
/// — and no such test, which is the whole of this bead.
///
/// NOT SHARING A SUITE OR A LAUNCHER WITH THE SIBLING, deliberately. The two classes route to
/// different backends and have already diverged where it counts: this one has its own
/// <c>ClickCount</c> since RemEx-hnin, and its scroll path still uses X11 wheel buttons where the
/// sibling's ydotool path had to stop. Asserting them separately is the point; a shared helper would
/// assume the agreement these tests exist to check.
///
/// No process is started. <c>XdotoolLauncher</c> stands in for the launcher, so these run on every
/// platform despite the class being <c>[SupportedOSPlatform("linux")]</c> (CA1416 is in the repo-wide
/// NoWarn, and the sibling Linux suites already rely on that).
/// </summary>
public sealed class LinuxRouterArgvTests
{
    private sealed class Recorder
    {
        public List<string[]> Calls { get; } = [];

        public XdotoolLauncher Launcher => arguments => Calls.Add(arguments);

        public string[] Single() => Assert.Single(Calls);
    }

    /// <summary>
    /// A router on the xdotool fallback: no libei, no uinput tablet, nothing else to route to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UinputTabletAvailable: false</c> is load bearing: the constructor calls <c>TryCreate</c> on
    /// the tablet service, which P/Invokes into <c>/dev/uinput</c>, and a unit test must not.
    /// </para>
    /// <para>
    /// <c>EisAvailable: false</c> is NOT, and a first draft of this remark claimed it was — review
    /// disproved it by flipping the flag and watching every test in this file still pass. The reason is worth
    /// keeping rather than just deleting the wrong sentence: every method here does return early on
    /// <c>_eis.IsAvailable</c>, but that property is set only inside <c>LinuxEisInputService.TryOpen</c>,
    /// which is reached only from <c>OpenEisSender</c>, which these tests never call. The capability
    /// flag merely decides whether the constructor LOGS about libei. So the flag states intent and
    /// guards against a future constructor that opens the sender eagerly; it is not what makes these
    /// assertions reach a shell argument today.
    /// </para>
    /// </remarks>
    private static (LinuxInputBackendRouter Router, Recorder Recorder) New()
    {
        var recorder = new Recorder();
        var router = new LinuxInputBackendRouter(
            new LinuxInputCapabilitySet
            {
                Tier = LinuxRemoteDesktopTier.X11Degraded,
                EisAvailable = false,
                PortalNotifyAvailable = false,
                UinputTabletAvailable = false,
                XdotoolPath = "/usr/bin/xdotool",
            },
            logger: null,
            recorder.Launcher);
        return (router, recorder);
    }

    [Fact]
    public void ButtonsUseXdotoolsOneBasedNumbering()
    {
        var (router, recorder) = New();

        router.MouseDown(MouseButtons.Right);

        // 3, not 2. xdotool is 1-based and orders left/middle/right, so the protocol's index 2 for
        // right becomes 3 — the exact class of off-by-one that RemEx-kie3 shipped as a middle-click
        // where the user asked for a left-click.
        Assert.Equal(["mousedown", "3"], recorder.Single());
    }

    [Fact]
    public void ReleaseUsesTheSameNumberAsThePress()
    {
        var (router, recorder) = New();

        router.MouseUp(MouseButtons.Middle);

        Assert.Equal(["mouseup", "2"], recorder.Single());
    }

    [Fact]
    public void AClickIsAPressAndAReleaseInThatOrder()
    {
        // Pinned because MouseClick is the one method here that is composed rather than routed, so a
        // reordering would be invisible to every other assertion in this file.
        var (router, recorder) = New();

        router.MouseClick(MouseButtons.Left);

        Assert.Equal(2, recorder.Calls.Count);
        Assert.Equal(["mousedown", "1"], recorder.Calls[0]);
        Assert.Equal(["mouseup", "1"], recorder.Calls[1]);
    }

    [Fact]
    public void RelativeMovesCarryTheEndOfOptionsMarker()
    {
        var (router, recorder) = New();

        router.MouseMoveRelative(-5, 7);

        // The "--" is what stops xdotool reading a negative delta as a flag. Its absence is the same
        // defect the sibling class had on ydotool, where the tool silently emitted nothing at all
        // (RemEx-r29r) — so this is asserted with a NEGATIVE delta, which is the only value that can
        // tell the two apart.
        Assert.Equal(["mousemove_relative", "--", "-5", "7"], recorder.Single());
    }

    [Fact]
    public void AbsoluteMovesTakeScreenCoordinatesWithNoFlagAtAll()
    {
        var (router, recorder) = New();

        router.MoveMouse(300, 400);

        // No "--absolute" here, unlike the ydotool path: xdotool's mousemove is absolute by default.
        // Two tools, opposite defaults, same verb — which is why each class is asserted separately.
        Assert.Equal(["mousemove", "300", "400"], recorder.Single());
    }

    [Fact]
    public void ScrollIsWheelButtonsHereAndOneProcessPerDetent()
    {
        var (router, recorder) = New();

        router.MouseScroll(0, 240);

        // X11 models the wheel as buttons 4/5 vertically and 6/7 horizontally, so the shape that was
        // wrong for ydotool is right here. Two detents, two presses.
        Assert.Equal(2, recorder.Calls.Count);
        Assert.All(recorder.Calls, c => Assert.Equal(["click", "4"], c));
    }

    [Fact]
    public void ScrollDirectionsMapToTheFourWheelButtons()
    {
        var (router, recorder) = New();

        router.MouseScroll(0, -120);
        router.MouseScroll(120, 0);
        router.MouseScroll(-120, 0);

        // Pinned together because these four numbers are pure convention and a transposition would
        // scroll the wrong way with nothing to indicate it. 4 up / 5 down / 7 right / 6 left.
        Assert.Equal(3, recorder.Calls.Count);
        Assert.Equal(["click", "5"], recorder.Calls[0]);
        Assert.Equal(["click", "7"], recorder.Calls[1]);
        Assert.Equal(["click", "6"], recorder.Calls[2]);
    }

    [Fact]
    public void TheMostNegativeScrollDeltaDoesNotThrow()
    {
        // Math.Abs(int.MinValue) has no representable result and throws; this loop's counter takes
        // the magnitude, which is why ClickCount widens to long (RemEx-hnin). Split from the ceiling
        // assertion below so a failure names which of the two regressed rather than leaving the
        // reader to work it out.
        var (router, recorder) = New();

        router.MouseScroll(0, int.MinValue);

        Assert.NotEmpty(recorder.Calls);
    }

    [Fact]
    public void AHugeScrollCannotSpawnMoreThanTenProcesses()
    {
        var (router, recorder) = New();

        router.MouseScroll(0, -1_000_000);

        // This loop spawns one process per detent, so an unbounded count would be a fork bomb driven
        // from the wire — a million units is 8333 detents before the clamp. Deliberately NOT using
        // int.MinValue here, so this test fails for the ceiling alone and not for the overflow.
        Assert.Equal(10, recorder.Calls.Count);
        Assert.All(recorder.Calls, c => Assert.Equal(["click", "5"], c));
    }

    [Fact]
    public void KeysAreSentByXkbNameNotByProtocolCode()
    {
        const int vkEscape = 0x1B;
        var (router, recorder) = New();

        router.KeyDown(vkEscape);

        // The protocol carries Win32 virtual-key codes; xdotool wants XKB keysym names. Sending 27
        // would press whatever xdotool decides "27" means rather than Escape.
        Assert.Equal(["keydown", "Escape"], recorder.Single());
    }

    [Fact]
    public void AKeyWithNoKnownNameFallsBackToItsNumberRatherThanVanishing()
    {
        // The fallback branch, which is invisible on any key that HAS a name. A malformed code off
        // the wire should produce a harmless no-op invocation, not skip the call and leave the
        // caller believing the key was sent.
        const int unmapped = 0xFFFE;
        var (router, recorder) = New();

        router.KeyUp(unmapped);

        Assert.Equal(["keyup", "65534"], recorder.Single());
    }

    [Fact]
    public void TypedTextIsPassedAfterAnEndOfOptionsMarker()
    {
        var (router, recorder) = New();

        router.TypeText("--window");

        // A security property rather than a formatting one: this text arrives from the network, and
        // without the marker a message beginning with a dash is read as flags. This was the ONE call
        // site here that already used a real argument list, precisely because its payload is
        // attacker-chosen; the rest have now been brought up to it.
        Assert.Equal(["type", "--", "--window"], recorder.Single());
    }
}
