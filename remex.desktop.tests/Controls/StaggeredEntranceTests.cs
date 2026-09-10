using FluentAssertions;
using Remex.Desktop.Controls;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

public class StaggeredEntranceTests
{
    public StaggeredEntranceTests()
    {
        StaggeredEntrance.ResetForTests();
    }

    [Fact]
    public void FirstCallForAKeyPlays()
    {
        StaggeredEntrance.ShouldPlay("HomeView", reducedMotion: false).Should().BeTrue();
    }

    [Fact]
    public void SecondCallForTheSameKeyDoesNotPlay()
    {
        StaggeredEntrance.ShouldPlay("HomeView", reducedMotion: false).Should().BeTrue();
        StaggeredEntrance.ShouldPlay("HomeView", reducedMotion: false).Should().BeFalse();
    }

    [Fact]
    public void DifferentKeysAreIndependent()
    {
        StaggeredEntrance.ShouldPlay("HomeView", reducedMotion: false).Should().BeTrue();
        StaggeredEntrance.ShouldPlay("RemoteView", reducedMotion: false).Should().BeTrue();
    }

    [Fact]
    public void ReducedMotionNeverPlaysAndDoesNotRecord()
    {
        StaggeredEntrance.ShouldPlay("HomeView", reducedMotion: true).Should().BeFalse();
        StaggeredEntrance.ShouldPlay("HomeView", reducedMotion: false).Should().BeTrue();
    }
}
