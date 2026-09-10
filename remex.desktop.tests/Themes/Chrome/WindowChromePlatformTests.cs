using System;
using FluentAssertions;
using Remex.Desktop.Themes.Chrome;
using Xunit;

namespace Remex.Desktop.Tests.Themes.Chrome;

/// <summary>
/// RemEx-5253o: the title-bar fullscreen button was a trap on Windows - the template's only exit
/// (the FullscreenPopover's hover-the-top-edge bar) is a macOS convention, not a Windows/Linux one.
/// </summary>
public class WindowChromePlatformTests
{
    [Fact]
    public void ShowFullScreenButton_IsGatedOnMacOS()
    {
        // Computed independently from the production expression (not copy-pasted from it), so an
        // inverted or platform-swapped guard in WindowChromePlatform still disagrees with this on
        // every non-macOS CI/dev box the suite actually runs on.
        WindowChromePlatform.ShowFullScreenButton.Should().Be(OperatingSystem.IsMacOS(),
            "the button (and its FullscreenPopover twin) must be visible only where the popover's " +
            "hover-the-top-edge exit is the native convention");
    }
}
