using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Models.Backup;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services.Backup;

/// <summary>
/// RemEx-8wpvr.7. Proves a manual export and re-import round-trips <see cref="SensorAlert"/>s into a
/// FRESH install without a restart.
///
/// <see cref="RemexSavefileService.ExportAsync"/> already carries <see cref="DashboardProfile.SensorAlerts"/>
/// for free — <c>BuildSavefileAsync</c> (RemexSavefileService.cs:~97) reads the whole
/// <see cref="DashboardProfile"/> off <see cref="DashboardLayoutService.LoadAsync"/> and
/// <c>ExportAsync</c> (RemexSavefileService.cs:~120) serializes the whole envelope, so nothing about
/// the export side singles alerts out. What genuinely needs a guard is the far end: <c>ImportAsync</c>
/// -&gt; <c>ImportDashboardLayoutAsync</c> -&gt; <see cref="DashboardLayoutService.SaveAsync"/> -&gt;
/// <see cref="DashboardLayoutService.ReloadAsync"/> (which is what actually replaces
/// <see cref="DashboardLayoutService.CurrentProfile"/> and raises <c>ProfileReplaced</c>) -&gt;
/// (in the running app) <c>SettingsViewModel</c>'s <c>ReloadFromPersistedLayout</c> -&gt;
/// <c>CanvasDashboardViewModel.ApplyProfile</c>/<c>FinishInitialize</c> -&gt;
/// <see cref="SensorAlertStore.ReplaceAll"/>. Both of the latter two already seed from
/// <c>_layoutService.CurrentProfile ?? profile</c> (RemEx-8wpvr.2 review round 3) — this file proves
/// that chain end to end from a real savefile rather than re-asserting the .2 seed logic in isolation.
///
/// <see cref="DashboardLayoutService"/> and <see cref="FileTransferRootSettingsService"/> resolve
/// their storage paths through <c>RemexDataPaths</c>, which <c>build/TestHostStateRedirect.cs</c>
/// (a module initializer compiled into every test assembly) points at a per-run temp directory before
/// any test runs — so calling the real <see cref="RemexSavefileService.ExportAsync"/> /
/// <see cref="RemexSavefileService.ImportAsync"/> here never touches a developer's real
/// <c>dashboard_layout.json</c> or <c>file_transfer_roots.json</c>. <see cref="DashboardLayoutService"/>
/// additionally gets its own per-test temp file below (its internal test-seam constructor) so the
/// source and destination profiles do not collide with each other, or with any other test in this
/// assembly sharing the one redirected file.
/// </summary>
public sealed class SavefileSensorAlertsRoundTripTests : IDisposable
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<string> _tempDirs = new();
    private readonly List<DashboardLayoutService> _layoutServices = new();

    private string CreateTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("remex-8wpvr7-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var layoutService in _layoutServices)
        {
            layoutService.Dispose();
        }

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private DashboardLayoutService CreateLayoutService()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var service = new DashboardLayoutService(Path.Combine(CreateTempDir(), "dashboard_layout.json"), theme);
        _layoutServices.Add(service);
        return service;
    }

    private RemexSavefileService CreateSavefileService(DashboardLayoutService layoutService, IDashboardProfileStorageService? hostStorage = null)
        => new(
            layoutService,
            new LauncherStorageService(CreateTempDir()),
            new FileTransferRootSettingsService(),
            hostStorage ?? new FakeDashboardProfileStorageService());

    private static SensorAlert AboveCritical(string sensorName) => new()
    {
        SensorName = sensorName,
        Threshold = 90,
        Direction = AlertDirection.Above,
        Severity = AlertSeverity.Critical,
    };

    private static SensorAlert BelowWarning(string sensorName) => new()
    {
        SensorName = sensorName,
        Threshold = 10,
        Direction = AlertDirection.Below,
        Severity = AlertSeverity.Warning,
    };

    /// <summary>
    /// Builds a source profile with two alerts and one non-sensor card, exports it through the real
    /// <see cref="RemexSavefileService.ExportAsync"/>, then imports the resulting bytes through a
    /// SECOND, entirely fresh <see cref="RemexSavefileService"/> (its own <see cref="DashboardLayoutService"/>,
    /// its own launcher directory, its own host-profile fake). Returns the destination
    /// <see cref="DashboardLayoutService"/> — whose <see cref="DashboardLayoutService.CurrentProfile"/>
    /// reflects the imported profile once this returns — alongside the import result and the two
    /// source alerts, for callers to assert against.
    /// </summary>
    private async Task<(DashboardLayoutService DestLayout, SavefileImportResult Result, SensorAlert AlertAbove, SensorAlert AlertBelow)>
        ExportThenImportAsync()
    {
        var sourceLayout = CreateLayoutService();
        await sourceLayout.LoadAsync();

        var alertAbove = AboveCritical("CPU Package");
        var alertBelow = BelowWarning("GPU Hotspot");

        var sourceProfile = sourceLayout.CurrentProfile with
        {
            Cards = new List<CardState>
            {
                new() { CardId = "connection-card", CardType = "Connection", PositionX = 5, PositionY = 5 },
            },
            SensorAlerts = new List<SensorAlert> { alertAbove, alertBelow },
        };
        await sourceLayout.SaveAsync(sourceProfile);
        await sourceLayout.ReloadAsync();

        var exportService = CreateSavefileService(sourceLayout);
        using var stream = new MemoryStream();
        await exportService.ExportAsync(stream);
        stream.Position = 0;

        // FRESH doubles for import: a different DashboardLayoutService (own temp file), a different
        // launcher directory, and a fresh host-profile fake — none of them share any state with the
        // export side above.
        var destLayout = CreateLayoutService();
        await destLayout.LoadAsync();

        var importService = CreateSavefileService(destLayout);
        var result = await importService.ImportAsync(stream);

        return (destLayout, result, alertAbove, alertBelow);
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsSensorAlerts_IntoAFreshDashboardProfile()
    {
        var (destLayout, result, alertAbove, alertBelow) = await ExportThenImportAsync();

        result.AppliedSections.Should().Contain(nameof(RemexSavefileSections.DashboardLayout));
        result.Warnings.Should().BeEmpty();

        var imported = destLayout.CurrentProfile;
        imported.SensorAlerts.Should().BeEquivalentTo(new[] { alertAbove, alertBelow },
            "the exported savefile carries the whole DashboardProfile, alerts included, and the import " +
            "must write it back unchanged");
        imported.Cards.Should().Contain(c => c.CardType == "Connection" && c.CardId == "connection-card",
            "the round trip must not drop non-sensor cards while carrying alerts");
    }

    [Fact]
    public async Task ImportedProfile_SeedsSensorAlertStoreAndKeepsNonSensorCard_ThroughFirstLoadAndReloadFromPersistedLayout()
    {
        var (destLayout, _, alertAbove, alertBelow) = await ExportThenImportAsync();

        // Drive the canvas the same way a real "Import savefile" action does once
        // SettingsViewModel's ReloadFromPersistedLayout hands the freshly-reloaded profile to the
        // canvas — FinishInitialize/ApplyProfile are reflection-invoked for the same reason
        // CanvasDashboardViewModelAlertLoadSaveTests and CanvasDashboardViewModelApplyProfileAlertSeedTests
        // do: this assembly has no Avalonia.Headless reference, so nothing pumps a real
        // Dispatcher.UIThread.InvokeAsync — and ReloadFromPersistedLayout itself branches on
        // Dispatcher.UIThread.CheckAccess(), which is exactly the dispatcher this test cannot pump.
        // ApplyProfile is the seam ReloadFromPersistedLayout calls once that check resolves
        // synchronously, so invoking it directly exercises the same reseed logic without needing a
        // dispatcher pump.
        var connection = new ConnectionViewModel();
        var alertStore = new SensorAlertStore();
        var alertTracker = new SensorAlertTracker();
        var theme = new ThemeService { PostToUiThread = action => action() };
        var shell = new ShellViewModel(
            destLayout,
            theme,
            new HardwareThemeService(theme),
            connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider())
        {
            ProfileReplacedDispatch = run => run(),
        };
        var vm = new CanvasDashboardViewModel(connection, destLayout, shell, alertStore, alertTracker);

        // First-load / restart path.
        FinishInitializeMethod.Invoke(vm, new object[] { destLayout.CurrentProfile });

        // Two independently-failing assertions: the store must carry BOTH imported alerts (this is
        // the part a dropped-on-import defect breaks), and the restore loop must still have put the
        // non-sensor card back on the canvas (this is the part an unrelated regression in the
        // non-sensor restore loop would break instead).
        alertStore.All.Should().BeEquivalentTo(new[] { alertAbove, alertBelow },
            "importing a savefile must repopulate SensorAlertStore through the first-load path");
        vm.Cards.Should().Contain(c => c.CardType == "Connection" && c.CardId == "connection-card",
            "the non-sensor card carried by the same import must still be restored onto the canvas");

        // No-restart path: mimic the real "Import savefile" action running against an ALREADY-LIVE
        // canvas — SettingsViewModel.ReloadFromPersistedLayout -> CanvasDashboardViewModel.ApplyProfile
        // is what actually runs then, not FinishInitialize. Use a second, independent store/tracker/vm
        // over the SAME destLayout so this assertion can only pass if ApplyProfile itself seeds the
        // store — calling ReplaceAll on the first vm's store instead would fire its subscribed
        // OnAlertStoreChanged handler and persist an empty alert list back onto destLayout, corrupting
        // the very profile this second call reads.
        var alertStore2 = new SensorAlertStore();
        var alertTracker2 = new SensorAlertTracker();
        var vm2 = new CanvasDashboardViewModel(connection, destLayout, shell, alertStore2, alertTracker2);

        ApplyProfileMethod.Invoke(vm2, new object[] { destLayout.CurrentProfile });

        alertStore2.All.Should().BeEquivalentTo(new[] { alertAbove, alertBelow },
            "importing a savefile must also repopulate SensorAlertStore through the no-restart " +
            "ReloadFromPersistedLayout -> ApplyProfile path, without requiring an app restart");
        vm2.Cards.Should().Contain(c => c.CardType == "Connection" && c.CardId == "connection-card",
            "the non-sensor card must be restored again on the no-restart reload path too");
    }

    private sealed class FakeDashboardProfileStorageService : IDashboardProfileStorageService
    {
        private readonly DashboardProfile _profile;

        public FakeDashboardProfileStorageService(DashboardProfile? profile = null)
        {
            _profile = profile ?? new DashboardProfile();
        }

        public DashboardProfile? SavedProfile { get; private set; }

        public Task<DashboardProfile> LoadProfileAsync() => Task.FromResult(_profile);

        public Task SaveProfileAsync(DashboardProfile profile)
        {
            SavedProfile = profile;
            return Task.CompletedTask;
        }
    }
}
