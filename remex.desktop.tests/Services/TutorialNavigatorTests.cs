using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the tutorial carousel's page set and movement (RemEx-5m3i).
/// </summary>
/// <remarks>
/// The failure this guards is platform-specific and therefore invisible to whoever wrote it: pages
/// are filtered per OS, so counting one list and indexing another only misbehaves on the platform
/// that hides a page.
/// </remarks>
public class TutorialNavigatorTests
{
    private static readonly TutorialPage[] Pages =
    [
        new(0, "Welcome", "", PlatformFlags.All),
        new(1, "Windows autostart", "", PlatformFlags.Windows),
        new(2, "Linux prerequisites", "", PlatformFlags.Linux),
        new(3, "Pairing", "", PlatformFlags.All),
        new(4, "Glossary: SPKI", "", PlatformFlags.All)
    ];

    [Fact]
    public void OnlyPagesForThisPlatformAreVisible()
    {
        var windows = TutorialNavigator.VisiblePages(Pages, PlatformFlags.Windows);
        var linux = TutorialNavigator.VisiblePages(Pages, PlatformFlags.Linux);

        Assert.Equal(new[] { 0, 1, 3, 4 }, windows.Select(p => p.PageIndex));
        Assert.Equal(new[] { 0, 2, 3, 4 }, linux.Select(p => p.PageIndex));
    }

    [Fact]
    public void PositionsIndexTheFilteredListNotTheAuthorNumbering()
    {
        // THE BUG THIS EXISTS TO PREVENT. On Linux, page 2 sits at position 1 - counting one list
        // and indexing the other makes the second dot show a page the dot was not drawn for, and
        // only on the platform that hides a page.
        var linux = TutorialNavigator.VisiblePages(Pages, PlatformFlags.Linux);

        Assert.Equal(4, linux.Count);
        Assert.Equal("Linux prerequisites", linux[1].Title);
        Assert.Equal(1, TutorialNavigator.PositionOfPage(linux, authorPageIndex: 2));
    }

    [Fact]
    public void ADeepLinkNamesAPageSoInsertingOneDoesNotBreakIt()
    {
        // Pages 8-15 are pure glossary a user reaches when they hit the thing it explains. A link
        // stored as "position 11" breaks the moment a page is inserted, hidden or reordered -
        // SILENTLY, landing the user on a neighbouring topic rather than failing.
        var windows = TutorialNavigator.VisiblePages(Pages, PlatformFlags.Windows);
        var before = TutorialNavigator.PositionOfPage(windows, authorPageIndex: 4);

        var withInsertion = TutorialNavigator.VisiblePages(
            Pages.Append(new TutorialPage(2, "Inserted", "", PlatformFlags.Windows)), PlatformFlags.Windows);
        var after = TutorialNavigator.PositionOfPage(withInsertion, authorPageIndex: 4);

        Assert.NotEqual(before, after);
        Assert.Equal("Glossary: SPKI", withInsertion[after].Title);
    }

    [Fact]
    public void ADeepLinkToAPageThisPlatformHidesResolvesToNothing()
    {
        // A Windows-only page linked from a Linux machine has no honest destination, and landing
        // the user on a neighbouring topic is worse than declining.
        var linux = TutorialNavigator.VisiblePages(Pages, PlatformFlags.Linux);

        Assert.Equal(-1, TutorialNavigator.PositionOfPage(linux, authorPageIndex: 1));
    }

    [Fact]
    public void NavigationStopsAtBothEndsRatherThanWrapping()
    {
        // A tutorial that loops back to page one reads as though the user missed something, and
        // gives no signal that they reached the end.
        var count = 4;

        Assert.Equal(0, TutorialNavigator.Previous(0, count));
        Assert.Equal(count - 1, TutorialNavigator.Next(count - 1, count));
        Assert.Equal(1, TutorialNavigator.Next(0, count));
        Assert.Equal(0, TutorialNavigator.Previous(1, count));
    }

    [Fact]
    public void AnOutOfRangePositionIsClampedRatherThanCrashingTheView()
    {
        // A saved position from a build with more pages, or from the other platform, must not index
        // past the end - the tutorial is the first thing a new user sees.
        Assert.Equal(3, TutorialNavigator.ClampPosition(99, 4));
        Assert.Equal(0, TutorialNavigator.ClampPosition(-5, 4));
    }

    [Fact]
    public void AnEmptyPageSetYieldsMinusOneRatherThanZero()
    {
        // Zero would look like a valid position and index into nothing. An empty tutorial should
        // not happen, but "should not happen" is how a launch-day crash gets written.
        Assert.Equal(-1, TutorialNavigator.ClampPosition(0, 0));
        Assert.Equal(-1, TutorialNavigator.Next(0, 0));
        Assert.False(TutorialNavigator.IsLastPage(0, 0));
    }

    [Fact]
    public void TheLastPageIsIdentifiedSoTheButtonCanSayFinish()
    {
        Assert.True(TutorialNavigator.IsLastPage(3, 4));
        Assert.False(TutorialNavigator.IsLastPage(2, 4));
    }

    [Fact]
    public void PagesAppearInAuthorOrderEvenIfTheSourceIsNot()
    {
        // Author numbering survives a page being hidden; list position does not.
        var shuffled = new[] { Pages[3], Pages[0], Pages[4] };

        var visible = TutorialNavigator.VisiblePages(shuffled, PlatformFlags.All);

        Assert.Equal(new[] { 0, 3, 4 }, visible.Select(p => p.PageIndex));
    }

    [Fact]
    public void ANullPageSetIsEmptyRatherThanThrowing()
    {
        Assert.Empty(TutorialNavigator.VisiblePages(null, PlatformFlags.All));
    }

    [Fact]
    public void EveryPositionAcrossTheSweepMapsBackToTheSamePage()
    {
        // The invariant the dots depend on: the nth dot shows the nth visible page, on every
        // platform. A mismatch here is what makes a dot strip lie.
        foreach (var platform in new[] { PlatformFlags.Windows, PlatformFlags.Linux })
        {
            var visible = TutorialNavigator.VisiblePages(Pages, platform);

            for (var position = 0; position < visible.Count; position++)
            {
                var authorIndex = visible[position].PageIndex;

                Assert.Equal(position, TutorialNavigator.PositionOfPage(visible, authorIndex));
            }
        }
    }
}
