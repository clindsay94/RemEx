using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins two review findings against DiagnosticLogsView.axaml.cs's follow-tail scroll handler
/// (RemEx-a8du fix round). No headless render in this suite, so — same as
/// <see cref="DiagnosticLogsViewTests"/> — these are source-text checks rather than a live wiring
/// test.
/// </summary>
public class DiagnosticLogsViewCodeBehindTests
{
    private static readonly string Source = CodeBehindSource();

    [Fact]
    public void ScrollChangedHandler_ReadsTheScrollViewerFrom_e_Source_NotSender()
    {
        // HIGH finding: ScrollViewer.ScrollChanged is attached to the ListBox, so Avalonia sets
        // `sender` to the ListBox itself — the ScrollViewer that actually raised the event is
        // RoutedEventArgs.Source. `sender is not ScrollViewer` was true on EVERY scroll, so
        // OnLogListScrolled never ran at all: no auto-pause, no Jump-to-newest chip, and a live
        // arrival always yanked a scrolled-up reader back to the bottom.
        Source.Should().Contain("e.Source is not ScrollViewer",
            "the ScrollViewer that raised the attached ScrollChanged event is e.Source, not sender");
        Source.Should().NotContain("sender is not ScrollViewer",
            "sender is the ListBox the handler is attached to, never the inner ScrollViewer");
    }

    [Fact]
    public void AtEndCalculation_AccountsForExtentGrowthFromALiveArrival()
    {
        // MEDIUM finding: a live arrival grows Extent without moving Offset, which reads as
        // "scrolled away" on the very first entry after the user was sitting at the bottom — pausing
        // following before the programmatic scroll-to-end even has a chance to run. The fix evaluates
        // "at end" against the extent as it was BEFORE this notification's growth.
        Source.Should().Contain("e.ExtentDelta",
            "the at-end check must discount the extent's growth from this notification, or an " +
            "arrival while sitting at the bottom reads as a scroll-away and wrongly pauses following");
    }

    [Fact]
    public void ExtentGrowthUsedInTheAtEndCheck_IsClampedToNonNegative()
    {
        // FOUND DURING THE LIVE CHECK, not in the original review: the virtualizing panel also
        // SHRINKS Extent as it replaces estimated item heights with real ones while settling after
        // ScrollToEnd() — a NEGATIVE ExtentDelta. Subtracting a raw negative delta widens the
        // threshold instead of narrowing it, so a jump that landed exactly at the end read as "not
        // quite there" and immediately re-paused following right after Jump-to-newest had just
        // resumed it (reproduced live: Offset=226, Extent=578, ExtentDelta=-14 read as NOT at end).
        // A shrink is not evidence the user was further from the end before it, so it must not widen
        // the pre-growth figure — hence clamping the delta used here to zero.
        Source.Should().MatchRegex(@"Math\.Max\(\s*0\s*,\s*e\.ExtentDelta\.Y\s*\)",
            "a negative ExtentDelta (virtualization shrinking the extent while settling) must not " +
            "widen the at-end threshold, or a scroll that landed exactly at the end reads as short of it");
    }

    [Fact]
    public void ScrollToEndUsesTheScrollViewersOwnScrollToEnd_NotScrollIntoViewOnTheLastItem()
    {
        // FOUND DURING THE LIVE CHECK: ScrollIntoView on the last item lands wherever that item's
        // realized bounds put it, which can miss the true maximum offset by a few pixels — enough
        // that the very next ScrollChanged read "not quite at end" and paused following again right
        // after it had just been resumed. ScrollViewer.ScrollToEnd() sets the offset to the actual
        // maximum, which is exactly what the at-end check compares against.
        Source.Should().Contain("scrollViewer.ScrollToEnd()",
            "landing via ScrollIntoView instead of the ScrollViewer's own ScrollToEnd() can miss the " +
            "true maximum offset by a few pixels and immediately re-pause following after a jump");
    }

    private static string CodeBehindSource() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "DiagnosticLogsView.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
