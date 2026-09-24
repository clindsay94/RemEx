using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Perf audit P0-9: <c>TaskManagerView.OnLoaded</c>/<c>OnUnloaded</c> only gate visual-tree
/// attachment, not the window being minimized or hidden to the tray, so a cached-but-still-loaded
/// Task Manager page kept polling and rebuilding its process list every 2s with nothing on screen.
/// <c>PollAsync</c> now waits on <c>ShellViewModel.IsWindowVisible</c> instead of polling while hidden.
/// </summary>
/// <remarks>Same shell construction as <c>RemoteDesktopStreamForegroundGatingTests</c> (P0-3).</remarks>
public sealed class TaskManagerWindowVisibilityGatingTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;
    private TaskManagerViewModel _vm = null!;

    public TaskManagerWindowVisibilityGatingTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-p09-taskmgr-").FullName;
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

        _vm = new TaskManagerViewModel(_shell.Connection, _shell);
    }

    public Task DisposeAsync()
    {
        _vm.Dispose();
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PollingDoesNotRefreshWhileTheWindowIsHidden()
    {
        // With no live socket, RefreshProcessesAsync flips IsLoading false once IsConnected reads
        // false (see its own no-reply-will-ever-arrive comment) - so if a refresh ran at all while
        // hidden, IsLoading would already be false well inside this window. Staying true is what
        // proves the loop stayed parked in WaitForWindowVisibleAsync instead of ticking through
        // refresh+delay on the old 2s cadence.
        _shell.IsWindowVisible = false;
        _vm.IsLoading.Should().BeTrue("no refresh has run yet");
        _vm.StartPolling();

        await Task.Delay(200);
        _vm.IsLoading.Should().BeTrue("the window is hidden, so PollAsync must still be parked, not refreshing");

        _vm.StopPolling();
    }

    [Fact]
    public async Task RestoringTheWindowResumesPollingImmediately()
    {
        _shell.IsWindowVisible = false;
        _vm.StartPolling();
        await Task.Delay(20);
        _vm.IsLoading.Should().BeTrue("still hidden, nothing has refreshed");

        // Flipping visible must wake the parked wait immediately, not require the next tick of some
        // still-running timer - there is none anymore, so the only path to IsLoading flipping false
        // here is WaitForWindowVisibleAsync's PropertyChanged handler firing.
        _shell.IsWindowVisible = true;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (_vm.IsLoading && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        _vm.IsLoading.Should().BeFalse("becoming visible must wake the wait and run a refresh promptly");

        _vm.StopPolling();
    }

    [Fact]
    public void DisposeStopsPollingEvenWhileHidden()
    {
        _shell.IsWindowVisible = false;
        _vm.StartPolling();
        // Must not hang: Dispose -> StopPolling cancels the token that WaitForWindowVisibleAsync is
        // awaiting on, so cancellation - not a visibility flip - is what unblocks a hidden loop too.
        _vm.Dispose();
    }
}
