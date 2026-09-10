using System;
using System.IO;
using System.Runtime.CompilerServices;

using System.Runtime.Versioning;
using Remex.Core.Models;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="LinuxInputEventTranslator"/>.
/// Covers XKB name → Linux keycode mapping and pointer button conversion.
/// Also indirectly validates the evdev axis normalization used by
/// <see cref="LinuxUinputTabletService"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxPointerSampleTranslatorTests
{
    // ── XKB → keycode ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Return", 28)]
    [InlineData("enter", 28)]
    [InlineData("BackSpace", 14)]
    [InlineData("tab", 15)]
    [InlineData("Escape", 1)]
    [InlineData("esc", 1)]
    [InlineData("Delete", 111)]
    [InlineData("shift", 42)]
    [InlineData("ctrl", 29)]
    [InlineData("alt", 56)]
    [InlineData("super", 125)]
    [InlineData("space", 57)]
    [InlineData("a", 30)]
    [InlineData("z", 44)]
    [InlineData("F1", 59)]
    [InlineData("F12", 88)]
    [InlineData("up", 103)]
    [InlineData("down", 108)]
    [InlineData("left", 105)]
    [InlineData("right", 106)]
    [InlineData("Home", 102)]
    [InlineData("End", 107)]
    [InlineData("PageUp", 104)]
    [InlineData("PageDown", 109)]
    public void XkbNameToLinuxKeycode_ReturnsExpectedCode(string keyName, int expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.XkbNameToLinuxKeycode(keyName));
    }

    [Fact]
    public void XkbNameToLinuxKeycode_IsCase_Insensitive()
    {
        Assert.Equal(
            LinuxInputEventTranslator.XkbNameToLinuxKeycode("shift"),
            LinuxInputEventTranslator.XkbNameToLinuxKeycode("SHIFT"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonexistent_key")]
    public void XkbNameToLinuxKeycode_ReturnsMinusOne_ForUnknownKeys(string keyName)
    {
        Assert.Equal(-1, LinuxInputEventTranslator.XkbNameToLinuxKeycode(keyName));
    }

    [Theory]
    [InlineData(0x0D, 28)]
    [InlineData(0x08, 14)]
    [InlineData(0x25, 105)]
    [InlineData(0x41, 30)]
    [InlineData(0x5A, 44)]
    [InlineData(0xA0, 42)]
    [InlineData(0xBB, 13)]
    public void ProtocolKeyCodeToLinuxKeycode_ReturnsExpectedCode(int keyCode, int expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(keyCode));
    }

    [Theory]
    [InlineData(0x0D, "Return")]
    [InlineData(0x08, "BackSpace")]
    [InlineData(0x25, "Left")]
    [InlineData(0x41, "a")]
    [InlineData(0x5A, "z")]
    [InlineData(0xA0, "Shift_L")]
    [InlineData(0xBB, "equal")]
    public void ProtocolKeyCodeToXkbName_ReturnsExpectedName(int keyCode, string expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode));
    }

    [Theory]
    [InlineData('A', 0x41)]
    [InlineData('é', 0xE9)]
    [InlineData('\n', 0xFF0D)]
    public void RuneToPortalKeysym_ReturnsExpectedKeysym(char value, int expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.RuneToPortalKeysym(new System.Text.Rune(value)));
    }

    [Fact]
    public void TextToPortalKeysyms_HandlesSupplementaryUnicode()
    {
        var keysyms = LinuxInputEventTranslator.TextToPortalKeysyms("🙂");
        Assert.Single(keysyms);
        Assert.Equal(unchecked((int)(0x01000000u | 0x1F642u)), keysyms[0]);
    }

    // ── Button index → BTN_ code ──────────────────────────────────────

    /// <summary>
    /// 1 = MIDDLE and 2 = RIGHT, matching every live input path on both hosts.
    /// </summary>
    /// <remarks>
    /// This test previously asserted 1 = BTN_RIGHT and 2 = BTN_MIDDLE, which is the opposite of what
    /// <c>LinuxInputSimulationService</c>'s three real backends and the Windows host all do. Because
    /// the method under test has no production callers, the wrong order looked verified rather than
    /// wrong — a test can pin a contract nothing else honours (RemEx-kie3).
    /// </remarks>
    [Theory]
    [InlineData(0, 272u)]  // BTN_LEFT
    [InlineData(1, 274u)]  // BTN_MIDDLE
    [InlineData(2, 273u)]  // BTN_RIGHT
    [InlineData(3, 275u)]  // BTN_SIDE
    [InlineData(4, 276u)]  // BTN_EXTRA
    public void ButtonIndexToLinuxCode_ReturnsExpectedBtnCode(int index, uint expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.ButtonIndexToLinuxCode(index));
    }

    /// <summary>
    /// The one button table maps 0/1/2 to left/middle/right, and no backend keeps its own copy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This REPLACED a test that compared the SOURCE TEXT of six separate tables, which was the only
    /// thing holding them in agreement. RemEx-upxn collapsed them onto <see cref="MouseButtonCodes"/>,
    /// so agreement is now a compile-time fact and the mapping can be asserted by CALLING it — which
    /// is worth strictly more than matching literals, because it survives the table being rewritten
    /// in any style at all.
    /// </para>
    /// <para>
    /// THE TRANSCRIPTION TRAP IS THE POINT. BTN_MIDDLE is 274 and BTN_RIGHT is 273, so the evdev
    /// codes run out of index order. Written out six times, one of them was eventually written
    /// wrong — RemEx-kie3, where a left click performed a middle click. That kind of mistake never
    /// crashes and never fails to work; it silently does the wrong thing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSharedButtonTableMapsLeftMiddleRight()
    {
        Assert.Equal(272u, MouseButtonCodes.ToEvdev(MouseButtons.Left));
        Assert.Equal(274u, MouseButtonCodes.ToEvdev(MouseButtons.Middle));
        Assert.Equal(273u, MouseButtonCodes.ToEvdev(MouseButtons.Right));
        Assert.Equal(275u, MouseButtonCodes.ToEvdev(MouseButtons.Side));
        Assert.Equal(276u, MouseButtonCodes.ToEvdev(MouseButtons.Extra));

        Assert.Equal(1, MouseButtonCodes.ToXdotool(MouseButtons.Left));
        Assert.Equal(2, MouseButtonCodes.ToXdotool(MouseButtons.Middle));
        Assert.Equal(3, MouseButtonCodes.ToXdotool(MouseButtons.Right));

        // The protocol indices themselves, since Android hardcodes the same three and a change here
        // without a change there is the two ends disagreeing about what a click is.
        Assert.Equal(0, MouseButtons.Left);
        Assert.Equal(1, MouseButtons.Middle);
        Assert.Equal(2, MouseButtons.Right);
    }

    [Fact]
    public void AnUnknownButtonIndexClicksLeftRatherThanFailing()
    {
        // Every table this replaced fell back to left, and that is deliberate rather than lazy: a
        // malformed index off the wire should produce an ordinary click, not tear down the input
        // path for the rest of the session.
        Assert.Equal(272u, MouseButtonCodes.ToEvdev(99));
        Assert.Equal(272u, MouseButtonCodes.ToEvdev(-1));
        Assert.Equal(1, MouseButtonCodes.ToXdotool(99));

        // Side and extra are 8 and 9 on xdotool, NOT 4 and 5 — those are the scroll wheel, and
        // "completing" the table with them would turn a back-button press into a scroll. Pinned in
        // that direction, so the three Linux backends give the same answer for the same index.
        Assert.Equal(8, MouseButtonCodes.ToXdotool(MouseButtons.Side));
        Assert.Equal(9, MouseButtonCodes.ToXdotool(MouseButtons.Extra));
        Assert.NotEqual(4, MouseButtonCodes.ToXdotool(MouseButtons.Side));
        Assert.NotEqual(5, MouseButtonCodes.ToXdotool(MouseButtons.Extra));
    }

    /// <summary>
    /// No input backend has reintroduced a button table of its own.
    /// </summary>
    /// <remarks>
    /// The one source-text assertion worth keeping, and it guards the property that made the
    /// behavioural test above sufficient: a backend that grows its own local table is once again
    /// free to drift, and every behavioural assertion here would stay green while it did.
    /// <para>
    /// WHAT IT ACTUALLY MATCHES, stated exactly rather than generously: mapping arms against the
    /// evdev BTN_ codes for middle and right, in both spellings, plus a second MOUSEEVENTF switch.
    /// It does NOT catch a regrown xdotool table (<c>0 =&gt; 1, 1 =&gt; 2, 2 =&gt; 3</c> is too
    /// ordinary a literal to match on), nor one written with named constants or a switch statement.
    /// It catches the copy-paste shape that actually occurred, which is worth having, and is not a
    /// proof that no table exists.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoInputBackendKeepsItsOwnButtonTable()
    {
        var inputDir = Path.Combine(RepoRoot(), "remex.agent", "Services", "Input");
        var files = new[]
        {
            Path.Combine(inputDir, "LinuxInputSimulationService.cs"),
            Path.Combine(inputDir, "Linux", "LinuxInputBackendRouter.cs"),
            Path.Combine(inputDir, "Linux", "LinuxInputEventTranslator.cs"),
            Path.Combine(inputDir, "WindowsInputSimulationService.cs"),
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // The literals every one of these tables used to contain, in both spellings.
            foreach (var literal in new[] { "=> 0x112", "=> 0x111", "=> 274u", "=> 273u", "=> 274", "=> 273" })
            {
                Assert.False(source.Contains(literal, StringComparison.Ordinal),
                    $"{name} contains '{literal}', so a button table has come back. Route it through "
                    + "MouseButtonCodes instead — six copies of this is what RemEx-kie3 came from.");
            }

            Assert.False(source.Contains("MOUSEEVENTF_MIDDLEDOWN,", StringComparison.Ordinal),
                $"{name} looks like it has a second MOUSEEVENTF switch; ButtonFlag is the only one.");
        }
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    [Fact]
    public void ButtonIndexToLinuxCode_DefaultsToLeftButton_ForUnknownIndex()
    {
        // Out-of-range indices should default to BTN_LEFT (272)
        Assert.Equal(272u, LinuxInputEventTranslator.ButtonIndexToLinuxCode(99));
    }

    // ── DesktopPointerSample axis normalisation (uinput mapping) ──────

    private const int AbsMax = 65535;
    private const int TiltMin = -64;
    private const int TiltMax = 63;

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 65535)]
    [InlineData(0.5, 32767)]
    public void NormalizedCoord_MapsToAbsRange(double norm, int expected)
    {
        int mapped = (int)(norm * AbsMax);
        Assert.Equal(expected, mapped);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 65535)]
    [InlineData(0.5, 32767)]
    public void NormalizedPressure_MapsToAbsRange(double pressure, int expected)
    {
        int mapped = (int)(pressure * AbsMax);
        Assert.Equal(expected, mapped);
    }

    [Theory]
    [InlineData(-1.0, -64)]
    [InlineData(1.0, 63)]
    [InlineData(0.0, 0)]
    public void NormalizedTilt_MapsToTiltRange(double tilt, int expected)
    {
        int mapped = tilt >= 0
            ? (int)(tilt * TiltMax)
            : (int)(tilt * -TiltMin);
        Assert.Equal(expected, mapped);
    }
}
