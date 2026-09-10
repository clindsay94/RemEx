using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The tray tooltip carries the phone-presence fact, not a constant (RemEx-3s4v).
/// </summary>
/// <remarks>
/// RemEx closes to the tray by default, so for most of its running life the tooltip IS the status
/// surface — and it said "RemEx Desktop - Remote Execution" whether or not a phone was attached.
/// </remarks>
public class TrayTooltipTests
{
    [Fact]
    public void ItCarriesBothTheProductAndThePresence()
    {
        var composed = TrayTooltip.Compose("RemEx", "Pixel 9 connected");

        composed.Should().Contain("RemEx");
        composed.Should().Contain("Pixel 9 connected",
            "the presence reading is the part a constant tooltip could never give");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoReadingYetItIsTheProductNameAlone(string? presence)
    {
        // The monitor publishes on its first refresh, so there is a window at startup with nothing to
        // say. A tooltip reading "RemEx — " with nothing after it is worse than the constant it
        // replaced, and a trailing separator is exactly what a naive concat produces.
        TrayTooltip.Compose("RemEx", presence).Should().Be("RemEx");
    }

    [Fact]
    public void AppFeedsItFromPresenceRatherThanTheLoopbackLink()
    {
        // THE BEAD'S ONE EXPLICIT PROHIBITION. Connection.IsConnected is the desktop's own socket to
        // its embedded host, up essentially always — a tooltip fed from it tells the user nothing,
        // which is the loopback conflation RemEx-porg exists to fix. A source scan because TrayIcon
        // is declared in App.axaml and there is no headless way to construct one.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml.cs"));

        app.Should().Contain("TrayTooltip.Compose(", "the tooltip must be composed, not left constant");
        app.Should().Contain("PhonePresenceMonitor.Instance.PresenceText",
            "and fed from phone presence, which is the fact the shell's dot shows");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
