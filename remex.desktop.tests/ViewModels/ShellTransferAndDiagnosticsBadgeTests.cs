using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-rjnbo.1: the Files and Diagnostics nav badges needed a real source, and both sources had
/// to exist BEFORE the page they describe - a badge bound to a lazily-built page view model would
/// only ever appear after the user had already visited it, which is backwards for a badge.
/// <see cref="ShellViewModel"/> now owns a <see cref="FileTransferQueue"/> eagerly (handed to
/// <see cref="FileTransferViewModel"/> when that page is finally built, rather than that view model
/// building a second, disagreeing one) and subscribes to the process-wide
/// <see cref="InMemoryLogSink"/> for the diagnostics count. These tests are deliberately about the
/// WIRING - that a count exists pre-navigation and updates live - not about FileTransferQueue's own
/// counting logic, which <c>FileTransferQueueTests</c> already covers.
/// </summary>
/// <remarks>
/// <c>transferQueuePost: action =&gt; action()</c> makes the shared queue's pump loop run
/// synchronously - this assembly has no <c>Avalonia.Headless</c> reference to pump a real
/// <c>Dispatcher.UIThread.Post</c>, the same reason <c>FileTransferQueueTests.NewQueue</c> does the
/// same thing. <c>ProfileReplacedDispatch</c> and <c>DiagnosticsLogDispatch</c> are both set to run
/// inline for the identical reason, spelled out on <c>ProfileReplacementInvalidatesCustomizationVmTests</c>.
/// </remarks>
public sealed class ShellTransferAndDiagnosticsBadgeTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public ShellTransferAndDiagnosticsBadgeTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-rjnbo1-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider(),
            transferQueuePost: action => action());

        _shell.ProfileReplacedDispatch = run => run();
        _shell.DiagnosticsLogDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void TheTransferCountExistsAndIsZeroBeforeAnyNavigationToFiles()
    {
        // The whole bead in one line: this reads ActiveTransferCount without ever calling
        // NavigateToFileTransfer, which is exactly what a badge needs and what the old
        // lazily-constructed _fileTransferViewModel could never give it.
        _shell.ActiveTransferCount.Should().Be(0);
        _shell.HasActiveTransfers.Should().BeFalse("a zero count hides the badge rather than showing \"0\"");
    }

    [Fact]
    public async Task EnqueuingATransferBeforeVisitingFilesDrivesTheBadge()
    {
        // The pump runs the work on a background Task regardless of transferQueuePost (that override
        // only makes the QUEUE's OWN state/Changed marshalling synchronous, not the pump's
        // scheduling) - so this awaits the work actually starting, same shape as
        // FileTransferQueueTests.Enqueue_ProcessesFifoOneActiveAtATime's firstStarted gate, rather
        // than racing the assertion against Task.Run.
        var workStarted = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var item = _shell.TransferQueueForTests.Enqueue(
            FileTransferQueueKind.Upload, "test.txt", async (_, _) =>
            {
                workStarted.TrySetResult();
                await gate.Task;
            });

        await workStarted.Task;

        // Still true: the item is enqueued/active without NavigateToFileTransfer ever having run.
        _shell.ActiveTransferCount.Should().Be(1);
        _shell.HasActiveTransfers.Should().BeTrue();

        gate.SetResult();
        await item.Completion.Task;

        _shell.ActiveTransferCount.Should().Be(0, "a completed transfer is no longer active");
        _shell.HasActiveTransfers.Should().BeFalse();
    }

    [Fact]
    public void NavigatingToFilesSharesTheSameQueueRatherThanBuildingASecondOne()
    {
        var beforeNav = _shell.TransferQueueForTests;

        _shell.NavigateToFileTransferCommand.Execute(null);

        var page = (FileTransferViewModel)_shell.CurrentView!;
        page.TransferQueue.Should().BeSameAs(beforeNav,
            "two counts of \"how many transfers are active\" would be able to disagree");
    }

    [Fact]
    public void TheDiagnosticsCountExistsAndIsZeroBeforeAnyNavigationToLogs()
    {
        _shell.DiagnosticsBadgeCount.Should().Be(0);
        _shell.HasUnreadDiagnostics.Should().BeFalse("a zero count hides the badge rather than showing \"0\"");
    }

    [Fact]
    public void AWarningLogEntryDrivesTheBadgeBeforeVisitingDiagnostics()
    {
        InMemoryLogSink.Append(LogLevel.Warning, "Test", "something", null);

        _shell.DiagnosticsBadgeCount.Should().Be(1);
        _shell.HasUnreadDiagnostics.Should().BeTrue();
    }

    [Fact]
    public void AnInformationLogEntryDoesNotDriveTheBadge()
    {
        // "Warning or above" - Information is the level most log lines are, and counting every one
        // of them would make the badge meaningless noise rather than an honest "something needs
        // your attention" signal.
        InMemoryLogSink.Append(LogLevel.Information, "Test", "routine", null);

        _shell.DiagnosticsBadgeCount.Should().Be(0);
    }

    [Fact]
    public void NavigatingToDiagnosticsClearsTheCount()
    {
        InMemoryLogSink.Append(LogLevel.Error, "Test", "boom", null);
        _shell.DiagnosticsBadgeCount.Should().Be(1);

        _shell.NavigateToDiagnosticLogsCommand.Execute(null);

        _shell.DiagnosticsBadgeCount.Should().Be(0, "arriving on the page is the acknowledgement");
    }

    /// <summary>
    /// Review, MEDIUM, RemEx-rjnbo.1: the badge used to count a warning even while the user was
    /// already sitting on the Logs page reading it, because <c>ShellViewModel.OnDiagnosticLogAdded</c>
    /// only cleared the count on navigation IN and incremented unconditionally after that.
    /// </summary>
    [Fact]
    public void AWarningLoggedWhileDiagnosticsIsCurrentDoesNotRaiseTheCount()
    {
        _shell.NavigateToDiagnosticLogsCommand.Execute(null);
        _shell.DiagnosticsBadgeCount.Should().Be(0);

        InMemoryLogSink.Append(LogLevel.Warning, "Test", "arrived while reading", null);

        _shell.DiagnosticsBadgeCount.Should().Be(0,
            "a warning that arrives while the diagnostics page is already on screen is not unread");
        _shell.HasUnreadDiagnostics.Should().BeFalse();
    }

    [Fact]
    public void AWarningLoggedWhileElsewhereStillRaisesTheCount()
    {
        // Visit diagnostics once (so the count starts at its post-visit zero, not the fixture's
        // pre-visit zero) and then leave, via a navigation target that needs no DI-resolved view
        // model (CanvasDashboardViewModel is built eagerly in the constructor) - NavigateToHome
        // would throw here, since this fixture's container never registered HomeViewModel.
        _shell.NavigateToDiagnosticLogsCommand.Execute(null);
        _shell.NavigateToCanvasCommand.Execute(null);

        InMemoryLogSink.Append(LogLevel.Warning, "Test", "arrived while away", null);

        _shell.DiagnosticsBadgeCount.Should().Be(1,
            "a warning that arrives while the user is on a different page is unread");
    }

    [Fact]
    public void NavigatingBackInClearsACountRaisedWhileElsewhere()
    {
        _shell.NavigateToDiagnosticLogsCommand.Execute(null);
        _shell.NavigateToCanvasCommand.Execute(null);
        InMemoryLogSink.Append(LogLevel.Warning, "Test", "arrived while away", null);
        _shell.DiagnosticsBadgeCount.Should().Be(1);

        _shell.NavigateToDiagnosticLogsCommand.Execute(null);

        _shell.DiagnosticsBadgeCount.Should().Be(0, "arriving on the page is the acknowledgement");
    }
}
