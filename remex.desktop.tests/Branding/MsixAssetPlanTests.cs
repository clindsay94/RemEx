using FluentAssertions;
using Remex.Branding;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

public class MsixAssetPlanTests
{
    [Theory]
    [InlineData("Square44x44Logo.scale-100.png", 44, 44)]
    [InlineData("Square44x44Logo.scale-200.png", 88, 88)]
    [InlineData("Square150x150Logo.scale-400.png", 600, 600)]
    [InlineData("Wide310x150Logo.scale-100.png", 310, 150)]
    [InlineData("Wide310x150Logo.scale-200.png", 620, 300)]
    [InlineData("LargeTile.scale-100.png", 310, 310)]
    [InlineData("SmallTile.scale-100.png", 71, 71)]
    [InlineData("StoreLogo.scale-150.png", 75, 75)]
    [InlineData("SplashScreen.scale-100.png", 620, 300)]
    [InlineData("SplashScreen.scale-400.png", 2480, 1200)]
    [InlineData("Square44x44Logo.targetsize-256.png", 256, 256)]
    [InlineData("Square44x44Logo.altform-unplated_targetsize-16.png", 16, 16)]
    [InlineData("Square44x44Logo.altform-lightunplated_targetsize-48.png", 48, 48)]
    public void Dimensions_MapsKnownAssets(string file, int w, int h)
    {
        MsixAssetPlan.Dimensions(file).Should().Be((w, h));
    }

    [Fact]
    public void Dimensions_ReturnsNull_ForUnknownFile()
    {
        MsixAssetPlan.Dimensions("MysteryLogo.scale-100.png").Should().BeNull();
    }
}
