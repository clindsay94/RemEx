using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;
using Remex.Desktop.Tests.Routines;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The command palette's routines entries (routines spec §1.2, R-UX-21): "Go to Routines", "Pause all
/// routines" and "Resume routines", reaching the same host the page uses.
/// </summary>
public sealed class CommandPaletteRoutinesTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layout;
    private readonly FakeRoutinesHost _host = new();
    private ShellViewModel _shell = null!;

    public CommandPaletteRoutinesTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-palette-routines-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layout = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layout.LoadAsync();
        _shell = new ShellViewModel(
            _layout,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .AddSingleton<IRoutinesHost>(_host)
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private CommandPaletteEntry Entry(string key)
    {
        var palette = new CommandPaletteViewModel(_shell);
        return palette.FilteredResults.Single(e => e.Label == LocalizationService.Instance[key]);
    }

    [Fact]
    public void GoToRoutinesNavigatesToThePage()
    {
        Entry("Palette_Routines").Command.Execute(null);

        _shell.ActiveNavIndex.Should().Be(10);
        _shell.CurrentView.Should().BeOfType<RoutinesViewModel>();
    }

    [Fact]
    public async Task PauseAndResumeReachTheHost()
    {
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Entry("Palette_PauseRoutines").Command).ExecuteAsync(null);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Entry("Palette_ResumeRoutines").Command).ExecuteAsync(null);

        _host.PausedCalls.Should().Equal(true, false);
    }

    [Fact]
    public void TheEntriesAreNotConfirmedPowerActions()
    {
        // Pausing and resuming are reversible and stop nothing destructive, so no dialog sits between
        // the palette and the host.
        Entry("Palette_PauseRoutines").RequiresConfirmation.Should().BeFalse();
        Entry("Palette_ResumeRoutines").RequiresConfirmation.Should().BeFalse();
    }
}
