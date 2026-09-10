using Remex.Desktop.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// Decides which tutorial pages this machine shows and how the carousel moves between them
/// (RemEx-5m3i).
/// </summary>
/// <remarks>
/// <para>
/// The tutorial is seventeen near-identical hand-written panels gated by an index converter, while a
/// <see cref="TutorialPage"/> model already exists and is populated — two parallel structures that
/// have to agree. **THE THING THAT MAKES THEM DISAGREE IS <see cref="PlatformFlags"/>.** Pages are
/// filtered per OS, so the dot strip and the navigation must both count the FILTERED list. Count one
/// and index the other and clicking the fifth dot lands on a different page than the dot was drawn
/// for — and only on the platform that hides a page, which is the half of the audience the author
/// is not sitting in front of.
/// </para>
/// <para>
/// Everything here indexes the filtered sequence. There is deliberately no method that takes a raw
/// <see cref="TutorialPage.PageIndex"/> as a position, because that is the mistake.
/// </para>
/// </remarks>
public static class TutorialNavigator
{
    /// <summary>
    /// The pages this platform shows, in author order.
    /// </summary>
    public static IReadOnlyList<TutorialPage> VisiblePages(
        IEnumerable<TutorialPage>? all, PlatformFlags platform)
    {
        if (all is null) return [];

        var visible = new List<TutorialPage>();
        foreach (var page in all)
        {
            if ((page.SupportedPlatforms & platform) != 0) visible.Add(page);
        }

        // Author order, not declaration order: PageIndex is the author's numbering and survives a
        // page being hidden, whereas list position does not.
        visible.Sort((a, b) => a.PageIndex.CompareTo(b.PageIndex));
        return visible;
    }

    /// <summary>
    /// Clamps a carousel position to something that exists.
    /// </summary>
    /// <remarks>
    /// Returns -1 for an empty sequence rather than 0, so a caller cannot index into nothing. An
    /// empty tutorial should not happen, but "should not happen" is how a launch-day crash gets
    /// written.
    /// </remarks>
    public static int ClampPosition(int position, int visibleCount)
    {
        if (visibleCount <= 0) return -1;
        return Math.Clamp(position, 0, visibleCount - 1);
    }

    /// <summary>
    /// Where "next" goes. Stops at the end rather than wrapping.
    /// </summary>
    /// <remarks>
    /// A tutorial that loops back to page one from the last page reads as though the user missed
    /// something, and there is no signal that they reached the end. The Finish affordance is what
    /// ends it, not a silent wrap.
    /// </remarks>
    public static int Next(int position, int visibleCount) =>
        ClampPosition(position + 1, visibleCount);

    /// <summary>Where "back" goes. Stops at the start.</summary>
    public static int Previous(int position, int visibleCount) =>
        ClampPosition(position - 1, visibleCount);

    /// <summary>Whether the carousel is on its last page, so the button can say "Finish".</summary>
    public static bool IsLastPage(int position, int visibleCount) =>
        visibleCount > 0 && position == visibleCount - 1;

    /// <summary>
    /// Finds the carousel position of a page by its AUTHOR index, for deep-linking.
    /// </summary>
    /// <remarks>
    /// **DEEP LINKS NAME A PAGE, NOT A POSITION.** Pages 8-15 are pure glossary that a user wants to
    /// reach when they hit the thing it explains, and a link stored as "position 11" breaks the
    /// moment a page is inserted, hidden on that platform, or reordered — silently, landing the user
    /// on a neighbouring topic rather than failing. Resolving the author's own page number against
    /// the filtered list keeps the link meaning what it said.
    ///
    /// Returns -1 when the page is not visible here, so a caller can decline rather than land the
    /// user somewhere arbitrary — a Windows-only page linked from a Linux machine has no honest
    /// destination.
    /// </remarks>
    public static int PositionOfPage(IReadOnlyList<TutorialPage> visible, int authorPageIndex)
    {
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i].PageIndex == authorPageIndex) return i;
        }

        return -1;
    }
}
