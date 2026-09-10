using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the media and volume keys through both Linux backends (RemEx-3cnq).
/// </summary>
/// <remarks>
/// <para>
/// Windows needs nothing: <c>WindowsInputSimulationService</c> passes the virtual-key code straight
/// to <c>SendInput</c>, so <c>VK_VOLUME_UP</c> and friends already worked there. Linux translates,
/// and had no entry for any of them — the phone would have sent a key the host silently dropped.
/// </para>
/// <para>
/// TWO TABLES, BECAUSE THERE ARE TWO BACKENDS. The portal and ydotool paths take an evdev keycode;
/// the xdotool path takes an X keysym name. A mapping added to one and not the other produces a
/// feature that works on Wayland and does nothing on X11, or the reverse — which is exactly the kind
/// of half-wiring that looks fine in review.
/// </para>
/// <para>
/// Every value here was read out of <c>/usr/include/linux/input-event-codes.h</c> and
/// <c>/usr/include/X11/XF86keysym.h</c> rather than recalled.
/// </para>
/// </remarks>
public class MediaKeyMappingTests
{
    [Theory]
    [InlineData(0xAD, 113)]  // VK_VOLUME_MUTE      -> KEY_MUTE
    [InlineData(0xAE, 114)]  // VK_VOLUME_DOWN      -> KEY_VOLUMEDOWN
    [InlineData(0xAF, 115)]  // VK_VOLUME_UP        -> KEY_VOLUMEUP
    [InlineData(0xB0, 163)]  // VK_MEDIA_NEXT_TRACK -> KEY_NEXTSONG
    [InlineData(0xB1, 165)]  // VK_MEDIA_PREV_TRACK -> KEY_PREVIOUSSONG
    [InlineData(0xB2, 166)]  // VK_MEDIA_STOP       -> KEY_STOPCD
    [InlineData(0xB3, 164)]  // VK_MEDIA_PLAY_PAUSE -> KEY_PLAYPAUSE
    public void EachMediaKeyHasItsEvdevCode(int protocolKey, int expectedEvdev) =>
        Assert.Equal(expectedEvdev, LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(protocolKey));

    [Theory]
    [InlineData(0xAD, "XF86AudioMute")]
    [InlineData(0xAE, "XF86AudioLowerVolume")]
    [InlineData(0xAF, "XF86AudioRaiseVolume")]
    [InlineData(0xB0, "XF86AudioNext")]
    [InlineData(0xB1, "XF86AudioPrev")]
    [InlineData(0xB2, "XF86AudioStop")]
    [InlineData(0xB3, "XF86MediaPlayPause")]
    public void EachMediaKeyHasItsXkbName(int protocolKey, string expectedName) =>
        Assert.Equal(expectedName, LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(protocolKey));

    [Fact]
    public void PlayPauseIsTheToggleKeysymRatherThanThePlayOne()
    {
        // THE TRAP THIS BEAD WALKED INTO AND BACK OUT OF. XF86AudioPlay is the obvious name and it is
        // WRONG: XF86keysym.h documents it as KEY_PLAYCD / KEY_PLAY — start playing — while
        // VK_MEDIA_PLAY_PAUSE is a toggle. X11's counterpart is XF86MediaPlayPause, annotated
        // _EVDEVK(0x0a4) = 164, which is the same code the evdev table emits for this key.
        //
        // Asserted as a pair so the two backends cannot drift into meaning different things: pressing
        // play/pause on the phone must toggle on X11 and on Wayland alike, not toggle on one and
        // restart playback on the other.
        Assert.Equal("XF86MediaPlayPause", LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(0xB3));
        Assert.Equal(164, LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(0xB3));
    }

    [Fact]
    public void TheTwoTablesAgreeOnEveryKeyTheySupport()
    {
        // A key mapped in one backend and not the other is the half-wiring this suite exists to stop:
        // a feature that works on Wayland and does nothing on X11, or the reverse.
        //
        // SWEEPS THE WHOLE PROTOCOL RANGE rather than just the media keys. A first version iterated a
        // hardcoded list of the seven added here, which made the assertion true but not general -
        // review pointed out that a future NON-media key added to one table only would sail past it.
        // The two tables cover the same set today, so sweeping costs nothing and turns a claim about
        // these seven into an invariant about all of them.
        var evdevOnly = new List<string>();
        var nameOnly = new List<string>();

        for (int key = 0; key <= 0xFF; key++)
        {
            bool hasEvdev = LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(key) >= 0;
            bool hasName = LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(key) is not null;

            if (hasEvdev && !hasName) evdevOnly.Add($"0x{key:X2}");
            if (hasName && !hasEvdev) nameOnly.Add($"0x{key:X2}");
        }

        Assert.True(
            evdevOnly.Count == 0 && nameOnly.Count == 0,
            $"The two Linux key tables disagree. Only the portal/ydotool table knows: "
            + $"[{string.Join(", ", evdevOnly)}]; only the xdotool table knows: "
            + $"[{string.Join(", ", nameOnly)}]. A key in one and not the other is dropped on the "
            + "backend that lacks it, with no error on either end.");
    }

    [WindowsOnlyFact("asserts WindowsInputSimulationService's mapping, which is [SupportedOSPlatform(windows)]")]
    public void WindowsPassesEveryMediaKeyStraightThrough()
    {
        // The bead's premise, checked rather than taken: these already worked on Windows because
        // MapKeyCodeToVirtualKey returns any code <= 255 verbatim, and the media range is 173-179.
        // Pinned so the two platforms stay symmetric - the Linux tables above would otherwise be the
        // only place this feature's key set is written down, and a change to the Windows mapping
        // could quietly narrow it.
        int[] mediaKeys = [0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3];

        Assert.All(mediaKeys, key =>
            Assert.Equal((ushort)key, WindowsInputSimulationService.MapKeyCodeToVirtualKey(key)));
    }
}
