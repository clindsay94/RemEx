using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-w6ipy: <c>RemoteDesktopViewModel</c> snapshots Quality/TargetFps/Scale off
/// <c>DashboardLayoutService.CurrentProfile</c> once, in its own constructor. Its save path
/// (<c>PersistStreamSettings</c>) already rebuilds from the LIVE <c>CurrentProfile</c>, so only
/// those three displayed fields go stale after a savefile import - a stream-settings Apply after an
/// import would otherwise write back the pre-import Quality/Fps/Scale until the user happened to
/// touch one of those three sliders. Fixed by re-reading the three fields, in place, on
/// <see cref="DashboardLayoutService.ProfileReplaced"/> - deliberately NOT by dropping and rebuilding
/// the cached view model the way <c>CustomizationViewModel</c> is, since this one can hold a live
/// stream that a mid-session rebuild would tear down.
/// </summary>
/// <remarks>
/// Same shape as <c>ProfileReplacementInvalidatesCustomizationVmTests</c> and
/// <c>RemoteViewModelProfileReplacementTests</c>: <c>IAsyncLifetime</c> instead of a blocking wait in
/// the constructor (<c>NoBlockingWaitsInTestsTests</c>), and <c>ShellViewModel.ProfileReplacedDispatch</c>
/// overridden to run inline because this assembly has no <c>Avalonia.Headless</c> reference to pump a
/// real <c>Dispatcher.UIThread.Post</c>.
/// </remarks>
public sealed class RemoteDesktopViewModelProfileReplacementTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;
    private RemoteDesktopViewModel _remoteDesktop = null!;

    public RemoteDesktopViewModelProfileReplacementTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-w6ipy-rd-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            new ConnectionViewModel(),
            new ServiceCollection().BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();

        _remoteDesktop = new RemoteDesktopViewModel(new ConnectionViewModel(), _shell);
    }

    [Fact]
    public async Task ImportRefreshesQualityFpsAndScaleWithoutTouchingTheStream()
    {
        var imported = _layoutService.CurrentProfile with
        {
            StreamQuality = 42,
            StreamFps = 30,
            StreamScale = 0.5,
        };
        await _layoutService.SaveAsync(imported);
        await _layoutService.ReloadAsync();

        _remoteDesktop.Quality.Should().Be(42,
            "a savefile import must refresh the displayed Quality, not leave the constructor-time " +
            "snapshot showing");
        _remoteDesktop.TargetFps.Should().Be(30,
            "a savefile import must refresh the displayed TargetFps");
        _remoteDesktop.Scale.Should().Be(0.5,
            "a savefile import must refresh the displayed Scale (snapped the same way construction snaps it)");
        _remoteDesktop.IsStreaming.Should().BeFalse(
            "re-reading the three settings fields must never touch a live stream");
    }

    public Task DisposeAsync()
    {
        _remoteDesktop.Dispose();
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }
}
