using System;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// "Reset look to defaults" asks first and fails closed, like every other destructive action
/// (<see cref="DestructiveActionFailClosedTests"/>): no delegate or a "No" changes nothing.
/// </summary>
public class CustomizationResetConfirmTests : IDisposable
{
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private (CustomizationViewModel Vm, DashboardLayoutService Layout) MakeVm()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        layout.RequestSave(new DashboardProfile
        {
            Customization = new CustomizationSettings { AccentColor = "#FF8800", SplashStyle = "Pong" },
        });
        return (new CustomizationViewModel(null!, layout, theme), layout);
    }

    [Fact]
    public async Task WithNoConfirmationDelegate_ResetDoesNothing()
    {
        var (vm, layout) = MakeVm();

        await vm.ResetToDefaultCommand.ExecuteAsync(null);

        layout.CurrentProfile.Customization.SplashStyle.Should().Be("Pong");
        layout.CurrentProfile.Customization.AccentColor.Should().Be("#FF8800");
    }

    [Fact]
    public async Task WhenDeclined_ResetDoesNothing()
    {
        var (vm, layout) = MakeVm();
        var asked = 0;
        vm.OnConfirmationRequested = (_, _, _) => { asked++; return Task.FromResult(false); };

        await vm.ResetToDefaultCommand.ExecuteAsync(null);

        asked.Should().Be(1);
        layout.CurrentProfile.Customization.SplashStyle.Should().Be("Pong");
        layout.CurrentProfile.Customization.AccentColor.Should().Be("#FF8800");
    }

    [Fact]
    public async Task WhenConfirmed_ResetGoesBackToTheGlassDefaults()
    {
        var (vm, layout) = MakeVm();
        string? title = null, message = null, button = null;
        vm.OnConfirmationRequested = (t, m, b) => { (title, message, button) = (t, m, b); return Task.FromResult(true); };

        await vm.ResetToDefaultCommand.ExecuteAsync(null);

        layout.CurrentProfile.Customization.SplashStyle.Should().Be("LiveHandshake");
        layout.CurrentProfile.Customization.AccentColor.Should().NotBe("#FF8800");
        title.Should().NotBeNullOrWhiteSpace();
        message.Should().NotBeNullOrWhiteSpace();
        button.Should().NotBeNullOrWhiteSpace();
    }
}
