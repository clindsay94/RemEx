using System.Collections.Generic;
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
/// RemEx-8wpvr.2 (review round 2, HIGH): <c>CanvasDashboardViewModel.FinishInitialize</c>'s
/// <c>_pendingSyncProfile != null</c> branch (the host sync arrived before the local
/// <c>_layoutService.LoadAsync()</c> finished — a race the code deliberately handles, see the
/// branch's own remarks) called <c>ApplyProfile</c> without the alert store ever being seeded.
/// Only the else branch's local-load path seeded it. So a pending host sync left
/// <c>_alertStore</c> empty for the rest of the session, and every later save writes
/// <c>SensorAlerts = _alertStore.All.ToList()</c> — the next card drag silently wiped every
/// configured alert from disk. The fix moved the seed into <c>ApplyProfile</c> itself, guarded
/// with <c>_suppressAlertStoreSave</c> (try/finally) so the seed's <c>SensorAlertStore.Changed</c>
/// does not also fire an extra, redundant <c>TriggerSave</c> once <c>OnAlertStoreChanged</c> is
/// subscribed (every <c>ApplyProfile</c> call except the very first, pre-subscription one).
/// </summary>
public sealed class CanvasDashboardViewModelApplyProfileAlertSeedTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PendingSyncProfileField =
        typeof(CanvasDashboardViewModel).GetField("_pendingSyncProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-8wpvr2-applyprofile-").FullName;
    private ThemeService _theme = null!;
    private DashboardLayoutService _layoutService = null!;
    private ShellViewModel _shell = null!;
    private ConnectionViewModel _connection = null!;
    private SensorAlertStore _alertStore = null!;
    private SensorAlertTracker _alertTracker = null!;
    private CanvasDashboardViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        // SYNCHRONOUS DISPATCH — same reason as CanvasDashboardViewModelAlertLoadSaveTests: this
        // assembly has no Avalonia.Headless reference, so nothing ever drains a real posted callback.
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await _layoutService.LoadAsync();

        // Local profile carries NO alerts — the pending host sync below is the only source of
        // alerts, exactly the race the finding describes (host sync wins over an empty local load).
        var localProfile = _layoutService.CurrentProfile with
        {
            Cards = new List<CardState>
            {
                new() { CardId = "connection-card", CardType = "Connection", PositionX = 5, PositionY = 5 },
            },
            SensorAlerts = new List<SensorAlert>(),
        };
        await _layoutService.SaveAsync(localProfile);
        await _layoutService.ReloadAsync();

        _connection = new ConnectionViewModel();
        _alertStore = new SensorAlertStore();
        _alertTracker = new SensorAlertTracker();
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            _connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();

        _vm = new CanvasDashboardViewModel(_connection, _layoutService, _shell, _alertStore, _alertTracker);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PendingHostSyncSeedsTheAlertStoreAndSurvivesASubsequentSave()
    {
        // Simulate the race exactly the way OnLayoutProfileReceivedAsync does: a host profile lands
        // in _pendingSyncProfile before _isInitialized flips true, carrying a configured alert the
        // local profile above does not have.
        var hostProfile = _layoutService.CurrentProfile with
        {
            SensorAlerts = new List<SensorAlert>
            {
                new() { SensorName = "CPU Package", Threshold = 90, Direction = AlertDirection.Above, Severity = AlertSeverity.Warning },
            },
        };
        PendingSyncProfileField.SetValue(_vm, hostProfile);

        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });

        // THE REGRESSION: before the fix, the pending-sync branch called ApplyProfile without ever
        // seeding _alertStore, so it stayed empty for the rest of the session.
        _alertStore.TryGet("CPU Package", out var seeded).Should().BeTrue(
            "FinishInitialize's pending-sync branch must seed the alert store the same as the local-load branch does");
        seeded!.Threshold.Should().Be(90);

        // A subsequent save (e.g. the next card drag) must not silently drop the alert that was just
        // seeded — SensorAlerts must still be populated in what gets persisted.
        _vm.ApplySensorAlert("GPU Hotspot", new SensorAlert { SensorName = "GPU Hotspot", Threshold = 80, Direction = AlertDirection.Above });

        _layoutService.CurrentProfile.SensorAlerts.Should().NotBeEmpty(
            "a save after a pending-sync init must not have lost the alerts the host sync carried");
        _layoutService.CurrentProfile.SensorAlerts.Should().Contain(a => a.SensorName == "CPU Package",
            "the alert seeded from the pending host-sync profile must survive into the next save");
    }

    [Fact]
    public async Task ApplyProfileAfterInitReseedsTheStoreWithoutAnExtraSave()
    {
        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });

        // Init subscribes OnAlertStoreChanged; from here on, ReplaceAll would fire an extra
        // TriggerSave unless ApplyProfile's own seed suppresses it.
        var saveCountBefore = _vm.AlertStoreSaveCount;

        var newProfile = _layoutService.CurrentProfile with
        {
            SensorAlerts = new List<SensorAlert>
            {
                new() { SensorName = "GPU Hotspot", Threshold = 85, Direction = AlertDirection.Above, Severity = AlertSeverity.Critical },
            },
        };

        ApplyProfileMethod.Invoke(_vm, new object[] { newProfile });

        _alertStore.TryGet("GPU Hotspot", out var reseeded).Should().BeTrue(
            "a later ApplyProfile call must reseed the store from the newly-applied profile");
        reseeded!.Threshold.Should().Be(85);

        _vm.AlertStoreSaveCount.Should().Be(saveCountBefore,
            "ApplyProfile's alert-store reseed must not fire an extra save on top of the layout-sync " +
            "save ApplyProfile already issues unconditionally (RemEx-8wpvr.2 review round 2, HIGH)");
    }
}
