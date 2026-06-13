using System.Runtime.Versioning;
using Remex.Host.Services.Input.Linux;
using Xunit;

namespace Remex.Host.Tests;

[SupportedOSPlatform("linux")]
public class LinuxInputEventTranslatorTests
{
    [Theory]
    [InlineData(0x0D, 28)]  // Enter (VK_RETURN) -> KEY_ENTER (28)
    [InlineData(0x08, 14)]  // Backspace (VK_BACK) -> KEY_BACKSPACE (14)
    [InlineData(0x1B, 1)]   // Escape (VK_ESCAPE) -> KEY_ESC (1)
    [InlineData(0x20, 57)]  // Space -> KEY_SPACE (57)
    public void ProtocolKeyCodeToLinuxKeycode_MapsCorrectly(int keyCode, int expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.ProtocolKeyCodeToLinuxKeycode(keyCode));
    }

    [Theory]
    [InlineData(0x0D, "Return")]
    [InlineData(0x08, "BackSpace")]
    [InlineData(0x1B, "Escape")]
    [InlineData(0x20, "space")]
    public void ProtocolKeyCodeToXkbName_MapsCorrectly(int keyCode, string expected)
    {
        Assert.Equal(expected, LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(keyCode));
    }
}
