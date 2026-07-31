using System.IO;
using System.Runtime.CompilerServices;

using System.Runtime.Versioning;
using Remex.Core.Models;
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
    /// Every button mapping in the repo agrees on 0/1/2 = left/middle/right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real defect behind RemEx-kie3 was not one wrong table but SIX tables with no shared
    /// definition and nothing comparing them: three in <c>LinuxInputSimulationService</c>, two in
    /// <c>LinuxInputBackendRouter</c>, and the Windows flag switch. The live maps are private to
    /// their platform services, and a Linux-only method cannot be invoked from a Windows test run
    /// anyway, so this reads them as SOURCE — the property worth pinning is that the tables agree,
    /// which is a textual fact about the pairs they contain.
    /// </para>
    /// <para>
    /// Asserted as "the inverted pair is absent" as well as "the correct pair is present", and
    /// deliberately NOT by counting matches: pinning how many BTN_-based backends exist would make
    /// correctly ADDING one fail this test, with a message about arity rather than about buttons.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryHostButtonMapping_AgreesOnLeftMiddleRight()
    {
        var inputDir = Path.Combine(RepoRoot(), "remex.agent", "Services", "Input");
        var linuxService = File.ReadAllText(Path.Combine(inputDir, "LinuxInputSimulationService.cs"));
        var router = File.ReadAllText(Path.Combine(inputDir, "Linux", "LinuxInputBackendRouter.cs"));
        var windowsService = File.ReadAllText(Path.Combine(inputDir, "WindowsInputSimulationService.cs"));

        // xdotool numbers buttons 1/2/3; ydotool and the portal use BTN_ codes; Windows uses flags.
        // In every one of them index 1 must be the MIDDLE button and index 2 the RIGHT button.
        foreach (var (name, source) in new[]
                 {
                     ("LinuxInputSimulationService", linuxService),
                     ("LinuxInputBackendRouter", router),
                 })
        {
            Assert.True(source.Contains("1 => 2,") && source.Contains("2 => 3,"),
                $"{name}: xdotool mapping must send index 1 to button 2 (middle) and 2 to 3 (right)");
            Assert.Contains("1 => 274u,", source.Replace("0x112", "274u"));
            Assert.Contains("2 => 273u,", source.Replace("0x111", "273u"));

            // The inversion this bead fixed must not come back under any spelling.
            Assert.DoesNotContain("1 => 0x111", source);   // 1 => BTN_RIGHT
            Assert.DoesNotContain("2 => 0x112", source);   // 2 => BTN_MIDDLE
            Assert.DoesNotContain("1 => 273u", source);
            Assert.DoesNotContain("2 => 274u", source);
        }

        Assert.Contains("1 => MOUSEEVENTF_MIDDLEDOWN", windowsService);
        Assert.Contains("2 => MOUSEEVENTF_RIGHTDOWN", windowsService);
        Assert.Contains("1 => MOUSEEVENTF_MIDDLEUP", windowsService);
        Assert.Contains("2 => MOUSEEVENTF_RIGHTUP", windowsService);
        Assert.DoesNotContain("1 => MOUSEEVENTF_RIGHT", windowsService);
        Assert.DoesNotContain("2 => MOUSEEVENTF_MIDDLE", windowsService);

        // And the one method that is NOT wired to a platform must not drift from them again.
        Assert.Equal(274u, LinuxInputEventTranslator.ButtonIndexToLinuxCode(1)); // BTN_MIDDLE
        Assert.Equal(273u, LinuxInputEventTranslator.ButtonIndexToLinuxCode(2)); // BTN_RIGHT
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
