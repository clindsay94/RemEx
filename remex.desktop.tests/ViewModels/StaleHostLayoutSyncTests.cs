using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-jt6w5.8: dashboard_layout.json was rewritten to an older, smaller layout seconds after launch.
/// The PC UI's loopback connection gets an unsolicited <c>layout_sync</c> on connect
/// (<c>PingPongHandler</c> "Sync layout on connect") carrying the machine-wide
/// <c>host_dashboard_layout.json</c>. That file is only a mirror of this UI's own layout, refreshed when a
/// save happens while connected, so it is stale whenever the per-user file changed without a connected
/// save (an offline edit, a save before the loopback came up). Applying it replaced the user's cards and
/// the trailing save wrote them to disk. The per-user file must win over that unsolicited copy; only an
/// explicit Sync request may pull the host's layout. Same harness as <see cref="CardThemeSurvivesSyncTests"/>.
/// </summary>
public sealed class StaleHostLayoutSyncTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ReceiveLayoutSyncMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ReceiveLayoutSync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly string[] LocalCardIds = ["cpu-card", "gpu-card", "ram-card"];

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-stale-host-sync-").FullName;
    private string LayoutPath => Path.Combine(_tempDir, "dashboard_layout.json");
    private DashboardLayoutService _layoutService = null!;
    private CanvasDashboardViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(LayoutPath, theme);
        await _layoutService.LoadAsync();

        // The user's layout: three cards, saved while RemEx was stopped (or before the loopback came up).
        await _layoutService.SaveAsync(_layoutService.CurrentProfile with
        {
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

    /// <summary>The host's stale mirror: an older layout with fewer cards in different places.</summary>
    private DashboardProfile StaleHostProfile() => _layoutService.CurrentProfile with
    {
        Cards = new List<CardState>
        {
            new() { CardId = "cpu-card", CardType = "Sensor", SensorId = "cpu-card", PositionX = 900 },
            new() { CardId = "old-card", CardType = "Sensor", SensorId = "old-card", PositionX = 950 },
        },
    };

    private async Task<List<CardState>> CardsOnDiskAsync()
    {
        await _layoutService.FlushAsync();
        using var reader = new DashboardLayoutService(LayoutPath, new ThemeService { PostToUiThread = action => action() });
        return (await reader.LoadAsync()).Cards ?? new();
    }

    [Fact]
    public async Task AnUnsolicitedHostSyncAfterTheLocalLoadDoesNotReplaceTheSavedLayout()
    {
        FinishInitializeMethod.Invoke(_vm, new object[] { await _layoutService.LoadAsync() });

        ReceiveLayoutSyncMethod.Invoke(_vm, new object[] { StaleHostProfile() });

        var onDisk = await CardsOnDiskAsync();
        onDisk.Select(c => c.CardId).Should().BeEquivalentTo(LocalCardIds,
            "the on-connect layout_sync is a stale mirror and must not overwrite the user's own file");
        onDisk.Single(c => c.CardId == "cpu-card").PositionX.Should().Be(0);
    }

    [Fact]
    public async Task AnUnsolicitedHostSyncThatBeatsTheLocalLoadDoesNotReplaceTheSavedLayout()
    {
        // The launch race: the loopback connects and the host's sync lands before the canvas has loaded.
        ReceiveLayoutSyncMethod.Invoke(_vm, new object[] { StaleHostProfile() });
        FinishInitializeMethod.Invoke(_vm, new object[] { await _layoutService.LoadAsync() });

        var onDisk = await CardsOnDiskAsync();
        onDisk.Select(c => c.CardId).Should().BeEquivalentTo(LocalCardIds,
            "a host sync that arrives before the local load must not be preferred over the user's file");
    }

    [Fact]
    public async Task AnExplicitSyncRequestStillAppliesTheHostLayout()
    {
        FinishInitializeMethod.Invoke(_vm, new object[] { await _layoutService.LoadAsync() });
        var requested = typeof(CanvasDashboardViewModel).GetField("_layoutSyncRequested", BindingFlags.NonPublic | BindingFlags.Instance)!;
        requested.SetValue(_vm, true); // what SyncLayoutAsync sets when it sends layout_request

        ReceiveLayoutSyncMethod.Invoke(_vm, new object[] { StaleHostProfile() });

        var onDisk = await CardsOnDiskAsync();
        onDisk.Select(c => c.CardId).Should().BeEquivalentTo(new[] { "cpu-card", "old-card" },
            "the Sync button is the user asking for the host's layout");
        ((bool)requested.GetValue(_vm)!).Should().BeFalse("one request admits one reply, not every later sync");
    }
}
