using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The 2026-09-13 preset wipe, replayed at the call site (RemEx-4kv0g.3): a layout that arrives on
/// <c>ApplyProfile</c> with no card themes must neither reset the live sensor nor be written over the
/// per-user file's themes. Same harness as <see cref="CanvasDashboardViewModelApplyProfileAlertSeedTests"/>.
/// </summary>
public sealed class CardThemeSurvivesSyncTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-cardtheme-sync-").FullName;
    private DashboardLayoutService _layoutService = null!;
    private CanvasDashboardViewModel _vm = null!;

    private static TelemetryPayload Reading(string id) => new()
    {
        Sensors = new List<SensorReading> { new() { Id = id, Name = id, Value = 40, Unit = "°C", Kind = MetricKind.CpuTempC } },
    };

    public async Task InitializeAsync()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), theme);
        await _layoutService.LoadAsync();

        var localProfile = _layoutService.CurrentProfile with
        {
            Cards = new List<CardState>
            {
                new() { CardId = "cpu-card", CardType = "Sensor", SensorId = "CPU Package", CardTheme = SensorCardTheme.Presets[4] }, // Sunset
                new() { CardId = "gpu-card", CardType = "Sensor", SensorId = "GPU Hotspot", CardTheme = SensorCardTheme.Presets[6] }, // Neon Purple, never reports
            },
        };
        await _layoutService.SaveAsync(localProfile);
        await _layoutService.ReloadAsync();

        var connection = new ConnectionViewModel();
        var alertStore = new SensorAlertStore();
        var alertTracker = new SensorAlertTracker();
        var shell = new ShellViewModel(
            _layoutService, theme, connection,
            new ServiceCollection().AddLogging().AddSingleton<SensorAlertStore>().AddSingleton<SensorAlertTracker>().BuildServiceProvider());
        shell.ProfileReplacedDispatch = run => run();
        _vm = new CanvasDashboardViewModel(connection, _layoutService, shell, alertStore, alertTracker);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AHostSyncWithNoCardThemesLeavesTheLiveSensorAndThePerUserFileOnTheirPresets()
    {
        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });
        _vm.ApplyTelemetry(Reading("CPU Package"));

        var card = _vm.Cards.Concat(_vm.StagedCards).First(c => c.Sensor?.Name == "CPU Package");
        card.Sensor!.Theme.Name.Should().Be("Sunset", "the persisted preset applies as soon as the sensor is seen");

        // The host's copy: same cards, same positions, but no colour information at all.
        var hostProfile = _layoutService.CurrentProfile with
        {
            Cards = _layoutService.CurrentProfile.Cards.Select(c => c with { CardTheme = null, PositionX = c.PositionX + 50 }).ToList(),
        };
        ApplyProfileMethod.Invoke(_vm, new object[] { hostProfile });

        card.Sensor.Theme.Name.Should().Be("Sunset", "null carries no colour information and must not reset a live override");

        var saved = _layoutService.CurrentProfile.Cards;
        saved.Single(c => c.CardId == "cpu-card").CardTheme?.Name.Should().Be("Sunset",
            "the sync's save must carry the live sensor's theme forward instead of writing the host's null");
        saved.Single(c => c.CardId == "gpu-card").CardTheme?.Name.Should().Be("Neon Purple",
            "a sensor that has not reported yet falls back to what this file already held for the card");
        saved.Single(c => c.CardId == "cpu-card").PositionX.Should().Be(50, "everything that is not a theme still comes from the host");
    }
}
