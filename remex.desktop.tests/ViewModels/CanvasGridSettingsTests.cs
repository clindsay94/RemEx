using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Snap to grid and grid size moved from Personalize › Layout to the Sensors page (RemEx-172ba). The
/// canvas now saves them itself, onto the same two profile fields, built from
/// <see cref="DashboardLayoutService.CurrentProfile"/> the way LayoutSettingsViewModel did.
/// Harness shape from <see cref="DashboardLocalLayoutTests"/>.
/// </summary>
public sealed class CanvasGridSettingsTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PendingProfileField =
        typeof(DashboardLayoutService).GetField("_pendingProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-172ba-grid-").FullName;
    private DashboardLayoutService _layoutService = null!;
    private ShellViewModel _shell = null!;
    private CanvasDashboardViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), theme);
        await _layoutService.LoadAsync();
        await _layoutService.SaveAsync(_layoutService.CurrentProfile with { GridSize = 40, IsSnapToGridEnabled = false });
        await _layoutService.ReloadAsync();

        var connection = new ConnectionViewModel();
        _shell = new ShellViewModel(
            _layoutService, theme, connection,
            new ServiceCollection().AddLogging().AddSingleton<SensorAlertStore>().AddSingleton<SensorAlertTracker>().BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
        // The shell starts its OWN canvas loading in its constructor, and that LoadAsync sets
        // CurrentProfile from disk whenever it finishes. Landing between a test's marker save and its
        // assertion, it wiped the marker (the full-suite flake, RemEx-172ba). Loads queue on the
        // service's gate in order, so once this one returns, the shell's has landed.
        await _layoutService.LoadAsync();
        _vm = new CanvasDashboardViewModel(connection, _layoutService, _shell, new SensorAlertStore(), new SensorAlertTracker());
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private bool HasPendingSave => PendingProfileField.GetValue(_layoutService) is not null;

    private void Load() => FinishInitializeMethod.Invoke(_vm, new object[] { _layoutService.CurrentProfile });

    [Fact]
    public void LoadingTheProfileSeedsBothControlsWithoutSaving()
    {
        Load();

        _vm.GridSize.Should().Be(40);
        _vm.IsSnapToGridEnabled.Should().BeFalse();
        HasPendingSave.Should().BeFalse("seeding the grid controls from the loaded file must not write it back");
    }

    [Fact]
    public void TogglingSnapSavesThatFieldOntoCurrentProfile()
    {
        Load();
        // A marker written behind the canvas's back: the save can only carry it if it builds from
        // CurrentProfile at save time.
        _layoutService.RequestSave(_layoutService.CurrentProfile with { HostAddress = "wss://marker-host/" });

        _vm.IsSnapToGridEnabled = true;

        _layoutService.CurrentProfile.IsSnapToGridEnabled.Should().BeTrue();
        _layoutService.CurrentProfile.HostAddress.Should().Be("wss://marker-host/");
    }

    [Fact]
    public void ChangingGridSizeSavesThatFieldOntoCurrentProfile()
    {
        Load();
        _layoutService.RequestSave(_layoutService.CurrentProfile with { HostAddress = "wss://marker-host/" });

        _vm.GridSize = 70;

        _layoutService.CurrentProfile.GridSize.Should().Be(70);
        _layoutService.CurrentProfile.HostAddress.Should().Be("wss://marker-host/");
    }

    [Fact]
    public void BeforeTheFirstLoadAChangeIsNotSaved()
    {
        _vm.GridSize = 90;

        HasPendingSave.Should().BeFalse("a save before the user's file has loaded would replace it with defaults");
        _layoutService.CurrentProfile.GridSize.Should().Be(40);
    }

    [Fact]
    public void AnImportedProfileReachesBothControls()
    {
        Load();
        var imported = _layoutService.CurrentProfile with { GridSize = 80, IsSnapToGridEnabled = true };

        ApplyProfileMethod.Invoke(_vm, new object[] { imported });

        _vm.GridSize.Should().Be(80);
        _vm.IsSnapToGridEnabled.Should().BeTrue();
        _layoutService.CurrentProfile.GridSize.Should().Be(80);
    }
}
