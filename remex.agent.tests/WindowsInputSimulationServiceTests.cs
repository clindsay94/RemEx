using System.Runtime.Versioning;
using Remex.Agent.Services.Input;
using Xunit;

namespace Remex.Agent.Tests;

[SupportedOSPlatform("windows")]
public class WindowsInputSimulationServiceTests
{
    [Theory]
    [InlineData(0x0D, 0x0D)] // Enter should stay VK_RETURN, not Avalonia Escape
    [InlineData(0x08, 0x08)] // Backspace
    [InlineData(0x25, 0x25)] // Left arrow should stay VK_LEFT
    [InlineData(0x41, 0x41)] // A
    [InlineData(0x70, 0x70)] // F1
    public void MapKeyCodeToVirtualKey_PrefersRawVirtualKeys(int keyCode, ushort expected)
    {
        Assert.Equal(expected, WindowsInputSimulationService.MapKeyCodeToVirtualKey(keyCode));
    }

    // Verifies classification only (RemEx-9krr) — NOT a confirmation that AltGr produces an
    // AltGr-layer character on a real Windows host. See the KEYEVENTF_EXTENDEDKEY caveat in
    // WindowsInputSimulationService: the extended flag fixes which physical key Windows thinks
    // fired, but real AltGr hardware emits left-Ctrl + right-Alt together, and character-layer
    // translation may key off that combination. Needs a real-device verification pass.
    [Theory]
    [InlineData((ushort)0xA5, true)]  // VK_RMENU (AltGr)
    [InlineData((ushort)0x24, true)]  // VK_HOME
    [InlineData((ushort)0x23, true)]  // VK_END
    [InlineData((ushort)0x21, true)]  // VK_PRIOR (Page Up)
    [InlineData((ushort)0x22, true)]  // VK_NEXT (Page Down)
    [InlineData((ushort)0x2D, true)]  // VK_INSERT
    [InlineData((ushort)0x41, false)] // 'A' — ordinary key
    [InlineData((ushort)0x70, false)] // F1 — not extended
    public void IsExtendedVirtualKey_MatchesWin32DocumentedSet(ushort vk, bool expected)
    {
        Assert.Equal(expected, WindowsInputSimulationService.IsExtendedVirtualKey(vk));
    }
}
