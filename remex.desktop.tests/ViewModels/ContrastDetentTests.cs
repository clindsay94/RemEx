using System;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The Contrast slider's detents (RemEx-4kv0g.16, PC half). The slider is a continuous -1..1 track,
/// so letting go "in the middle" stored 0.0027 while its label read 0.0 and the palette was built
/// for a contrast that was not 0. A direct write now lands on -1, 0 or 1 when it is within 0.05.
/// </summary>
public class ContrastDetentTests
{
    [Theory]
    [InlineData(0.0027, 0.0)]
    [InlineData(-0.0008, 0.0)]
    [InlineData(0.049, 0.0)]
    [InlineData(-0.049, 0.0)]
    [InlineData(0.97, 1.0)]
    [InlineData(-0.96, -1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-1.0, -1.0)]
    [InlineData(0.0, 0.0)]
    public void ValuesInsideADetentLandOnIt(double value, double expected)
        => CustomizationViewModel.SnapContrastToDetent(value).Should().Be(expected);

    [Theory]
    // 0.05 itself is outside: the band is strictly less than the radius, which is what keeps the
    // 0.1 keyboard steps (and anything a hand places deliberately) exactly where they were put.
    [InlineData(0.05)]
    [InlineData(-0.05)]
    [InlineData(0.1)]
    [InlineData(-0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(-0.94)]
    public void ValuesOutsideEveryDetentAreLeftAlone(double value)
        => CustomizationViewModel.SnapContrastToDetent(value).Should().Be(value);

    [Fact]
    public void ADirectWriteNearTheCentreIsStoredAndSavedAsZero()
    {
        var savedHost = App.EmbeddedHostServices;
        DashboardLayoutService? layoutService = null;
        try
        {
            App.EmbeddedHostServices = null;
            var theme = new ThemeService { PostToUiThread = action => action() };
            layoutService = new DashboardLayoutService(theme);
            var vm = new CustomizationViewModel(null!, layoutService, theme);

            vm.ThemeContrast = 0.0027;

            vm.ThemeContrast.Should().Be(0.0, "the slider's write snaps onto the centre detent");
            layoutService.CurrentProfile.Customization.ThemeContrast.Should().Be(0.0,
                "the snapped value, not the raw one, is what gets saved");
        }
        finally
        {
            App.EmbeddedHostServices = savedHost;
            layoutService?.Dispose();
        }
    }

    [Fact]
    public void ADirectWriteAwayFromEveryDetentIsKeptExactly()
    {
        var savedHost = App.EmbeddedHostServices;
        DashboardLayoutService? layoutService = null;
        try
        {
            App.EmbeddedHostServices = null;
            var theme = new ThemeService { PostToUiThread = action => action() };
            layoutService = new DashboardLayoutService(theme);
            var vm = new CustomizationViewModel(null!, layoutService, theme);

            vm.ThemeContrast = 0.3;

            vm.ThemeContrast.Should().Be(0.3);
            layoutService.CurrentProfile.Customization.ThemeContrast.Should().Be(0.3);
        }
        finally
        {
            App.EmbeddedHostServices = savedHost;
            layoutService?.Dispose();
        }
    }
}
