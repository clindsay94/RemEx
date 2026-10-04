using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The PC behaviour fixes of the 3.0 comb, round 1 (RemEx-pp4cm.5): the ones a test can pin without
/// rendering anything. Visual items (Home's Pair button, the Apps empty state) are checked by eye.
/// </summary>
public sealed class CombRoundOnePcBehaviourTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public CombRoundOnePcBehaviourTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-comb1-pc-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                // Settings is built on first visit, and it needs the savefile service. Its launcher
                // store points into the temp folder so nothing here touches the user's real data.
                .AddSingleton(new RemexSavefileService(
                    _layoutService,
                    new LauncherStorageService(Directory.CreateDirectory(Path.Combine(_tempDir, "launchers")).FullName),
                    new FileTransferRootSettingsService()))
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ─── tray-tooltip-connected-lie ──────────────────────────────────────────────

    [Fact]
    public void TraySummary_CarriesThePhonePresenceLine_AndTheProductNameSpelledRight()
    {
        var summary = _shell.BuildTraySummary(telemetry: null, presenceText: "Pixel 9 connected");

        summary.Should().Be("RemEx — Pixel 9 connected");
        summary.Should().NotContain("Remex", "the product is spelled RemEx everywhere else");
        summary.Should().NotContain("Connected", "the loopback link's state is not what the tooltip reports");
    }

    // ─── feature-banner-dead-end ─────────────────────────────────────────────────

    [Fact]
    public void ConnectionBanner_NamesThePageTheWaySidebarDoes_AndSendsNobodyToSettings()
    {
        var message = ShellViewModel.BuildConnectionBannerMessage("Nav_Sensors");

        message.Should().Contain(LocalizationService.Instance["Nav_Sensors"]);
        message.Should().NotContain("Sensor Workspace", "that was a retired name hardcoded in English");
        message.Should().NotContain("{0}", "the page name is filled in");
        message.Should().NotContain(
            LocalizationService.Instance["Shell_OpenSettings"],
            "Settings has nothing that fixes the in-process link, so the banner must not point there");
    }

    // ─── tutorial-esc-not-persisted ──────────────────────────────────────────────

    [Fact]
    public async Task EscapeOnTheTutorial_CountsAsSkip_SoItDoesNotComeBackEveryLaunch()
    {
        _shell.ShowTutorialOverlay = true;

        _shell.DismissOverlays();

        _shell.ShowTutorialOverlay.Should().BeFalse();
        await _layoutService.FlushAsync();
        _layoutService.CurrentProfile!.HasCompletedTutorial.Should().BeTrue(
            "Escape used to hide the overlay without recording that the tutorial was finished");
    }

    // ─── no-reselect-to-top ──────────────────────────────────────────────────────

    [Fact]
    public void ReselectingThePageYouAreOn_RaisesCurrentPageReselected_ButArrivingDoesNot()
    {
        var raised = 0;
        _shell.CurrentPageReselected += () => raised++;

        _shell.NavigateToCanvas();
        raised.Should().Be(0, "arriving at a page is not reselecting it");

        _shell.NavigateToCanvas();
        raised.Should().Be(1, "picking the sidebar item you are already on scrolls its page back to the top");

        _shell.NavigateToDiagnosticLogs();
        raised.Should().Be(1, "moving to a different page is not a reselect");
    }

    // ─── pair-two-clicks ─────────────────────────────────────────────────────────

    [Fact]
    public void StartPairing_OpensSettings_AndRunsTheSamePairingCommandTheSettingsButtonRuns()
    {
        var command = _shell.Connection.GenerateQrCodeCommand;
        command.Should().BeAssignableTo<IAsyncRelayCommand>();
        var pairing = (IAsyncRelayCommand)command;
        pairing.ExecutionTask.Should().BeNull("nothing has started pairing yet");

        _shell.StartPairing();

        _shell.CurrentView.Should().BeOfType<SettingsViewModel>();
        pairing.ExecutionTask.Should().NotBeNull("one click must start pairing, not only open the page it lives on");
    }

    // ─── palette-settings / home-disconnect ──────────────────────────────────────

    [Fact]
    public void Palette_SettingsEntryOpensTheSettingsPage_AndNoEntryDrivesTheLoopbackLink()
    {
        var palette = new CommandPaletteViewModel(_shell);
        var entries = palette.FilteredResults;

        entries.Single(e => e.Label == LocalizationService.Instance["Palette_Settings"]).Command
            .Should().BeSameAs(_shell.NavigateToSettingsCommand);
        entries.Single(e => e.Label == LocalizationService.Instance["Shell_LogsDiagnostics"]).Command
            .Should().BeSameAs(_shell.NavigateToDiagnosticLogsCommand);
        entries.Single(e => e.Label == LocalizationService.Instance["Personalize_Title"]).Command
            .Should().BeSameAs(_shell.ToggleSettingsPanelCommand);

        var commands = entries.Select(e => e.Command).ToList();
        commands.Should().NotContain(_shell.Connection.ConnectCommand);
        commands.Should().NotContain(_shell.Connection.DisconnectCommand);
        commands.Should().NotContain(_shell.Connection.SendPingCommand);
    }

    // ─── window-min-size ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1200, 800, 1366, 728, 720, 560, 1200, 728)]   // 1366x768 minus the taskbar: height shrinks
    [InlineData(1200, 800, 1920, 1040, 720, 560, 1200, 800)]  // roomy screen: unchanged
    [InlineData(1200, 800, 1024, 600, 720, 560, 1024, 600)]   // small screen: fits it
    [InlineData(1200, 800, 640, 480, 720, 560, 720, 560)]     // tiny screen: the minimum wins
    public void FitStartSize_ShrinksToTheWorkingArea_ButNeverBelowTheMinimum(
        double reqW, double reqH, double areaW, double areaH, double minW, double minH, double expW, double expH)
    {
        var fitted = MainWindow.FitStartSize(new Size(reqW, reqH), new Size(areaW, areaH), new Size(minW, minH));

        fitted.Should().Be(new Size(expW, expH));
    }

    // ─── activity-wire-tokens ────────────────────────────────────────────────────

    [Theory]
    [InlineData("ForceRestart", "Remote_ForceRestart")]
    [InlineData("forceshutdown", "Remote_ForceShutdown")]
    [InlineData("RestartToUefi", "Remote_RebootUefi")]
    [InlineData("SignOut", "Remote_SignOut")]
    public void ActivityCommandLabel_MapsTheWireVerbToTheLabelTheCommandsPageShows(string verb, string key)
    {
        ActivityEntry.CommandLabel(verb).Should().Be(LocalizationService.Instance[key]);
        ActivityEntry.CommandLabel(verb).Should().NotBe(verb, "the raw wire token must not reach the activity feed");
    }

    [Fact]
    public void ActivityCommandLabel_FallsBackToTheVerbItself_WhenItHasNoLabel()
    {
        ActivityEntry.CommandLabel("SomethingNew").Should().Be("SomethingNew");
    }

    // ─── taskmgr-poll-fault ──────────────────────────────────────────────────────

    /// <summary>
    /// A failing refresh used to retry through <c>catch { await Task.Delay(2000, ct); }</c>: stopping
    /// the poll during that delay threw out of the catch block and faulted the fire-and-forget task
    /// unobserved, and nothing on screen said the list was stale.
    /// </summary>
    [Fact]
    public async Task TaskManagerPoll_AFailingRefresh_FlagsTheErrorAndStopsCleanly()
    {
        using var vm = new TaskManagerViewModel(_shell.Connection, _shell);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var poll = vm.PollAsync(
            cts.Token,
            () => { attempts++; throw new InvalidOperationException("the host went away"); },
            TimeSpan.FromSeconds(30));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!vm.HasRefreshError && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        vm.HasRefreshError.Should().BeTrue("the user must be told the list is out of date");

        cts.Cancel();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));   // completes; the old code faulted with TaskCanceledException

        poll.IsCompletedSuccessfully.Should().BeTrue();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task TaskManagerPoll_ASuccessfulRefreshAfterAFailure_ClearsTheError()
    {
        using var vm = new TaskManagerViewModel(_shell.Connection, _shell);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var poll = vm.PollAsync(
            cts.Token,
            () =>
            {
                if (++attempts == 1) throw new InvalidOperationException("first try fails");
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(20));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (attempts < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        cts.Cancel();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        attempts.Should().BeGreaterThanOrEqualTo(3);
        vm.HasRefreshError.Should().BeFalse();
    }

    // ─── rd-handled-after-await ──────────────────────────────────────────────────

    /// <summary>
    /// <c>OnViewKeyDown</c>/<c>OnViewKeyUp</c> are <c>async void</c>: Avalonia resumes routing at the
    /// first await, so <c>e.Handled = true</c> set AFTER <c>SendInputAsync</c> came too late and Tab /
    /// Escape also acted on the local window while a stream forwarded them. A source scan, because the
    /// handler needs a live RemoteDesktopView and a stream to run.
    /// </summary>
    [Theory]
    [InlineData("OnViewKeyDown")]
    [InlineData("OnViewKeyUp")]
    public void RemoteDesktopKeyHandlers_MarkTheKeyHandled_BeforeTheyAwait(string handler)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Views", "RemoteDesktopView.axaml.cs"));
        var start = source.IndexOf($"async void {handler}(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"{handler} must exist");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = source[start..end];

        var handled = body.IndexOf("e.Handled = true", StringComparison.Ordinal);
        var awaited = body.IndexOf("await vm.SendInputAsync", StringComparison.Ordinal);

        handled.Should().BeGreaterThan(0, "the handler marks the key handled");
        awaited.Should().BeGreaterThan(0, "the handler forwards the key");
        handled.Should().BeLessThan(awaited, "after an await the routing has already moved on");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
