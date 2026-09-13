using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Review, MEDIUM, RemEx-rjnbo.1: <c>TrayFlyoutViewModel.OnlineDeviceCount</c> used to refresh only
/// inside <c>RebuildTiles</c> (on show / <c>IsPhoneAttached</c> / locale), and a PINNED flyout is
/// never re-shown (TrayFlyoutViewModel.cs:126) - so the badge froze the moment a second paired
/// device changed state without <c>IsPhoneAttached</c> itself flipping.
/// </summary>
/// <remarks>
/// Going from one attached phone to two keeps <c>IsPhoneAttached</c> AND <c>State</c> at
/// true/PhoneAttached both times (<see cref="PhonePresenceMonitor"/>'s own contract), but
/// <c>DeviceName</c> moves from the phone's name to null - <see cref="PhonePresence"/> only names a
/// device when exactly one is attached. That is the "presence signal" this test drives: calling
/// <see cref="PhonePresenceMonitor.Refresh"/> directly, the same call the production poll makes, so
/// it raises <c>PhonePresenceMonitor.PropertyChanged</c> for <c>DeviceName</c> without ever touching
/// <c>IsPhoneAttached</c> - proving the fix's new handler, not the existing IsPhoneAttached-filtered
/// one, is what moves <see cref="TrayFlyoutViewModel.OnlineDeviceCount"/>.
/// </remarks>
public sealed class TrayFlyoutOnlineDeviceCountTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-rjnbo1-tray-").FullName;
    private readonly IServiceProvider? _savedHost = App.EmbeddedHostServices;
    private readonly MutablePairedDeviceSource _devices = new([Row("phone-a", online: true)]);
    private ShellViewModel _shell = null!;
    private TrayFlyoutViewModel _tray = null!;

    public async Task InitializeAsync()
    {
        App.EmbeddedHostServices = new Provider(
            new MutableSessionSource([Phone("192.168.1.42", "Pixel 9")]), _devices);
        PhonePresenceMonitor.Instance.Refresh();

        var theme = new ThemeService { PostToUiThread = action => action() };
        var layoutService = new DashboardLayoutService(
            Path.Combine(_tempDir, "dashboard_layout.json"), theme);
        await layoutService.LoadAsync();

        var connection = new ConnectionViewModel();
        _shell = new ShellViewModel(
            layoutService,
            theme,
            connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider(),
            transferQueuePost: action => action());
        _shell.ProfileReplacedDispatch = run => run();
        _shell.DiagnosticsLogDispatch = run => run();

        var home = new HomeViewModel(connection, _shell);
        _tray = new TrayFlyoutViewModel(_shell, home, new FakeLauncherStorage());
    }

    public Task DisposeAsync()
    {
        _tray.Dispose();
        _shell.Dispose();
        App.EmbeddedHostServices = _savedHost;
        PhonePresenceMonitor.Instance.Refresh();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void APresenceSignalAfterBuildUpdatesTheCountWithoutRebuildingTiles()
    {
        _tray.OnlineDeviceCount.Should().Be(1, "one paired device is online at construction time");
        var tilesBeforeSignal = _tray.Tiles;

        // The second device coming online: the paired-device source now reports two, and a second
        // phone session moves PhonePresenceMonitor from OnePhone to SeveralPhones.
        _devices.Rows = [Row("phone-a", online: true), Row("phone-b", online: true)];
        var sessionSource = new MutableSessionSource(
            [Phone("192.168.1.42", "Pixel 9"), Phone("192.168.1.43", "Pixel 8")]);
        App.EmbeddedHostServices = new Provider(sessionSource, _devices);
        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.IsPhoneAttached.Should().BeTrue(
            "one phone or two, a phone is still attached either way - this must not be the signal");
        PhonePresenceMonitor.Instance.DeviceName.Should().BeNull(
                "PhonePresence only names a device when exactly one phone is attached - this is the " +
                "property that actually changed and drove the fix's handler");

        _tray.OnlineDeviceCount.Should().Be(2,
            "the pinned flyout's count must follow a live presence signal, not only IsPhoneAttached");
        _tray.Tiles.Should().BeSameAs(tilesBeforeSignal,
            "only OnlineDeviceCount should refresh from this signal - RebuildTiles must not re-run");
    }

    private static ClientSession Phone(string address, string? name) => new(address, name);

    private static PairedDeviceRow Row(string clientId, bool online) =>
        new(clientId, DeviceName: null, NameOverride: null, FirstPairedUtc: null, LastSeenUtc: null,
            IsOnline: online);

    private sealed class MutableSessionSource(IReadOnlyList<ClientSession> sessions) : IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Snapshot() => sessions;
    }

    private sealed class MutablePairedDeviceSource(IReadOnlyList<PairedDeviceRow> rows) : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> Rows { get; set; } = rows;
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => Rows;
    }

    /// <summary>
    /// Empty by construction: this test file exercises the online-device-count seam only, not the
    /// toolbar's app-shortcut behaviour (see <c>TrayFlyoutViewModelToolbarTests</c> for that).
    /// </summary>
    private sealed class FakeLauncherStorage : ILauncherStorageService
    {
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>());
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private sealed class Provider(IClientSessionSource sessions, IPairedDeviceSource devices) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType switch
        {
            _ when serviceType == typeof(IClientSessionSource) => sessions,
            _ when serviceType == typeof(IPairedDeviceSource) => devices,
            _ => null,
        };
    }
}
