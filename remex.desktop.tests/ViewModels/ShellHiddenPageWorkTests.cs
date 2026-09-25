using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Perf audit P3-63 and P3-65: work the shell kept doing for pages that were not on screen. The
/// Logs view model stops following the live log while another page is current, and the fullscreen
/// host only carries content while the chrome is actually hidden. Same shell construction as
/// <c>RemoteDesktopStreamForegroundGatingTests</c>.
/// </summary>
public sealed class ShellHiddenPageWorkTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public ShellHiddenPageWorkTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-p3-hidden-").FullName;
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

    [Fact]
    public void TheLogsPageStopsFollowingWhileAnotherPageIsShownAndResumesOnReturn()
    {
        _shell.NavigateToDiagnosticLogs();
        var logs = _shell.CurrentView.Should().BeOfType<DiagnosticLogsViewModel>().Subject;
        logs.IsLive.Should().BeTrue();

        _shell.NavigateToAbout();
        logs.IsLive.Should().BeFalse("nobody is looking at the log list");

        _shell.NavigateToDiagnosticLogs();
        _shell.CurrentView.Should().BeSameAs(logs);
        logs.IsLive.Should().BeTrue();
    }

    [Fact]
    public void TheImmersiveHostIsEmptyUnlessTheChromeIsHidden()
    {
        _shell.NavigateToRemoteDesktop();
        _shell.ImmersiveView.Should().BeNull("outside fullscreen the hidden host must not build a second view");

        var raised = new List<string?>();
        _shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _shell.IsShellChromeHidden = true;
        _shell.ImmersiveView.Should().BeSameAs(_shell.CurrentView);
        raised.Should().Contain(nameof(ShellViewModel.ImmersiveView));

        raised.Clear();
        _shell.IsShellChromeHidden = false;
        _shell.ImmersiveView.Should().BeNull();
        raised.Should().Contain(nameof(ShellViewModel.ImmersiveView));
    }
}
