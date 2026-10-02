using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The PC's Reduced motion switch starts from the OS setting, and a stored choice wins (sweep D8).
/// </summary>
/// <remarks>
/// The phone follows Android's "remove animations"; the PC's switch used to start off for everyone and
/// never look at Windows or the Linux desktop, so a user who had turned animations off system-wide
/// still got sliding pages and a moving background.
/// </remarks>
public sealed class ReducedMotionFollowsTheOsTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel? _shell;

    public ReducedMotionFollowsTheOsTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-reduced-motion-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync() => await _layoutService.LoadAsync();

    public Task DisposeAsync()
    {
        _shell?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private ShellViewModel Shell(bool? osPrefersReduced)
    {
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider(),
            osPrefersReducedMotion: () => osPrefersReduced);
        return _shell;
    }

    [Fact]
    public void WithNoStoredChoiceTheOsDecides_AndNothingIsSaved()
    {
        var shell = Shell(osPrefersReduced: true);

        shell.IsReducedMotion.Should().BeTrue("the OS asks for reduced motion and the user has not chosen");
        _layoutService.CurrentProfile!.IsReducedMotionSet.Should().BeFalse(
            "following the OS is not a choice, so it must not be written back as one");
        _layoutService.CurrentProfile.IsReducedMotion.Should().BeFalse();
    }

    [Fact]
    public async Task AStoredChoiceWinsOverTheOs()
    {
        _layoutService.RequestSave(_layoutService.CurrentProfile! with { IsReducedMotion = false, IsReducedMotionSet = true });
        await _layoutService.FlushAsync();

        Shell(osPrefersReduced: true).IsReducedMotion.Should().BeFalse(
            "the user turned it off on purpose; the OS setting must not override that");
    }

    [Fact]
    public void FlippingTheSwitchMakesItAChoice()
    {
        var shell = Shell(osPrefersReduced: true);

        shell.IsReducedMotion = false;

        _layoutService.CurrentProfile!.IsReducedMotionSet.Should().BeTrue();
        _layoutService.CurrentProfile.IsReducedMotion.Should().BeFalse();
    }

    [Theory]
    // stored, isChoice, os => expected
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, null, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)] // an old profile holding "on" was the user's doing
    [InlineData(true, true, false, true)]
    public void ResolveTruthTable(bool stored, bool isChoice, bool? os, bool expected) =>
        SystemMotionPreference.Resolve(stored, isChoice, os).Should().Be(expected);

    [Theory]
    [InlineData("[KDE]\nAnimationDurationFactor=0\n", true)]
    [InlineData("[KDE]\nAnimationDurationFactor=0.5\n", false)]
    [InlineData("[General]\nAnimationDurationFactor=0\n", null)]
    [InlineData("[KDE]\nSingleClick=false\n", null)]
    public void KdeGlobalsAreRead(string text, bool? expected) =>
        SystemMotionPreference.ParseKdeGlobals(text).Should().Be(expected);

    [Theory]
    [InlineData("[Settings]\ngtk-enable-animations=false\n", true)]
    [InlineData("[Settings]\ngtk-enable-animations=0\n", true)]
    [InlineData("[Settings]\ngtk-enable-animations=true\n", false)]
    [InlineData("[Settings]\ngtk-theme-name=Adwaita\n", null)]
    public void GtkSettingsAreRead(string text, bool? expected) =>
        SystemMotionPreference.ParseGtkSettingsIni(text).Should().Be(expected);
}
