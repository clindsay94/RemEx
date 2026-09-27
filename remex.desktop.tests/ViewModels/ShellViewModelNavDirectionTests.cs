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
/// items' numeric <c>Tag</c>, but the Tags are ids, not positions. The drawer shows
/// Home, Sensors, Commands, Launcher, Processes, Files, Logs, Settings, About (Tags
/// 0,1,2,3,4,7,8,9,6), so moving between Settings and About, or from either to Files or Logs, slid
/// the wrong way. Direction now follows visual drawer order, and the structural guard below keeps
/// <see cref="ShellViewModel.DrawerNavOrder"/> and <c>ShellView.axaml</c> from drifting apart - the
/// Routines item (Tag 10, after Commands) is meant to be a one-line change to each.
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
    [InlineData(Home, Sensors)]
    [InlineData(Commands, Launcher)]
    [InlineData(Processes, Files)]
    [InlineData(Logs, Settings)]
    [InlineData(Settings, About)]
    public void MovingOneItemDownTheDrawerIsForward(int from, int to)
    {
        ShellViewModel.TransitionDirectionFor(from, to).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(to, from).Should().Be(-1);
    }

    [Theory]
    [InlineData(Home, About)]
    [InlineData(Home, Settings)]
    [InlineData(Sensors, Logs)]
    [InlineData(Processes, Settings)]
    [InlineData(Files, About)]
    [InlineData(Commands, Files)]
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
    [InlineData(Home)]
    [InlineData(Files)]
    [InlineData(About)]
    public void RemoteDesktopHasNoDrawerItemSoItDrillsInForwardAndComesBack(int drawerPage)
    {
        ShellViewModel.TransitionDirectionFor(drawerPage, RemoteDesktop).Should().Be(1);
        ShellViewModel.TransitionDirectionFor(RemoteDesktop, drawerPage).Should().Be(-1);
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

        // Processes -> Remote Desktop (off-drawer): forward, then back out to About.
        _shell.NavigateToRemoteDesktop();
        _shell.TransitionDirection.Should().Be(1);
        _shell.NavigateToAbout();
        _shell.TransitionDirection.Should().Be(-1,
            "Tag 6 > Tag 5 read as forward before RemEx-pp0rt.2; About is a drawer page, RD is not");
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
