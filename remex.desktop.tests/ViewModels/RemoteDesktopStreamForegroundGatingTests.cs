using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Perf audit P0-3: the desktop Remote Desktop stream kept receiving and decoding after the user
/// left the RD page or hid the window to the tray, because navigation only swapped
/// <c>CurrentView</c> and the view's detach only unsubscribed. The shell now stops the stream (via
/// the existing <c>StopStreamAsync</c> path, so the host's StreamSerial guard is unchanged) when the
/// page loses the foreground, and asks for it back when the page regains it.
/// </summary>
/// <remarks>
/// No live socket here: with no open WebSocket, <c>RemoteDesktopService</c> sends are no-ops, so the
/// stop runs to completion synchronously, and the resume is refused by <c>CanStartStream</c> (not
/// connected). <see cref="RemoteDesktopViewModel.IsStreamPausedForBackground"/> is what shows the
/// resume was armed and then consumed. Same shell construction as
/// <c>RemoteDesktopViewModelProfileReplacementTests</c>.
/// </remarks>
public sealed class RemoteDesktopStreamForegroundGatingTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public RemoteDesktopStreamForegroundGatingTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-p03-rd-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private RemoteDesktopViewModel OpenStreamingRemoteDesktop()
    {
        _shell.NavigateToRemoteDesktop();
        var rd = _shell.CurrentView.Should().BeOfType<RemoteDesktopViewModel>().Subject;
        rd.IsStreaming = true;
        return rd;
    }

    [Fact]
    public void LeavingTheRemoteDesktopPageStopsTheStreamAndArmsAResume()
    {
        var rd = OpenStreamingRemoteDesktop();

        _shell.NavigateToAbout();

        rd.IsStreaming.Should().BeFalse("leaving the RD page must stop the stream, not keep decoding off-screen");
        rd.IsStreamPausedForBackground.Should().BeTrue("coming back to the page must restart the stream");
    }

    [Fact]
    public void ReturningToTheRemoteDesktopPageConsumesTheResume()
    {
        var rd = OpenStreamingRemoteDesktop();
        _shell.NavigateToAbout();

        _shell.NavigateToRemoteDesktop();

        rd.IsStreamPausedForBackground.Should().BeFalse("returning to the page must re-request the stream");
    }

    [Fact]
    public void HidingTheWindowWhileOnTheRemoteDesktopPageStopsTheStream()
    {
        var rd = OpenStreamingRemoteDesktop();

        _shell.IsWindowVisible = false;

        rd.IsStreaming.Should().BeFalse("a tray-hidden or minimized window must not keep decoding frames");
        rd.IsStreamPausedForBackground.Should().BeTrue();

        _shell.IsWindowVisible = true;

        rd.IsStreamPausedForBackground.Should().BeFalse("restoring the window must re-request the stream");
    }

    [Fact]
    public void HidingTheWindowOnAnotherPageLeavesAnIdleRemoteDesktopAlone()
    {
        _shell.NavigateToRemoteDesktop();
        var rd = (RemoteDesktopViewModel)_shell.CurrentView!;
        _shell.NavigateToAbout();

        _shell.IsWindowVisible = false;
        _shell.IsWindowVisible = true;

        rd.IsStreaming.Should().BeFalse();
        rd.IsStreamPausedForBackground.Should().BeFalse("a stream that was never running has nothing to resume");
    }

    [Fact]
    public async Task AStreamTheUserStoppedIsNotResumedOnReturn()
    {
        var rd = OpenStreamingRemoteDesktop();
        await rd.StopStreamCommand.ExecuteAsync(null);

        _shell.NavigateToAbout();

        rd.IsStreamPausedForBackground.Should().BeFalse("only a stream the shell paused is resumed");
    }
}
