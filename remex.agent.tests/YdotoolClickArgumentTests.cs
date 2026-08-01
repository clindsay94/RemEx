using Remex.Agent.Services.Input;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the argument <c>ydotool click</c> is actually given (RemEx-nb7c).
///
/// WHAT WAS WRONG. The call sites built the argument from an evdev <c>BTN_*</c> code with a literal
/// 'D' or 'U' appended — <c>0x00110D</c> for press, <c>0x00110U</c> for release. Neither is a valid
/// ydotool click argument, and the two are not even wrong in the same way: 'D' happens to be a hex
/// digit so the press form parses, as 0x00110D; 'U' is not a hex digit at all. Note that ydotool
/// did not *reject* the release form — <c>tool_click.c</c> calls <c>strtol(..., NULL, 16)</c> and
/// never checks an endptr, so it silently truncated it to 0x110 and took the do-nothing branch. The
/// press form did nothing either. ydotool masks the argument to a
/// low nibble and ORs it onto BTN_MOUSE (<c>Client/tool_click.c</c>:
/// <c>keycode = (key &amp; 0xf) | 0x110</c>), so 0x00110D selected 0x11D, an unassigned evdev code
/// — and it carried neither the 0x40 nor the 0x80 bit, which is what actually decides whether an
/// event is emitted at all. Both of the tool's guards failed and nothing reached uinput, matching
/// the man page's note that a bare <c>0x00</c> "chooses left button, but does nothing".
///
/// It went unnoticed because ydotool is the third-choice Linux path — the portal is preferred and
/// xdotool is the usual fallback — so it only runs on a Wayland session with no portal and ydotoold
/// present.
///
/// CITED, NOT REMEMBERED. Everything asserted here comes from ydotool's own man page:
/// <c>0x00 - LEFT, 0x01 - RIGHT, 0x02 - MIDDLE, 0x03 - SIDE, 0x04 - EXTR, 0x05 - FORWARD,
/// 0x06 - BACK, 0x07 - TASK, 0x40 - Mouse down, 0x80 - Mouse up</c>, with worked examples
/// <c>0xC0</c> = left click, <c>0x41</c> = right button down, <c>0x82</c> = middle button up. Those
/// three examples are reproduced below as the anchor, so a future edit is checked against upstream's
/// own numbers rather than against this file's opinion. Only 0x00-0x04 are reachable from here:
/// <see cref="MouseButtons"/> defines no forward, back or task index.
/// </summary>
public sealed class YdotoolClickArgumentTests
{
    // Parsed back out of the REAL argument builder the call sites use, so these assertions cover the
    // string that reaches the command line and not merely the mapping behind it.
    private static int Down(int button) => Parse(MouseButtonCodes.YdotoolClickArgument(button, pressed: true));

    private static int Up(int button) => Parse(MouseButtonCodes.YdotoolClickArgument(button, pressed: false));

    private static int Parse(string argument)
    {
        Assert.StartsWith("0x", argument);
        return System.Convert.ToInt32(argument[2..], 16);
    }

    [Fact]
    public void TheManPagesOwnWorkedExamplesComeOutRight()
    {
        // "0x41: right button down" and "0x82: middle button up" — upstream's examples, verbatim.
        Assert.Equal(0x41, Down(MouseButtons.Right));
        Assert.Equal(0x82, Up(MouseButtons.Middle));

        // "0xC0: left button click (down then up)" is the two halves together.
        Assert.Equal(0xC0, Down(MouseButtons.Left) | Up(MouseButtons.Left));
    }

    [Fact]
    public void YdotoolOrdersRightBeforeMiddleUnlikeEveryOtherTableHere()
    {
        // THE TRAP. The protocol vocabulary and all the sibling tables use 0 left, 1 middle,
        // 2 right; ydotool uses 0 LEFT, 1 RIGHT, 2 MIDDLE. Passing the protocol index straight
        // through would swap right-click and middle-click on this backend only — silently, which is
        // exactly what RemEx-kie3 already cost this project once.
        Assert.Equal(0x00, MouseButtonCodes.ToYdotool(MouseButtons.Left));
        Assert.Equal(0x01, MouseButtonCodes.ToYdotool(MouseButtons.Right));
        Assert.Equal(0x02, MouseButtonCodes.ToYdotool(MouseButtons.Middle));

        Assert.NotEqual(MouseButtons.Middle, MouseButtonCodes.ToYdotool(MouseButtons.Middle));
        Assert.NotEqual(MouseButtons.Right, MouseButtonCodes.ToYdotool(MouseButtons.Right));
    }

    [Theory]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.Right)]
    [InlineData(MouseButtons.Side)]
    [InlineData(MouseButtons.Extra)]
    public void EveryArgumentCarriesExactlyOneOfTheTwoActionBits(int button)
    {
        // The defect in one assertion: the old argument had NEITHER bit set, which the man page
        // documents as doing nothing. A press must carry 0x40 and not 0x80, a release the reverse.
        Assert.Equal(MouseButtonCodes.YdotoolDown, Down(button) & 0xC0);
        Assert.Equal(MouseButtonCodes.YdotoolUp, Up(button) & 0xC0);
    }

    [Theory]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.Right)]
    [InlineData(MouseButtons.Side)]
    [InlineData(MouseButtons.Extra)]
    public void TheArgumentStaysInsideTheDocumentedByteAndFormatsAsTwoHexDigits(int button)
    {
        // An evdev code (0x110+) does not fit the documented space at all, so bounding the value is
        // what stops that class of argument coming back. The formatting assertion covers the other
        // half of the old bug: the release form used to render a trailing 'U', which is not a hex
        // digit, so it was not a number in any base.
        foreach (var text in new[]
                 {
                     MouseButtonCodes.YdotoolClickArgument(button, pressed: true),
                     MouseButtonCodes.YdotoolClickArgument(button, pressed: false),
                 })
        {
            Assert.Matches("^0x[0-9A-F]{2}$", text);
        }

        foreach (var argument in new[] { Down(button), Up(button) })
        {
            Assert.InRange(argument, 0x40, 0xFF);
        }
    }

    [Fact]
    public void AnUnknownButtonIndexFallsBackToAWorkingLeftClick()
    {
        // Matches ToEvdev and ToXdotool: a malformed index off the wire should produce an ordinary
        // click rather than tear down the input path — but it must still carry an action bit.
        Assert.Equal(MouseButtonCodes.YdotoolDown, Down(99));
        Assert.Equal(MouseButtonCodes.YdotoolUp, Up(99));
    }
}
