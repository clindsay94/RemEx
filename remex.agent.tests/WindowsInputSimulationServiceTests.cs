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
}
