using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf P0-18: <c>DashboardLayoutService.LoadAsyncCore</c> skips
/// <c>ThemeService.ApplyCustomization</c> when the loaded profile's customization already
/// content-equals <see cref="ThemeService.UserSettings"/>, and skips the last-seed sidecar
/// rewrite only when BOTH that apply was skipped AND this instance already wrote that exact
/// content to the sidecar this session. Round-1 review (HIGH) passed the logic but required
/// tests before it could land.
/// </summary>
/// <remarks>
/// OWN TEMP DIRECTORY FOR THE LAYOUT FILE, same reasoning as <c>DashboardLayoutPreLoadSaveTests</c>
/// and <c>DashboardLayoutClobberTests</c>: the shared redirected file the public constructor
/// resolves is a cross-test hazard for a class asserting exact apply/skip behaviour on a single
/// load. <see cref="LastSeedSidecar"/>'s file is NOT redirected per-instance (it hangs off
/// <c>RemexDataPaths.PerUserDirectory</c>, assembly-wide) so this class cleans it up before and
/// after every test the same way <c>LastSeedSidecarTests</c> does.
/// </remarks>
public sealed class DashboardLayoutSidecarSkipTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "remex-dashboard-layout-sidecar-skip-" + Guid.NewGuid().ToString("N"));

    public DashboardLayoutSidecarSkipTests() => CleanUpSidecar();

    public void Dispose()
    {
        CleanUpSidecar();
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void CleanUpSidecar()
    {
        var path = LastSeedSidecar.FilePath;
        if (File.Exists(path)) File.Delete(path);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        foreach (var stray in Directory.EnumerateFiles(directory, $".{Path.GetFileName(path)}.*.tmp"))
            File.Delete(stray);
    }

    private DashboardLayoutService NewService(ThemeService theme) =>
        new(Path.Combine(_tempDirectory, "dashboard_layout.json"), theme);

    private static CustomizationSettings Sample(string accent = "#224466") => new()
    {
        SchemaVersion = CustomizationMigration.CurrentSchemaVersion,
        AccentColor = accent,
        SchemeVariant = SchemeVariants.Vibrant,
        ThemeMode = ThemeModes.Dark,
        ThemeContrast = 0.25,
        FlyoutHiddenSensorIds = { "cpu-temp" },
    };

    private void WriteProfile(DashboardLayoutService service, CustomizationSettings customization)
    {
        var profile = new DashboardProfile { Customization = customization };
        var json = JsonSerializer.Serialize(profile, DashboardLayoutService.JsonOptions);
        File.WriteAllText(service.FilePathForTests, json);
    }

    /// <summary>Polls (not sleeps) for the sidecar file to appear, same pattern as
    /// <c>DashboardLayoutSaveOrderingTests.AQueuedSaveReachesDiskOnItsOwn_WithoutAnExplicitFlush</c>
    /// — the sidecar write is fire-and-forget from <c>DashboardLayoutService.LoadAsyncCore</c>,
    /// so nothing in this test assembly can await it directly.</summary>
    private static async Task<bool> WaitForSidecarAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (LastSeedSidecar.TryRead(out _)) return true;
            await Task.Delay(25);
        }
        return LastSeedSidecar.TryRead(out _);
    }

    // ---- (a) CustomizationContentEquals ----------------------------------------------------

    [Fact]
    public void CustomizationContentEquals_TwoSeparateDeserializationsOfTheSameJson_AreEqual()
    {
        var original = Sample();
        var json = JsonSerializer.Serialize(original, DashboardLayoutService.JsonOptions);

        var a = JsonSerializer.Deserialize<CustomizationSettings>(json, DashboardLayoutService.JsonOptions)!;
        var b = JsonSerializer.Deserialize<CustomizationSettings>(json, DashboardLayoutService.JsonOptions)!;

        ReferenceEquals(a, b).Should().BeFalse("anti-vacuity: these must be two distinct instances, not the same object");
        DashboardLayoutService.CustomizationContentEquals(a, b).Should().BeTrue(
            "two reads of identical JSON describe the same persisted content, even though the " +
            "record's own Equals would fail on the List members");
    }

    [Fact]
    public void CustomizationContentEquals_IsFalse_WhenOneListEntryDiffers()
    {
        var json = JsonSerializer.Serialize(Sample(), DashboardLayoutService.JsonOptions);
        var a = JsonSerializer.Deserialize<CustomizationSettings>(json, DashboardLayoutService.JsonOptions)!;
        var b = a with { FlyoutHiddenSensorIds = new() { "gpu-temp" } };

        DashboardLayoutService.CustomizationContentEquals(a, b).Should().BeFalse(
            "a single differing list entry is real content drift, not something the skip may ignore");
    }

    [Fact]
    public void CustomizationContentEquals_IsFalse_WhenOneDoubleDiffers()
    {
        var json = JsonSerializer.Serialize(Sample(), DashboardLayoutService.JsonOptions);
        var a = JsonSerializer.Deserialize<CustomizationSettings>(json, DashboardLayoutService.JsonOptions)!;
        var b = a with { ThemeContrast = a.ThemeContrast + 0.1 };

        DashboardLayoutService.CustomizationContentEquals(a, b).Should().BeFalse(
            "a single differing double is real content drift, not something the skip may ignore");
    }

    // ---- (b) apply is actually skipped, proven by reference identity -----------------------

    [Fact]
    public async Task LoadOfIdenticalContent_LeavesUserSettingsAsTheOriginalInstance()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        using var testService = NewService(theme);

        var c = Sample();
        theme.ApplyCustomization(c);
        WriteProfile(testService, c with { }); // a distinct instance, identical content

        await testService.LoadAsync();

        // If the apply had actually re-run (not just produced an equivalent result), UserSettings
        // would be a DIFFERENT CustomizationSettings instance — ApplyCustomization always assigns
        // whatever it is handed, and the deserialized profile is never the same object as `c`.
        ReferenceEquals(theme.UserSettings, c).Should().BeTrue(
            "content-equal load must skip ApplyCustomization entirely, not merely produce an " +
            "equivalent-looking result");

        // Review round 2 (MEDIUM): this service has not written a sidecar yet, so the first load
        // still triggers the fire-and-forget sidecar write even though the theme apply is skipped.
        // Waiting for it here keeps that background write from finishing after this test has torn
        // down its temp directory and landing on the next test's clean sidecar check instead.
        var written = await WaitForSidecarAsync(TimeSpan.FromSeconds(5));
        written.Should().BeTrue("the first load of the session writes the sidecar regardless of the theme-apply skip");
    }

    // ---- (c) genuinely different content DOES replace UserSettings -------------------------

    [Fact]
    public async Task LoadOfDifferentContent_ReplacesUserSettings()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        using var testService = NewService(theme);

        var c = Sample("#224466");
        theme.ApplyCustomization(c);
        WriteProfile(testService, Sample("#AA3366"));

        await testService.LoadAsync();

        ReferenceEquals(theme.UserSettings, c).Should().BeFalse(
            "the skip must not fire when the loaded content actually changed");
        theme.UserSettings!.AccentColor.Should().Be("#AA3366",
            "the newly loaded, genuinely different customization must actually be applied");

        // Review round 2 (MEDIUM): same background-write leak risk as (b) above.
        var written = await WaitForSidecarAsync(TimeSpan.FromSeconds(5));
        written.Should().BeTrue("a real content change also triggers the sidecar write");
    }

    // ---- (d) sidecar still writes once on the first load after a pre-applied theme ---------

    [Fact]
    public async Task FirstLoadAfterAPreAppliedTheme_StillWritesTheSidecarOnce()
    {
        // Simulates App.ApplyThemeBeforeWindowShown: something applied this exact customization to
        // the theme before DashboardLayoutService ever loaded anything (RemEx-alwfa.1). The theme
        // apply below is skipped as a result — the sidecar write must not be, because this service
        // has never written it this session.
        var theme = new ThemeService { PostToUiThread = action => action() };
        var preApplied = Sample("#5566AA");
        theme.ApplyCustomization(preApplied);

        using var testService = NewService(theme);
        WriteProfile(testService, preApplied with { }); // content-equal, distinct instance

        LastSeedSidecar.TryRead(out _).Should().BeFalse("anti-vacuity: nothing must have written the sidecar yet");

        await testService.LoadAsync();

        var written = await WaitForSidecarAsync(TimeSpan.FromSeconds(5));
        written.Should().BeTrue(
            "the sidecar must be written on the first load of the session even though the theme " +
            "apply itself was skipped as already-requested");

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Seed.Should().Be("#5566AA");
    }
}
