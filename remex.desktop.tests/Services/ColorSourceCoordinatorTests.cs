using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The pure half of the coordinator (a source colour supplies hue and tone, the profile's own
/// vibrancy supplies chroma, so the Vibrancy slider keeps shaping a seed the person cannot edit),
/// and the source GATE itself (RemEx-8twk0.3 review, HIGH): before this class covered
/// <c>Apply</c>, the method that actually decides whether a Windows-accent change touches the
/// saved profile had zero coverage.
/// </summary>
/// <remarks>
/// THE Apply TESTS BUILD REAL COLLABORATORS, not mocks: a <see cref="DashboardLayoutService"/>
/// redirected to a private per-test temp directory, exactly like
/// <see cref="DashboardLayoutClobberTests"/>; a <see cref="ThemeService"/> made headless the way
/// <see cref="HardwareAccentInjectionTests"/> does; and a real <see cref="WindowsAccentWatcher"/>
/// on the fake clock <see cref="ManualTimeProvider"/> shares with
/// <see cref="WindowsAccentWatcherTests"/>. <c>Apply</c> never drives the watcher itself here, so
/// its read function is never called — only <see cref="ColorSourceCoordinator.Apply"/> is under
/// test.
/// </remarks>
public class ColorSourceCoordinatorTests : IDisposable
{
    // OWN TEMP DIRECTORY PER TEST, same reason as DashboardLayoutClobberTests: a
    // DashboardLayoutService built through the public constructor shares the one
    // assembly-redirected dashboard_layout.json, and the Apply tests below deliberately read that
    // file's bytes off disk to prove a save was, or was not, queued. Unused by the pure
    // ShapedBySource tests below, which never touch a DashboardLayoutService at all.
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "remex-color-source-coordinator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void ShapedBySource_TakesHueAndToneFromTheSourceAndChromaFromTheProfile()
    {
        var settings = new CustomizationSettings { ThemeSeedChromaRequest = 20.0, AccentColor = "#6C4CFF" };

        var shaped = ColorSourceCoordinator.ShapedBySource(settings, "#0078D4");

        var (sourceHue, _, sourceTone) = SeedHct.FromColor(Color.Parse("#0078D4"));
        var (hue, chroma, tone) = SeedHct.FromColor(Color.Parse(shaped.AccentColor));
        hue.Should().BeApproximately(sourceHue, 2.0);
        tone.Should().BeApproximately(sourceTone, 2.0);
        chroma.Should().BeLessOrEqualTo(21.0, "the profile's vibrancy REQUEST, not the source's chroma, shapes the seed");
        shaped.ThemeSeedChroma.Should().BeApproximately(chroma, 0.01, "what was achieved is what is persisted (RemEx-ndhlv)");
        shaped.ThemeSeedChromaRequest.Should().Be(20.0,
            "the request itself must never change just because the achieved chroma did (RemEx-ceu4x)");
    }

    [Fact]
    public void ShapedBySource_LeavesEveryOtherFieldAlone()
    {
        var settings = DashboardLayoutClobberTests.BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);

        var shaped = ColorSourceCoordinator.ShapedBySource(settings, "#0078D4");

        shaped.Should().BeEquivalentTo(settings, o => o.Excluding(s => s.AccentColor).Excluding(s => s.ThemeSeedChroma));
    }

    /// <summary>
    /// RemEx-ceu4x. Before this fix, <c>ShapedBySource</c> re-requested whatever chroma was last
    /// ACHIEVED, so a hue that could not hold the request permanently lowered the ceiling for every
    /// hue after it. Two hues that cannot hold a chroma of 90 (HCT's chroma envelope collapses to
    /// almost nothing at the tone extremes, regardless of hue) must each still leave the REQUEST at
    /// 90, and a hue that CAN hold 90 — tried twice, with a low hue in between — must get the full
    /// chroma back both times.
    /// </summary>
    [Fact]
    public void ShapedBySource_TheRequestSurvivesHuesThatCannotHoldItAndAGoodHueGetsItBack()
    {
        var settings = new CustomizationSettings { ThemeSeedChromaRequest = 90.0, AccentColor = "#6C4CFF" };

        const string nearWhite = "#F2F2F2";
        const string nearBlack = "#0A0A0A";
        const string pureGreen = "#00FF00";
        const string pureRed = "#FF0000";

        var afterNearWhite = ColorSourceCoordinator.ShapedBySource(settings, nearWhite);
        afterNearWhite.ThemeSeedChromaRequest.Should().Be(90.0, "a hue that cannot hold 90 must not touch the request");
        afterNearWhite.ThemeSeedChroma.Should().BeLessThan(90.0, "a near-white hue cannot hold anywhere near chroma 90");

        var afterNearBlack = ColorSourceCoordinator.ShapedBySource(afterNearWhite, nearBlack);
        afterNearBlack.ThemeSeedChromaRequest.Should().Be(90.0, "a second bad hue must not touch the request either");
        afterNearBlack.ThemeSeedChroma.Should().BeLessThan(90.0, "a near-black hue cannot hold chroma 90 either");
        afterNearBlack.ThemeSeedChroma.Should().NotBeApproximately(afterNearWhite.ThemeSeedChroma, 0.01,
            "what was achieved tracks the CURRENT hue, not a value inherited from the previous one");

        var afterFirstGoodHue = ColorSourceCoordinator.ShapedBySource(afterNearBlack, pureGreen);
        afterFirstGoodHue.ThemeSeedChromaRequest.Should().Be(90.0);
        afterFirstGoodHue.ThemeSeedChroma.Should().BeApproximately(90.0, 3.0,
            "a hue that CAN hold 90 must get the full request back, not whatever the worst prior hue allowed");

        var afterSecondGoodHue = ColorSourceCoordinator.ShapedBySource(afterFirstGoodHue, pureRed);
        afterSecondGoodHue.ThemeSeedChromaRequest.Should().Be(90.0);
        afterSecondGoodHue.ThemeSeedChroma.Should().BeApproximately(90.0, 3.0,
            "the ratchet is gone: a second good hue in a row still gets the full chroma back");
    }

    [Fact]
    public void ShapedBySource_ReturnsTheSameInstanceForAnUnparseableSource()
    {
        var settings = new CustomizationSettings();

        ColorSourceCoordinator.ShapedBySource(settings, "#FF0O00").Should().BeSameAs(settings);
    }

    private async Task<(DashboardLayoutService Layout, ColorSourceCoordinator Coordinator)> BuildAsync()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(Path.Combine(_tempDirectory, "dashboard_layout.json"), theme);
        await layout.LoadAsync();
        var watcher = new WindowsAccentWatcher(() => null, new ManualTimeProvider());
        var coordinator = new ColorSourceCoordinator(layout, theme, watcher);
        return (layout, coordinator);
    }

    /// <summary>Sets the profile's colour source through the service's own save API, not by hand-editing JSON.</summary>
    private static async Task SetColorSourceAsync(DashboardLayoutService layout, string colorSource)
    {
        layout.RequestSave(layout.CurrentProfile with
        {
            Customization = layout.CurrentProfile.Customization with { ColorSource = colorSource },
        });
        await layout.FlushAsync();
    }

    [Theory]
    [InlineData(ColorSources.Custom)]
    [InlineData(ColorSources.Wallpaper)]
    public async Task Apply_LeavesTheProfileAloneWhenTheSourceIsNotTheWindowsAccent(string colorSource)
    {
        var (layout, coordinator) = await BuildAsync();
        await SetColorSourceAsync(layout, colorSource);
        var before = layout.CurrentProfile;
        var onDiskBefore = await File.ReadAllTextAsync(layout.FilePathForTests);

        coordinator.Apply("#123456");
        // Flush BEFORE reading the file back: RequestSave is debounced, so without this a wrongly
        // queued save would not have reached disk yet and the on-disk assertion could not fail.
        await layout.FlushAsync();

        layout.CurrentProfile.Should().Be(before,
            $"a {colorSource} source must not let a Windows-accent change touch the profile");
        (await File.ReadAllTextAsync(layout.FilePathForTests)).Should().Be(onDiskBefore,
            "no save should have been queued");
    }

    [Fact]
    public async Task Apply_WritesTheShapedSeedAndSavesWhenTheSourceIsTheWindowsAccent()
    {
        var (layout, coordinator) = await BuildAsync();
        await SetColorSourceAsync(layout, ColorSources.WindowsAccent);
        var settingsBeforeApply = layout.CurrentProfile.Customization;
        var expectedShaped = ColorSourceCoordinator.ShapedBySource(settingsBeforeApply, "#0078D4");

        coordinator.Apply("#0078D4");
        await layout.FlushAsync();

        layout.CurrentProfile.Customization.AccentColor.Should().Be(expectedShaped.AccentColor,
            "the Windows accent must be shaped into the seed the same way ShapedBySource does");
        (await File.ReadAllTextAsync(layout.FilePathForTests)).Should().Contain(expectedShaped.AccentColor,
            "a shaped accent change must be saved to disk");
    }

    [Fact]
    public async Task Apply_TheSameHexTwiceIsANoOpTheSecondTime()
    {
        var (layout, coordinator) = await BuildAsync();
        await SetColorSourceAsync(layout, ColorSources.WindowsAccent);

        coordinator.Apply("#0078D4");
        await layout.FlushAsync();
        var afterFirstApply = layout.CurrentProfile;
        var onDiskAfterFirstApply = await File.ReadAllTextAsync(layout.FilePathForTests);

        coordinator.Apply("#0078D4");
        await layout.FlushAsync();

        layout.CurrentProfile.Should().Be(afterFirstApply,
            "the unchanged-accent short-circuit must make the second identical Apply a no-op");
        (await File.ReadAllTextAsync(layout.FilePathForTests)).Should().Be(onDiskAfterFirstApply,
            "no further save should have been queued for an identical accent");
    }

    [Fact]
    public async Task Apply_AnUnparseableHexChangesNothing()
    {
        var (layout, coordinator) = await BuildAsync();
        await SetColorSourceAsync(layout, ColorSources.WindowsAccent);
        var before = layout.CurrentProfile;
        var onDiskBefore = await File.ReadAllTextAsync(layout.FilePathForTests);

        coordinator.Apply("#FF0O00");
        await layout.FlushAsync(); // same reason as above: let a wrongly queued save land before looking

        layout.CurrentProfile.Should().Be(before, "an unparseable hex must leave the profile untouched");
        (await File.ReadAllTextAsync(layout.FilePathForTests)).Should().Be(onDiskBefore,
            "no save should have been queued for a hex ShapedBySource could not parse");
    }

    /// <summary>
    /// RemEx-ceu4x round-trip: the sheet used to seed the Vibrancy slider from the ACHIEVED chroma
    /// of the persisted <c>AccentColor</c>, so reopening it after even one hue that could not hold
    /// the request showed the slider parked wherever that hue left it, not where the person put it.
    /// </summary>
    [Fact]
    public async Task ReopeningTheSheetShowsTheRequestOnTheSliderNotTheAchievedValue()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(Path.Combine(_tempDirectory, "dashboard_layout.json"), theme);

        // A seed whose OWN chroma is 60 (achievable at this hue/tone), but a request of 90 sitting
        // alongside it - exactly the gap ShapedBySource leaves behind when a Windows-accent hue
        // could not hold the full ask.
        var seedWithAchievedSixty = SeedHct.ToHex(hue: 260, chroma: 60, tone: 45);
        var settings = new CustomizationSettings
        {
            SchemaVersion = CustomizationMigration.CurrentSchemaVersion,
            ColorSource = ColorSources.WindowsAccent,
            AccentColor = seedWithAchievedSixty,
            ThemeSeedChroma = SeedHct.ChromaOf(seedWithAchievedSixty, 60.0),
            ThemeSeedChromaRequest = 90.0,
        };
        await File.WriteAllTextAsync(
            layout.FilePathForTests,
            System.Text.Json.JsonSerializer.Serialize(
                new DashboardProfile { Customization = settings }, DashboardLayoutService.JsonOptions));
        await layout.LoadAsync();

        var vm = new CustomizationViewModel(null!, layout, theme);

        vm.SeedChroma.Should().Be(90.0, "the slider must show the REQUEST the person made");
        vm.SeedChroma.Should().NotBe(layout.CurrentProfile.Customization.ThemeSeedChroma,
            "not the achieved chroma of whatever hue last shaped the seed");
    }

    /// <summary>
    /// RemEx-aidt1 review LOW. The constructor seeds <c>_seedChroma</c> from the persisted request
    /// by a direct field write, bypassing <c>OnSeedChromaChanged</c>'s
    /// <see cref="SeedHct.MaxChroma"/> clamp (RemEx-8twk0.8 gate addendum). A hand-edited profile
    /// carrying a request outside the wheel's own range must not reopen the sheet with the slider
    /// reporting a number the wheel itself cannot reach.
    /// </summary>
    [Fact]
    public async Task ReopeningTheSheetClampsAHandEditedRequestToTheWheelsRange()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(Path.Combine(_tempDirectory, "dashboard_layout.json"), theme);

        var settings = new CustomizationSettings
        {
            SchemaVersion = CustomizationMigration.CurrentSchemaVersion,
            ThemeSeedChromaRequest = 500.0,
        };
        await File.WriteAllTextAsync(
            layout.FilePathForTests,
            System.Text.Json.JsonSerializer.Serialize(
                new DashboardProfile { Customization = settings }, DashboardLayoutService.JsonOptions));
        await layout.LoadAsync();

        var vm = new CustomizationViewModel(null!, layout, theme);

        vm.SeedChroma.Should().Be(SeedHct.MaxChroma,
            "a request outside the wheel's own range must clamp at construction the same way a live edit does");
    }

    /// <summary>
    /// RemEx-aidt1 main finding. <c>ColorSourceCoordinator.Apply</c> can run while the sheet is
    /// open — the coordinator is always alive, and the sheet is only ever an additional listener on
    /// <c>ThemeService.CustomizationApplied</c>. Before this fix, the view model's handler assigned
    /// <c>AccentColor</c> directly, which ran <c>SyncSeedFromAccent</c> UNGUARDED and overwrote the
    /// live Vibrancy REQUEST with whatever the just-applied hue could ACHIEVE — silently, with
    /// nothing persisted until the next unrelated control was touched, at which point the corrupted
    /// value became the new persisted request.
    /// </summary>
    [Fact]
    public async Task ACustomizationAppliedFromTheCoordinatorKeepsTheLiveRequestEvenWhenTheHueCannotHoldIt()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(Path.Combine(_tempDirectory, "dashboard_layout.json"), theme);
        await layout.LoadAsync();

        layout.RequestSave(layout.CurrentProfile with
        {
            Customization = layout.CurrentProfile.Customization with
            {
                ColorSource = ColorSources.WindowsAccent,
                ThemeSeedChromaRequest = 90.0,
            },
        });
        await layout.FlushAsync();

        var watcher = new WindowsAccentWatcher(() => null, new ManualTimeProvider());
        var coordinator = new ColorSourceCoordinator(layout, theme, watcher);
        // Constructed AFTER the profile above is saved, so the VM's own construction-time seeding
        // (the test above) starts it at the same request of 90 the coordinator is about to shape a
        // near-white hue against.
        var vm = new CustomizationViewModel(null!, layout, theme);

        // Near-white: HCT's chroma envelope collapses toward the tone extremes regardless of hue,
        // so this cannot hold anywhere near 90.
        coordinator.Apply("#F2F2F2");
        await layout.FlushAsync();

        vm.SeedChroma.Should().Be(90.0,
            "a background accent sync while the sheet is open must not overwrite the live request");
        vm.AccentColor.Should().Be(layout.CurrentProfile.Customization.AccentColor,
            "the wheel must still show the shaped seed the coordinator just applied");

        // The corruption this bead fixes only reached disk on the NEXT save — nudge an unrelated
        // control and check what actually got persisted.
        vm.ThemeContrast = 0.3;
        await layout.FlushAsync();

        layout.CurrentProfile.Customization.ThemeSeedChromaRequest.Should().Be(90.0,
            "an unrelated save must not persist a request the accent sync silently overwrote");
    }
}
