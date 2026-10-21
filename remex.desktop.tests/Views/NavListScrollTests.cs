using FluentAssertions;
using Remex.Desktop.Views;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>RemEx-172ba: the drawer opens with the current page's item on screen, Home at the very top.</summary>
public class NavListScrollTests
{
    [Fact]
    public void TheFirstItemAlwaysScrollsToTheTop() =>
        NavListScroll.OffsetToReveal(itemTop: 8, itemHeight: 48, viewportHeight: 200, currentOffset: 458, isFirstItem: true)
            .Should().Be(0);

    [Fact]
    public void AnItemAboveTheViewportIsRevealedAtTheTop() =>
        NavListScroll.OffsetToReveal(itemTop: 60, itemHeight: 48, viewportHeight: 200, currentOffset: 400, isFirstItem: false)
            .Should().Be(60);

    [Fact]
    public void AnItemBelowTheViewportIsRevealedAtTheBottom() =>
        NavListScroll.OffsetToReveal(itemTop: 600, itemHeight: 48, viewportHeight: 200, currentOffset: 0, isFirstItem: false)
            .Should().Be(448);

    [Fact]
    public void AnItemAlreadyOnScreenLeavesTheListWhereItIs() =>
        NavListScroll.OffsetToReveal(itemTop: 300, itemHeight: 48, viewportHeight: 200, currentOffset: 250, isFirstItem: false)
            .Should().Be(250);
}
