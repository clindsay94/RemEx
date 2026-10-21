using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-pp0rt.2: the shell's shared-axis page transition used to read its direction off the nav
/// items' numeric <c>Tag</c>, but the Tags are ids, not positions. Since RemEx-172ba the drawer
/// shows Home, Remote desktop, Files, Commands, Launcher, Processes, Sensors, Routines, Settings,
/// Logs, About (Tags 0,5,7,2,3,4,1,10,9,8,6), so a Tag comparison gets most moves wrong. Direction
/// follows visual drawer order, and the structural guard below keeps
/// <see cref="ShellViewModel.DrawerNavOrder"/> and <c>ShellView.axaml</c> from drifting apart.
/// </summary>
public sealed class ShellViewModelNavDirectionTests : IAsyncLifetime
{
    private const int Home = 0;
    private const int Sensors = 1;
    private const int Commands = 2;
    private const int Launcher = 3;
    private const int Processes = 4;
    private const int RemoteDesktop = 5;
    private const int About = 6;
    private const int Files = 7;
    private const int Logs = 8;
    private const int Settings = 9;
    private const int Routines = 10;

    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public ShellViewModelNavDirectionTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-nav-direction-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        // Same shell construction as RemoteDesktopStreamForegroundGatingTests.
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(Home, RemoteDesktop)]
    [InlineData(RemoteDesktop, Files)]
    [InlineData(Files, Commands)]
    [InlineData(Commands, Launcher)]
    [InlineData(Launcher, Processes)]
    [InlineData(Processes, Sensors)]
    [InlineData(Sensors, Routines)]
    [InlineData(Routines, Settings)]
    [InlineData(Settings, Logs)]
    [InlineData(Logs, About)]
    public void MovingOneItemDownTheDrawerIsForward(int from, int to)
    {
        ShellViewModel.TransitionDirectionFor(from, to).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(to, from).Should().Be(-1);
    }

    [Theory]
    [InlineData(Home, About)]
    [InlineData(Home, Settings)]
    [InlineData(RemoteDesktop, Sensors)]
    [InlineData(Files, Logs)]
    [InlineData(Commands, Routines)]
    [InlineData(Processes, About)]
    [InlineData(Files, Launcher)]
    public void MovingSeveralItemsDownTheDrawerIsForward(int from, int to)
    {
        ShellViewModel.TransitionDirectionFor(from, to).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(to, from).Should().Be(-1);
    }

    [Theory]
    [InlineData(About, Files)]
    [InlineData(About, Logs)]
    [InlineData(About, Settings)]
    public void LeavingAboutForAPageAboveItIsBackDespiteTheHigherTag(int from, int to)
    {
        // About is Tag 6 but sits last in the drawer, below Files (7), Logs (8) and Settings (9).
        // The old Tag comparison read every one of these as forward.
        ShellViewModel.TransitionDirectionFor(from, to).Should().Be(-1);
        ShellViewModel.TransitionDirectionFor(to, from).Should().Be(1);
    }

    [Theory]
    [InlineData(Home)]
    [InlineData(Settings)]
    [InlineData(About)]
    public void ReactivatingTheCurrentPageKeepsTheForwardDirection(int page)
        => ShellViewModel.TransitionDirectionFor(page, page).Should().Be(1);

    [Theory]
    [InlineData(Files)]
    [InlineData(Commands)]
    [InlineData(About)]
    public void LeavingRemoteDesktopForAPageBelowItIsForward(int drawerPage)
    {
        // Remote desktop is second in the drawer since RemEx-172ba; it used to rank below the whole
        // drawer as an off-drawer page.
        ShellViewModel.TransitionDirectionFor(RemoteDesktop, drawerPage).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(drawerPage, RemoteDesktop).Should().Be(-1);
    }

    [Fact]
    public void AnIndexWithNoDrawerItemRanksBelowTheWholeDrawer()
    {
        ShellViewModel.TransitionDirectionFor(About, 99).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(99, Home).Should().Be(-1);
    }

    [Theory]
    [InlineData(Settings)]
    [InlineData(Logs)]
    [InlineData(About)]
    public void LeavingRoutinesForAPageBelowItIsForwardDespiteTheLowerTag(int to)
    {
        // Routines is Tag 10 but sits eighth, above the Settings family. A Tag comparison would read
        // every one of these as backward.
        ShellViewModel.TransitionDirectionFor(Routines, to).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(to, Routines).Should().Be(-1);
    }

    [Theory]
    [InlineData(Commands)]
    [InlineData(Launcher)]
    [InlineData(Processes)]
    public void LeavingSensorsForAPageAboveItIsBackDespiteTheHigherTag(int to)
    {
        // Sensors is Tag 1 but sits seventh, after Processes.
        ShellViewModel.TransitionDirectionFor(Sensors, to).Should().Be(-1);
        ShellViewModel.TransitionDirectionFor(to, Sensors).Should().Be(1);
    }

    [Fact]
    public void NavigatingToRoutinesUsesItsDrawerPositionAndShowsTheRoutinesPage()
    {
        // (Remote, Home and Launcher need services this bare container does not register; Processes
        // and About construct their own view models.)
        _shell.NavigateToTaskManager();
        _shell.NavigateToRoutines();

        _shell.TransitionDirection.Should().Be(1, "Routines sits below Processes");
        _shell.ActiveNavIndex.Should().Be(Routines);
        _shell.CurrentView.Should().BeOfType<RoutinesViewModel>();

        _shell.NavigateToAbout();
        _shell.TransitionDirection.Should().Be(1, "About sits below Routines");

        _shell.NavigateToRoutines();
        _shell.TransitionDirection.Should().Be(-1, "Routines sits above About");
    }

    [Fact]
    public void NavigatingTheShellSetsTransitionDirectionFromDrawerOrder()
    {
        // Processes -> About: About is last in the drawer.
        _shell.NavigateToTaskManager();
        _shell.NavigateToAbout();
        _shell.TransitionDirection.Should().Be(1);

        // About -> Processes: back up the drawer.
        _shell.NavigateToTaskManager();
        _shell.TransitionDirection.Should().Be(-1);

        // Processes -> Remote desktop: RD is second, so back up; then down again to About.
        _shell.NavigateToRemoteDesktop();
        _shell.TransitionDirection.Should().Be(-1);
        _shell.ActiveNavIndex.Should().Be(RemoteDesktop);
        _shell.NavigateToAbout();
        _shell.TransitionDirection.Should().Be(1);
    }

    [Fact]
    public void DirectionOrderMatchesTheDrawerOrderInShellViewXaml()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

        var drawerTags = Regex.Matches(xaml, @"<ListBoxItem\s[^>]*>")
            .Select(m => m.Value)
            .Where(tag => Regex.IsMatch(tag, @"Classes=""[^""]*\bnav-item\b"))
            .Select(tag => Regex.Match(tag, @"\bTag=""(\d+)""").Groups[1].Value)
            .Select(int.Parse)
            .ToList();

        drawerTags.Should().NotBeEmpty("the drawer's nav-item ListBoxItems moved or were renamed");
        ShellViewModel.DrawerNavOrder.Should().Equal(drawerTags,
            "ShellViewModel.DrawerNavOrder must list the drawer's Tags in the order ShellView.axaml shows them");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
