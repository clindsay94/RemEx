using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The Home footer must not say "No phone connected" over "Status: Connected" (RemEx-pp4cm.23).
/// </summary>
public class HomeFooterStatusTests
{
    [Fact]
    public void LinkUp_NoPhone_SaysItIsWaitingForOne() =>
        HomeViewModel.LinkStatusKey(linkConnected: true, hostUpWithNoPhone: true).Should().Be("Home_WaitingForPhone");

    [Theory]
    [InlineData(true, false)]   // a phone is attached: the link's own "Connected" is true
    [InlineData(false, true)]   // the link is down or connecting: its own text says so
    [InlineData(false, false)]
    public void OtherwiseTheLinksOwnStatusShows(bool linkConnected, bool hostUpWithNoPhone) =>
        HomeViewModel.LinkStatusKey(linkConnected, hostUpWithNoPhone).Should().BeNull();

    [Fact]
    public void TheFooterBindsTheReconciledLine_NotTheRawLinkStatus()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));
        xaml.Should().Contain("{Binding LinkStatusText}");
        xaml.Should().NotContain("{Binding Connection.StatusText}",
            "the raw link status is the in-process host's and reads 'Connected' with no phone attached");
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
