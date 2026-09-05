using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
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
        var settings = new CustomizationSettings { ThemeSeedChroma = 20.0, AccentColor = "#6C4CFF" };

        var shaped = ColorSourceCoordinator.ShapedBySource(settings, "#0078D4");

        var (sourceHue, _, sourceTone) = SeedHct.FromColor(Color.Parse("#0078D4"));
        var (hue, chroma, tone) = SeedHct.FromColor(Color.Parse(shaped.AccentColor));
        hue.Should().BeApproximately(sourceHue, 2.0);
        tone.Should().BeApproximately(sourceTone, 2.0);
        chroma.Should().BeLessOrEqualTo(21.0, "the profile's vibrancy, not the source's chroma, shapes the seed");
        shaped.ThemeSeedChroma.Should().BeApproximately(chroma, 0.01, "what was achieved is what is persisted (RemEx-ndhlv)");
    }

    [Fact]
    public void ShapedBySource_LeavesEveryOtherFieldAlone()
    {
        var settings = DashboardLayoutClobberTests.BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);

        var shaped = ColorSourceCoordinator.ShapedBySource(settings, "#0078D4");

        shaped.Should().BeEquivalentTo(settings, o => o.Excluding(s => s.AccentColor).Excluding(s => s.ThemeSeedChroma));
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
}
