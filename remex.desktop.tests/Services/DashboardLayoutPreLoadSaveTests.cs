using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-71b1m: on a boot where the first-run tutorial flow triggers, something persisted a
/// SCHEMA-0 default customization to <c>dashboard_layout.json</c> before the migrated record from
/// the real <see cref="DashboardLayoutService.ReloadAsync"/> ever landed.
/// </summary>
/// <remarks>
/// <para>
/// THE MECHANISM: <see cref="DashboardLayoutService.CurrentProfile"/> starts life as this class's own
/// constructor default - a bare <c>new DashboardProfile()</c>, unmigrated, SchemaVersion 0 - and stays
/// that way until <see cref="DashboardLayoutService.LoadAsync"/> or
/// <see cref="DashboardLayoutService.ReloadAsync"/> actually completes once. Every read-modify-write
/// save in the app (<c>ShellViewModel.CompleteTutorial</c>/<c>OnIsReducedMotionChanged</c>,
/// <c>CanvasDashboardViewModel.TriggerSave</c>/<c>DismissCoachMark</c>) builds its new profile as
/// <c>CurrentProfile with { ... }</c> and calls <see cref="DashboardLayoutService.RequestSave"/>. Unlike
/// the RemEx-8y3qy clobber (a load that ran and FAILED), nothing before this bead distinguished "no
/// load has happened yet" from "a load just succeeded" - both leave <c>_profileIsFallback</c> false, so
/// a save that races ahead of the very first load sailed straight through and persisted the raw
/// schema-0 default over whatever the user's file already held.
/// </para>
/// <para>
/// THE FIX adds a second, narrower flag - true once a load has completed at all, success or the
/// failure fallback - and <see cref="DashboardLayoutService.RequestSave"/> refuses to queue while it is
/// false: no profile has been loaded means there is nothing real to save through, so refusing is
/// strictly safer than writing the constructor default over a file <see cref="DashboardLayoutService"/>
/// has not yet even looked at.
/// </para>
/// </remarks>
public class DashboardLayoutPreLoadSaveTests : IDisposable
{
    // OWN TEMP DIRECTORY, PER TEST INSTANCE - same reasoning as DashboardLayoutClobberTests: the
    // shared redirected file used by the public constructor is a real cross-test hazard, and this
    // class's whole point is to construct a service that has NEVER called LoadAsync/ReloadAsync.
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "remex-dashboard-layout-preload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private DashboardLayoutService NewService() =>
        new(Path.Combine(_tempDirectory, "dashboard_layout.json"), new ThemeService());

    [Fact]
    public async Task RequestSave_BeforeAnyLoadHasEverRun_IsRefusedAndNothingIsWritten()
    {
        // A freshly constructed service - no LoadAsync, no ReloadAsync. This is exactly the ordering
        // gap the bead is about: something (the coach-mark/tutorial path was the observed one) called
        // RequestSave on a CanvasDashboardViewModel/ShellViewModel-shaped save before the layout
        // service had ever loaded anything.
        using var service = NewService();

        service.HasLoadedOnceForTests.Should().BeFalse(
            "anti-vacuity: this test only means something if no load has actually run yet");
        service.CurrentProfile.Customization.SchemaVersion.Should().Be(0,
            "anti-vacuity: the profile this save would have been based on must actually be the "
            + "unmigrated schema-0 constructor default, not something already stamped");

        // Exactly CanvasDashboardViewModel.TriggerSave / DismissCoachMark / ShellViewModel.CompleteTutorial's
        // shape: read CurrentProfile, change one field, save.
        service.RequestSave(service.CurrentProfile with { HasCompletedTutorial = true });
        await service.FlushAsync();

        File.Exists(service.FilePathForTests).Should().BeFalse(
            "a save with nothing real to persist over must write nothing at all, not a schema-0 "
            + "default that then has to be overwritten by the real load");
        service.CurrentProfile.HasCompletedTutorial.Should().BeTrue(
            "CurrentProfile still updates synchronously in memory, same as ever - only the disk "
            + "write is refused, since the pending real load is about to replace it anyway");
    }

    [Fact]
    public async Task RequestSave_AfterALoadHasRun_SavesThroughTheMigratedProfile()
    {
        // THE CONTROL. The very same save shape, on the very same kind of file (missing on disk, a
        // genuine fresh install) - the only difference is that LoadAsync has actually completed first,
        // exactly as App.InitializeAppAsync always awaits ReloadAsync before anything can reach a
        // ViewModel that could call RequestSave. This must keep working: the guard exists to close the
        // ordering gap, not to block first-run saves in general.
        using var service = NewService();

        var loaded = await service.LoadAsync();
        service.HasLoadedOnceForTests.Should().BeTrue("anti-vacuity: the load must have actually run");
        loaded.Customization.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion,
            "anti-vacuity: a load that ran must stamp the fresh profile at the current schema, not "
            + "leave it at the unmigrated default this test is distinguishing itself from");

        service.RequestSave(service.CurrentProfile with { HasCompletedTutorial = true });
        await service.FlushAsync();

        File.Exists(service.FilePathForTests).Should().BeTrue(
            "once a real load has happened, the same save shape must persist normally");
        var onDisk = JsonSerializer.Deserialize<DashboardProfile>(
            await File.ReadAllTextAsync(service.FilePathForTests), DashboardLayoutService.JsonOptions)!;
        onDisk.HasCompletedTutorial.Should().BeTrue("the field the save meant to change must land");
        onDisk.Customization.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion,
            "the migrated schema stamp from the load must survive the save, not regress to schema 0");
    }
}
