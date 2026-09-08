using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-9iz00.1: the first-run tutorial overlay moved from 17 hand-toggled <c>IsVisible</c>
/// pages to a real <c>Carousel</c> bound to <c>ShellViewModel.TutorialPageIndex</c>, with a
/// <c>PipsPager</c> and the Back/Next/Finish buttons all reading the same index. These tests pin
/// the view-model contract that binding depends on: the index can never leave
/// <c>[0, TutorialPageCount - 1]</c> no matter what sets it (the Carousel's own SelectedIndex
/// binding included), Previous/Next disable at the ends rather than silently no-op forever, and
/// showing the overlay is always a fresh start at page 0.
/// </summary>
public sealed class ShellViewModelTutorialPagingTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public ShellViewModelTutorialPagingTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-9iz00-1-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider(),
            transferQueuePost: action => action());
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void IndexClampsToZeroWhenSetBelowRange()
    {
        _shell.TutorialPageIndex = -7;
        _shell.TutorialPageIndex.Should().Be(0,
            "a Carousel SelectedIndex binding or any other caller can hand this a negative value");
    }

    [Fact]
    public void IndexClampsToTheLastPageWhenSetAboveRange()
    {
        _shell.TutorialPageIndex = 999;
        _shell.TutorialPageIndex.Should().Be(_shell.TutorialPageCount - 1,
            "the PipsPager and the Finish button both key off TutorialPageCount - 1 being the real ceiling");
    }

    [Fact]
    public void PreviousIsDisabledOnTheFirstPage()
    {
        _shell.TutorialPageIndex = 0;
        _shell.TutorialPreviousCommand.CanExecute(null).Should().BeFalse();
        _shell.TutorialNextCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void NextIsDisabledOnTheLastPageWhereFinishDismissesInstead()
    {
        _shell.TutorialPageIndex = _shell.TutorialPageCount - 1;

        _shell.TutorialNextCommand.CanExecute(null).Should().BeFalse(
            "Finish takes over on the last page - Next has nowhere left to go");
        _shell.TutorialPreviousCommand.CanExecute(null).Should().BeTrue();

        _shell.ShowTutorialOverlay = true;
        _shell.TutorialFinishCommand.Execute(null);
        _shell.ShowTutorialOverlay.Should().BeFalse("Finish is the last page's dismiss action");
    }

    [Fact]
    public void NextAndPreviousStayInSyncAsTheIndexMoves()
    {
        // Walk the whole deck forward, then back, and check the two commands never agree that
        // movement is possible off the end they are supposed to guard.
        _shell.TutorialPageIndex = 0;
        while (_shell.TutorialNextCommand.CanExecute(null))
            _shell.TutorialNextCommand.Execute(null);

        _shell.TutorialPageIndex.Should().Be(_shell.TutorialPageCount - 1);

        while (_shell.TutorialPreviousCommand.CanExecute(null))
            _shell.TutorialPreviousCommand.Execute(null);

        _shell.TutorialPageIndex.Should().Be(0);
    }

    [Fact]
    public void ShowingTheOverlayResetsToPageZero()
    {
        _shell.TutorialPageIndex = 5;
        _shell.ShowTutorialOverlay = true;

        _shell.TutorialPageIndex.Should().Be(0,
            "the overlay flag itself resets the page, not just the two call sites that happened to set it first");
    }

    [Fact]
    public void ReplayingTheTutorialAlsoResetsToPageZero()
    {
        _shell.TutorialPageIndex = 3;
        _shell.ShowTutorialOverlay = false;

        _shell.ReplayTutorialCommand.Execute(null);

        _shell.TutorialPageIndex.Should().Be(0);
        _shell.ShowTutorialOverlay.Should().BeTrue();
    }

    // ─── RemEx-9iz00.1 fix round (HIGH): the PipsPager made every one of the 17 carousel slots
    // reachable, including pages SupportedPlatforms excludes for the running platform. These pin
    // the platform-filtered visible-page list and the snap that keeps TutorialPageIndex out of the
    // pages it hides, using TutorialPlatformOverride (internal, InternalsVisibleTo) rather than the
    // real OS so the same assertions run identically on every CI platform.

    [Theory]
    [InlineData(PlatformFlags.Windows)]
    [InlineData(PlatformFlags.Linux)]
    public void VisiblePageCountIsSmallerThanTheFullDeckOnEveryPlatform(PlatformFlags platform)
    {
        _shell.TutorialPlatformOverride = platform;

        _shell.TutorialVisiblePageCount.Should().BeLessThan(_shell.TutorialPageCount,
            "HWiNFO (Windows-only) and Quick Settings (Android-only) each hide at least one page " +
            "on every platform, so the PipsPager's dot count must never equal the raw 17");
    }

    [Fact]
    public void SettingAHiddenPageIndexSnapsForwardToTheNearestVisiblePage()
    {
        _shell.TutorialPlatformOverride = PlatformFlags.Linux;

        _shell.TutorialPageIndex = 2; // HWiNFO - Windows-only, hidden on Linux

        _shell.TutorialPageIndex.Should().Be(3,
            "page 3 (Dashboard, all platforms) is the next visible page after the hidden HWiNFO " +
            "page - a Linux user must never land on a Windows-only page");
    }

    [Fact]
    public void VisiblePageIndexRoundTripsThroughTheVisibleList()
    {
        _shell.TutorialPlatformOverride = PlatformFlags.Linux;
        var visible = _shell.VisibleTutorialPageIndices;

        for (var i = 0; i < visible.Count; i++)
        {
            _shell.TutorialVisiblePageIndex = i;
            _shell.TutorialVisiblePageIndex.Should().Be(i);
            _shell.TutorialPageIndex.Should().Be(visible[i]);
        }
    }

    [Fact]
    public void PushingAnOutOfRangeVisibleIndexClampsAndNotifies()
    {
        _shell.TutorialPlatformOverride = PlatformFlags.Linux;
        var raised = new List<string>();
        _shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        _shell.TutorialVisiblePageIndex = 999;

        _shell.TutorialVisiblePageIndex.Should().Be(_shell.TutorialVisiblePageCount - 1);
        raised.Should().Contain(nameof(ShellViewModel.TutorialVisiblePageIndex));
        raised.Should().Contain(nameof(ShellViewModel.TutorialPageIndex));
    }

    [Fact]
    public void SettingAnOutOfRangeIndexRaisesPropertyChangedEvenWhenTheStoredValueDoesNotMove()
    {
        // RemEx-9iz00.1 fix round (LOW, folded into the HIGH fix): a coerced clamp that leaves the
        // field unchanged still has to push the correction back onto a two-way binding source, or
        // the Carousel/PipsPager would keep showing the invalid value it sent forever.
        _shell.TutorialPageIndex = _shell.TutorialPageCount - 1; // already at the ceiling

        var raised = new List<string>();
        _shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        _shell.TutorialPageIndex = 999; // clamps to the same ceiling - the field itself never moves

        _shell.TutorialPageIndex.Should().Be(_shell.TutorialPageCount - 1);
        raised.Should().Contain(nameof(ShellViewModel.TutorialPageIndex),
            "the binding source that pushed 999 needs to be told the real value is still the last " +
            "page, even though SetProperty saw no field change and stayed silent");
        raised.Should().Contain(nameof(ShellViewModel.TutorialVisiblePageIndex));
    }

    [Fact]
    public void PushingAnOutOfRangeVisibleIndexOnTheLastVisiblePageStillNotifies()
    {
        // RemEx-9iz00.1 fix round (LOW): TutorialVisiblePageIndex's setter clamps, then assigns
        // TutorialPageIndex - when that assignment is a no-op (already on the last visible page),
        // TutorialPageIndex's own SetProperty stays silent, so nothing corrected the out-of-range
        // push back onto TutorialVisiblePageIndex itself without the mirrored notify this fixes.
        _shell.TutorialPlatformOverride = PlatformFlags.Linux;
        _shell.TutorialVisiblePageIndex = _shell.TutorialVisiblePageCount - 1; // already at the ceiling

        var raised = new List<string>();
        _shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        _shell.TutorialVisiblePageIndex = 999; // clamps to the same ceiling - the field never moves

        _shell.TutorialVisiblePageIndex.Should().Be(_shell.TutorialVisiblePageCount - 1);
        raised.Should().Contain(nameof(ShellViewModel.TutorialVisiblePageIndex),
            "the binding source that pushed 999 needs to be told the real value is still the last " +
            "visible page, even though the underlying TutorialPageIndex never moved and stayed silent");
    }
}
