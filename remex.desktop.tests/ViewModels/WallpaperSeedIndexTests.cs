using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class WallpaperSeedIndexTests
{
    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(5, 5, 0)]   // wallpaper changed, fewer candidates: first candidate, index reset
    [InlineData(-1, 5, 0)]
    [InlineData(2, 0, 0)]
    public void AnOutOfRangeStoredIndexFallsBackToTheFirstCandidate(int stored, int count, int expected)
    {
        CustomizationViewModel.ResolveWallpaperSeedIndex(stored, count).Should().Be(expected);
    }
}
