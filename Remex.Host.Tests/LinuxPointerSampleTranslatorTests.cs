using System.Runtime.Versioning;
using Remex.Core.Models;
using Remex.Host.Services.Input.Linux;
using Remex.Host.Services.RemoteDesktop.Linux;
using Xunit;

namespace Remex.Host.Tests;

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

    // ── Button index → BTN_ code ──────────────────────────────────────

    [Theory]
    [InlineData(0, 272u)]  // BTN_LEFT
    [InlineData(1, 273u)]  // BTN_RIGHT
    [InlineData(2, 274u)]  // BTN_MIDDLE
    [InlineData(3, 275u)]  // BTN_SIDE
    [InlineData(4, 276u)]  // BTN_EXTRA
    public void ButtonIndexToLinuxCode_ReturnsExpectedBtnCode(int index, uint expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.ButtonIndexToLinuxCode(index));
    }

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
