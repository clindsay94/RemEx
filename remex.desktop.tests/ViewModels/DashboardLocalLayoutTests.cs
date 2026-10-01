using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-sydzo: the dashboard has exactly one layout source, this device's own
/// <c>dashboard_layout.json</c>. The Sync button and the host's mirror copy it pulled from are gone
/// (RemEx-jt6w5.8 showed that copy could only ever be the same layout or an older one). These pin the
/// two halves that must survive the removal: launch loads the user's file, and an edit saves back to it.
/// Same harness shape as <see cref="CardThemeSurvivesSyncTests"/>.
/// </summary>
public sealed class DashboardLocalLayoutTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TriggerSaveMethod =
        typeof(CanvasDashboardViewModel).GetMethod("TriggerSave", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly string[] LocalCardIds = ["cpu-card", "gpu-card", "ram-card"];
    private const int SavedGridSize = 37;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-local-layout-").FullName;
    private string LayoutPath => Path.Combine(_tempDir, "dashboard_layout.json");
    private DashboardLayoutService _layoutService = null!;
    private CanvasDashboardViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(LayoutPath, theme);
        await _layoutService.LoadAsync();

        // The user's own layout: three cards and a non-default grid size.
        await _layoutService.SaveAsync(_layoutService.CurrentProfile with
        {
            GridSize = SavedGridSize,
            Cards = LocalCardIds
                .Select((id, i) => new CardState { CardId = id, CardType = "Sensor", SensorId = id, PositionX = i * 100 })
                .ToList(),
        });
        await _layoutService.ReloadAsync();

        var connection = new ConnectionViewModel();
        var shell = new ShellViewModel(
            _layoutService, theme, connection,
            new ServiceCollection().AddLogging().AddSingleton<SensorAlertStore>().AddSingleton<SensorAlertTracker>().BuildServiceProvider());
        shell.ProfileReplacedDispatch = run => run();
        _vm = new CanvasDashboardViewModel(connection, _layoutService, shell, new SensorAlertStore(), new SensorAlertTracker());
    }

    public Task DisposeAsync()
    {
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private async Task<DashboardProfile> ProfileOnDiskAsync()
    {
        await _layoutService.FlushAsync();
        using var reader = new DashboardLayoutService(LayoutPath, new ThemeService { PostToUiThread = action => action() });
        return await reader.LoadAsync();
    }

    [Fact]
    public async Task LaunchLoadsTheUsersOwnLayoutFile()
    {
        FinishInitializeMethod.Invoke(_vm, new object[] { await _layoutService.LoadAsync() });

        _vm.GridSize.Should().Be(SavedGridSize, "the canvas takes its settings from the user's own layout file");

        var onDisk = await ProfileOnDiskAsync();
        onDisk.Cards!.Select(c => c.CardId).Should().BeEquivalentTo(LocalCardIds,
            "loading the layout must leave the user's file as it was");
    }

    [Fact]
    public async Task AnEditSavesBackToTheUsersOwnLayoutFile()
    {
        FinishInitializeMethod.Invoke(_vm, new object[] { await _layoutService.LoadAsync() });

        _vm.GridSize = 48;
        TriggerSaveMethod.Invoke(_vm, null);

        var onDisk = await ProfileOnDiskAsync();
        onDisk.GridSize.Should().Be(48, "an edit on the canvas is saved to the user's own layout file");
        onDisk.Cards!.Select(c => c.CardId).Should().Contain(LocalCardIds,
            "saving must keep the cards that were loaded from the file");
        onDisk.Cards!.Single(c => c.CardId == "gpu-card").PositionX.Should().Be(100);
    }

    [Fact]
    public void TheDashboardOffersNoSyncFromTheHost()
        => typeof(CanvasDashboardViewModel).GetProperty("SyncLayoutCommand").Should().BeNull(
            "the Sync button was removed (RemEx-sydzo); the per-user file is the only layout source");
}
