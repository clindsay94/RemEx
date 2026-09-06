using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Services.Network;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-w6ipy: <c>RemoteViewModel</c> caches a <c>DashboardProfile</c> snapshot from one
/// <c>LoadAsync()</c> call at construction and, on every WOL field edit, writes the whole profile
/// back through <c>_profile with { ... }</c>. A savefile import replaces
/// <c>DashboardLayoutService.CurrentProfile</c> wholesale, but the next WOL edit rebuilt from the
/// stale <c>_profile</c> snapshot and silently reverted everything the import changed. Same shape
/// as RemEx-waqb4's <c>CustomizationViewModel</c> bug, fixed the same way it was diagnosed: the
/// write path now bases on <see cref="DashboardLayoutService.CurrentProfile"/>, and the displayed
/// WOL fields re-read on <see cref="DashboardLayoutService.ProfileReplaced"/>.
/// </summary>
/// <remarks>
/// <c>IAsyncLifetime</c>, not a blocking wait in the constructor, same reason as
/// <c>ProfileReplacementInvalidatesCustomizationVmTests</c>: a constructor cannot await
/// <see cref="DashboardLayoutService.LoadAsync"/> or the savefile-import round trip, and
/// <c>.GetAwaiter().GetResult()</c> there is the exact pattern <c>NoBlockingWaitsInTestsTests</c>
/// bans repo-wide in test code.
/// <para>
/// <c>ShellViewModel.ProfileReplacedDispatch</c> IS SET TO RUN INLINE for the same reason as that
/// suite: this assembly has no <c>Avalonia.Headless</c> reference, so nothing drains a real
/// <c>Dispatcher.UIThread.Post</c>, and <c>RemoteViewModel.LoadWolConfigAsync</c>'s own (unrelated)
/// <c>Dispatcher.UIThread.Post</c> for the constructor-time load is left un-overridden and simply
/// never runs in this process - which is fine, because every assertion here reads
/// <see cref="DashboardLayoutService.CurrentProfile"/> or <c>RemoteViewModel</c>'s own properties
/// after the FIRST <c>ReloadAsync</c> in the test body, by which point that field-population is
/// long since either irrelevant (the write path bases on <c>CurrentProfile</c>, not on whatever the
/// constructor-time load set) or superseded by <c>OnProfileReplaced</c>'s own re-read.
/// </para>
/// </remarks>
public sealed class RemoteViewModelProfileReplacementTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;
    private RemoteViewModel _remote = null!;

    public RemoteViewModelProfileReplacementTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-w6ipy-").FullName;
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

        _remote = new RemoteViewModel(new ConnectionViewModel(), _shell, new NoOpWakeOnLanService(), _layoutService);
    }

    [Fact]
    public async Task ImportRefreshesDisplayedWolFieldsAndASubsequentEditPersistsTheImportedCustomization()
    {
        var importedCornerRadius = _layoutService.CurrentProfile.Customization.CornerRadius + 7;
        await ImportProfileAsync(importedCornerRadius, "11:22:33:44:55:66");

        _remote.WolMacAddress.Should().Be("11:22:33:44:55:66",
            "a savefile import must refresh the displayed WOL fields, not leave them showing " +
            "whatever this view model saw at construction");

        // GlowStrength's counterpart here: edit a WOL field the import itself never touched.
        _remote.WolBroadcastIp = "192.168.1.255";

        _layoutService.CurrentProfile.WolMacAddress.Should().Be("11:22:33:44:55:66",
            "the WOL edit's write path must layer onto the imported profile as its base, not the " +
            "stale constructor-time snapshot");
        _layoutService.CurrentProfile.Customization.CornerRadius.Should().Be(importedCornerRadius,
            "a WOL field edit after an import must not revert the imported customization to the " +
            "pre-import snapshot just because a different field changed");
    }

    private async Task ImportProfileAsync(double cornerRadius, string wolMacAddress)
    {
        var imported = _layoutService.CurrentProfile with
        {
            Customization = _layoutService.CurrentProfile.Customization with { CornerRadius = cornerRadius },
            WolMacAddress = wolMacAddress,
        };

        // The real savefile-import path (RemexSavefileService.ImportDashboardLayoutAsync): SaveAsync
        // followed by the ReloadAsync that reads it back, becomes the new CurrentProfile, and raises
        // ProfileReplaced.
        await _layoutService.SaveAsync(imported);
        await _layoutService.ReloadAsync();
    }

    public Task DisposeAsync()
    {
        _remote.Dispose();
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private sealed class NoOpWakeOnLanService : IWakeOnLanService
    {
        public Task WakeAsync(string macAddress, string broadcastIp = "255.255.255.255", int port = 9)
            => Task.CompletedTask;
    }
}
