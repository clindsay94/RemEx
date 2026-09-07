using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-a8du, item 2: the follow-tail state machine. Kept entirely on the ViewModel (
/// <see cref="DiagnosticLogsViewModel.IsFollowingTail"/>, <see cref="DiagnosticLogsViewModel.OnLogListScrolled"/>,
/// <see cref="DiagnosticLogsViewModel.ScrollToEndRequested"/>) so it is testable without a headless
/// render — remex.desktop.tests has none.
/// </summary>
public class DiagnosticLogsFollowTailTests
{
    private static LogEntry Entry(string message) =>
        new(DateTime.UtcNow, LogLevel.Information, "Cat", message, null);

    // The shell is stored and never dereferenced by DiagnosticLogsViewModel's constructor or by any
    // command under test here (DestructiveActionFailClosedTests documents the same for this type).
    private static DiagnosticLogsViewModel CreateViewModel() => new(null!);

    [Fact]
    public void FollowTail_IsOnByDefault()
    {
        using var vm = CreateViewModel();
        Assert.True(vm.IsFollowingTail);
    }

    [Fact]
    public void ScrollingAwayFromTheEnd_PausesFollowing()
    {
        using var vm = CreateViewModel();

        vm.OnLogListScrolled(isAtEnd: false);

        Assert.False(vm.IsFollowingTail);
    }

    [Fact]
    public void ReachingTheEndOnItsOwn_DoesNotResumeFollowing()
    {
        // Deliberate: a manual scroll back down to the tail must not be reinterpreted as "resume
        // following" — only the explicit Jump-to-newest chip does that. Otherwise a user scrolling
        // to check the latest line would find following silently re-armed underneath them.
        using var vm = CreateViewModel();
        vm.OnLogListScrolled(isAtEnd: false);

        vm.OnLogListScrolled(isAtEnd: true);

        Assert.False(vm.IsFollowingTail);
    }

    [Fact]
    public void JumpToNewest_ResumesFollowingAndRequestsAScroll()
    {
        using var vm = CreateViewModel();
        vm.OnLogListScrolled(isAtEnd: false);
        var scrollRequested = false;
        vm.ScrollToEndRequested += () => scrollRequested = true;

        vm.JumpToNewestCommand.Execute(null);

        Assert.True(vm.IsFollowingTail);
        Assert.True(scrollRequested);
    }

    [Fact]
    public void ArrivalWhileFollowing_RequestsAScroll()
    {
        // ProcessIncomingEntry is the synchronous half OnLogAdded posts to Dispatcher.UIThread —
        // nothing pumps that dispatcher in this test assembly (no Avalonia.Headless reference in the
        // repo), so going through OnLogAdded itself would never run this body at all.
        using var vm = CreateViewModel();
        Assert.True(vm.IsFollowingTail, "the fixture assumes following is still on");
        var scrollRequested = false;
        vm.ScrollToEndRequested += () => scrollRequested = true;

        vm.ProcessIncomingEntry(Entry("a live entry"));

        Assert.True(scrollRequested);
    }

    [Fact]
    public void ArrivalWhileNotFollowing_DoesNotRequestAScroll()
    {
        using var vm = CreateViewModel();
        vm.OnLogListScrolled(isAtEnd: false);
        var scrollRequested = false;
        vm.ScrollToEndRequested += () => scrollRequested = true;

        vm.ProcessIncomingEntry(Entry("a live entry"));

        Assert.False(scrollRequested,
            "a user who scrolled away to read must not be yanked back down by the next arrival");
    }

    [Fact]
    public void ArrivalThatFailsTheDisplayFilter_DoesNotRequestAScrollEitherWhileFollowing()
    {
        // An entry filtered out below the display level never lands in VisibleEntries, so scrolling
        // for it would move the viewport toward a row the list will never actually show.
        using var vm = CreateViewModel();
        vm.SelectedDisplayLevel = "Error";
        var scrollRequested = false;
        vm.ScrollToEndRequested += () => scrollRequested = true;

        vm.ProcessIncomingEntry(Entry("below the display floor"));

        Assert.False(scrollRequested);
    }
}
