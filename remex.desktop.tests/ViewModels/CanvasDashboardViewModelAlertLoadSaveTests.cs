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
/// RemEx-8wpvr.2 (review, HIGH): <see cref="CanvasDashboardViewModel.InitializeAsync"/> called
/// <c>_alertStore.ReplaceAll(profile.SensorAlerts)</c> BEFORE restoring the persisted non-sensor
/// cards, while the constructor had already subscribed <c>OnAlertStoreChanged</c> to
/// <see cref="SensorAlertStore.Changed"/>. <c>ReplaceAll</c> raises <c>Changed</c> unconditionally,
/// so on any profile carrying a configured alert this fired a debounced <c>TriggerSave()</c> with
/// <see cref="CanvasDashboardViewModel.Cards"/> still empty. <c>CanvasLayoutMerge.MergeCards</c> only
/// preserves persisted entries whose <c>CardType == "Sensor"</c>, so the save that landed dropped
/// every non-sensor card (Connection/Actions/Latency) — silently, on every offline (no-host) launch
/// that had at least one alert configured.
/// </summary>
/// <remarks>
/// <see cref="DashboardLayoutService.RequestSave"/> assigns
/// <see cref="DashboardLayoutService.CurrentProfile"/> SYNCHRONOUSLY (only the write to disk is
/// debounced — see its own remarks and <c>CanvasDashboardViewModelLayoutSyncTests</c>), so the
/// corruption this test guards against is observable on <c>CurrentProfile</c> immediately after
/// <see cref="CanvasDashboardViewModel.InitializeAsync"/> returns, with no need to force a flush.
/// </remarks>
public sealed class CanvasDashboardViewModelAlertLoadSaveTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-8wpvr2-").FullName;
    private ThemeService _theme = null!;
    private DashboardLayoutService _layoutService = null!;
    private ShellViewModel _shell = null!;
    private CanvasDashboardViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        // SYNCHRONOUS DISPATCH, same reason as CanvasDashboardViewModelLayoutSyncTests: this assembly
        // has no Avalonia.Headless reference, so nothing drains a real posted callback.
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await _layoutService.LoadAsync();

        // Persist exactly the shape the bug loses: one non-sensor card, no sensor cards yet (those
        // restore lazily from telemetry - see CanvasLayoutMerge's own remarks), plus one configured
        // alert so the load-time ReplaceAll actually has something to fire Changed over.
        var savedProfile = _layoutService.CurrentProfile with
        {
            Cards = new List<CardState>
            {
                new() { CardId = "connection-card", CardType = "Connection", PositionX = 20, PositionY = 20 },
            },
            SensorAlerts = new List<SensorAlert>
            {
                new() { SensorName = "CPU Package", Threshold = 90, Direction = AlertDirection.Above, Severity = AlertSeverity.Warning },
            },
        };
        await _layoutService.SaveAsync(savedProfile);

        // SaveAsync writes to disk but deliberately never assigns CurrentProfile itself (only a load
        // does) - reload so CurrentProfile actually reflects what was just saved.
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

        // A fresh store/tracker for the VM under test - InitializeAsync's ReplaceAll is what should
        // populate this one from the profile just saved above.
        _vm = new CanvasDashboardViewModel(connection, _layoutService, _shell, new SensorAlertStore(), new SensorAlertTracker());
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadingAProfileWithAConfiguredAlertDoesNotDropTheNonSensorCardsStillRestoring()
    {
        // NOT await _vm.InitializeAsync() directly — it awaits Dispatcher.UIThread.InvokeAsync, and
        // this assembly has no Avalonia.Headless reference, so nothing ever drains a real Post.
        // _shell's own HardwareThemeService already constructed a DispatcherTimer above, binding
        // "the" UI thread to whichever pool thread was running the IAsyncLifetime.InitializeAsync
        // setup; the awaited LoadAsync below almost certainly resumes on a different one, so
        // CheckAccess() reads false and the real callback strands in a queue nothing pumps — measured
        // directly: an earlier version of this test hung exactly that way. FinishInitialize is the
        // synchronous, UI-thread half of InitializeAsync's load path split out for exactly this reason
        // (see its own remarks) — invoking it directly by reflection reproduces the load path under
        // test without going anywhere near the dispatcher.
        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });

        // THE REGRESSION: with no telemetry ever arriving, the only way a save could have fired
        // during this InitializeAsync call is through the alert-store load path. If that save ran
        // before the restore loop below finished, the persisted Connection card is gone from
        // CurrentProfile for good - nothing else in the load path calls TriggerSave again.
        _layoutService.CurrentProfile.Cards.Should().Contain(c => c.CardType == "Connection",
            "the alert-store load must never be the reason a persisted non-sensor card disappears " +
            "from the saved profile (RemEx-8wpvr.2)");

        // The restore loop itself must still have put the card back on the live canvas - the fix
        // must not skip restoring it, only stop saving mid-restore.
        _vm.Cards.Should().Contain(c => c.CardType == "Connection",
            "the non-sensor card restore loop must still run during InitializeAsync");
    }
}
