using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-hmigd: an explicit Light/Dark <c>ThemeMode</c> (and a non-default seed) reverted to dark
/// ~25-40s after the app finished loading, with nothing visibly touching the profile in between.
/// </summary>
/// <remarks>
/// <para>
/// THE MECHANISM, pinned by live repro with temporary logging (see the bead notes): on the
/// loopback self-connect every desktop build makes to its own embedded agent at startup, the agent
/// sends a <c>layout_sync</c> message carrying ITS OWN <c>host_dashboard_layout.json</c> — a
/// completely separate, machine-wide file from the per-user <c>dashboard_layout.json</c> this
/// device's <see cref="ThemeService"/> is painted from. <see cref="CanvasDashboardViewModel"/>'s
/// handler for that message, <c>ApplyProfile</c>, applies the synced <c>Cards</c>/grid state
/// (correct) and then persists the WHOLE received <see cref="DashboardProfile"/> — including its
/// <c>Customization</c> — back into local storage via <c>_layoutService.RequestSave(profile)</c>
/// (the bug). <see cref="DashboardLayoutService.RequestSave"/> updates
/// <see cref="DashboardLayoutService.CurrentProfile"/> SYNCHRONOUSLY, so the corruption is
/// immediate in memory; the visible repaint follows whenever anything next reads the file back
/// (observed via the 30s-later silent autosnapshot), which is what made it look like nothing had
/// touched the profile.
/// </para>
/// <para>
/// The fix layers only the fields <c>ApplyProfile</c> actually applies (cards, grid, pinned
/// sensors) onto THIS device's own <see cref="DashboardLayoutService.CurrentProfile"/> before
/// saving, instead of persisting the host's profile wholesale — the same "layer onto CurrentProfile"
/// shape as <c>CanvasDashboardViewModel.TriggerSave</c> and <c>DismissCoachMark</c> already use.
/// </para>
/// <para>
/// <c>ApplyProfile</c> is private and reached in production only through the
/// <c>ConnectionViewModel.LayoutProfileReceived</c> event (which cannot be raised from outside its
/// declaring class) or the constructor-time initial-sync race, both of which are integration
/// details the bug has nothing to do with — so this test invokes <c>ApplyProfile</c> directly via
/// reflection, the same seam style <c>ConnectionViewModelTests</c> already uses for this
/// assembly's other private-method coverage.
/// </para>
/// </remarks>
public sealed class CanvasDashboardViewModelLayoutSyncTests : IAsyncLifetime
{
    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-hmigd-").FullName;
    private ThemeService _theme = null!;
    private DashboardLayoutService _layoutService = null!;
    private ShellViewModel _shell = null!;
    private CanvasDashboardViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        // SYNCHRONOUS DISPATCH, same reason as RemoteViewModelProfileReplacementTests: this
        // assembly has no Avalonia.Headless reference, so nothing drains a real posted callback.
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await _layoutService.LoadAsync();

        // THIS DEVICE'S OWN, explicit, non-default theme — the thing the bug overwrites.
        var localCustomization = _layoutService.CurrentProfile.Customization with
        {
            ThemeMode = ThemeModes.Light,
            AccentColor = "#F5F5F5",
            SchemaVersion = CustomizationMigration.CurrentSchemaVersion,
        };
        await _layoutService.SaveAsync(_layoutService.CurrentProfile with { Customization = localCustomization });

        // SaveAsync writes to disk but deliberately never assigns CurrentProfile itself (only a
        // load does) - reload so CurrentProfile actually reflects what was just saved.
        await _layoutService.ReloadAsync();

        var connection = new ConnectionViewModel();
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();

        _vm = new CanvasDashboardViewModel(connection, _layoutService, _shell, new SensorAlertStore(), new SensorAlertTracker());
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void ReceivingAHostLayoutSyncMustNotOverwriteThisDevicesOwnTheme()
    {
        // THE HOST'S OWN PROFILE — a different device's Customization, exactly like
        // host_dashboard_layout.json vs. the per-user dashboard_layout.json in the live repro.
        var hostProfile = new DashboardProfile
        {
            Customization = new CustomizationSettings
            {
                ThemeMode = ThemeModes.Dark,
                AccentColor = "#00A35D",
                SchemaVersion = CustomizationMigration.CurrentSchemaVersion,
            },
        };

        ApplyProfileMethod.Invoke(_vm, new object[] { hostProfile });

        _layoutService.CurrentProfile.Customization.ThemeMode.Should().Be(ThemeModes.Light,
            "a card-layout sync from the connected host must never overwrite this device's own " +
            "explicit theme mode with the host's");
        _layoutService.CurrentProfile.Customization.AccentColor.Should().Be("#F5F5F5",
            "nor must it overwrite this device's own seed with whatever accent the host's separate " +
            "profile happens to carry");
    }
}
