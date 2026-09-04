using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-8y3qy: within seconds of launch, the saved <c>canvasBackgroundType</c> (and other
/// customization fields) reset to their C# defaults on disk. This class reproduces the mechanism
/// and pins the fix.
/// </summary>
/// <remarks>
/// <para>
/// THE MECHANISM: <see cref="DashboardLayoutService.CurrentProfile"/> starts life as a bare
/// <c>new DashboardProfile()</c> (all-default <c>CustomizationSettings</c>, including
/// <c>BackgroundMaterial = "Mica"</c>) and is only replaced once <see cref="DashboardLayoutService.LoadAsync"/>
/// finishes reading the real file. Several view models persist incremental changes with a
/// read-modify-write over exactly that property — <c>var updated = _layoutService.CurrentProfile with
/// { SomeField = x }; _layoutService.RequestSave(updated);</c> (<c>ShellViewModel.OnIsReducedMotionChanged</c>,
/// <c>ShellViewModel.CompleteTutorial</c>, <c>CanvasDashboardViewModel.TriggerSave</c>,
/// <c>CanvasDashboardViewModel.DismissCoachMark</c>). None of them checks whether a load has actually
/// completed.
/// </para>
/// <para>
/// <see cref="DashboardLayoutService.LoadAsync"/>'s own catch block makes this reachable without any
/// caller ordering bug at all: a transient failure to read <c>dashboard_layout.json</c> (a sharing
/// violation from a concurrent reader/writer — the auto-snapshot, a savefile import, or anything else
/// briefly holding the file) is swallowed and silently replaced with an all-default profile, with
/// nothing distinguishing "genuinely missing" from "temporarily unreadable". The very next
/// <c>RequestSave</c> from any of the read-modify-write call sites above — which fire routinely within
/// seconds of launch as telemetry restores sensor cards — persists that default profile over the
/// user's real one. Two seconds later (the debounce) it is on disk, and the file has visibly shrunk
/// because most fields quietly went back to their C# defaults.
/// </para>
/// </remarks>
public class DashboardLayoutClobberTests : IDisposable
{
    // OWN TEMP DIRECTORY, PER TEST INSTANCE, NOT THE SHARED REDIRECTED FILE (RemEx-8y3qy). Every
    // DashboardLayoutService built through the public constructor across the WHOLE test assembly
    // shares one redirected dashboard_layout.json (build/TestHostStateRedirect.cs is per-ASSEMBLY,
    // not per-test) - and this class's whole reason to exist is deliberately locking and racing that
    // file. An undisposed service anywhere else in the assembly, or simply two tests here running
    // back to back, can leave a debounce timer armed that fires a write between another test's write
    // and its read - which is exactly the flake the gate hit on
    // ReadExistingProfileAsync_RetriesThroughATransientSharingViolation while the changed code was
    // unrelated (resx + a tray flyout VM). xUnit constructs a new instance of this class per test
    // method and calls Dispose after it, so a fresh directory here is a fresh directory per test,
    // with no risk of two tests in this class colliding with each other either.
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "remex-dashboard-layout-clobber-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A service pointed at this test's own, private profile file via the internal test seam.</summary>
    private DashboardLayoutService NewService() =>
        new(Path.Combine(_tempDirectory, "dashboard_layout.json"), new ThemeService());

    [Fact]
    public async Task LoadAsyncRoundTripsEveryCustomizationField_WhenSchemaIsAlreadyCurrent()
    {
        using var service = NewService();
        var expected = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);

        await File.WriteAllTextAsync(
            service.FilePathForTests,
            JsonSerializer.Serialize(new DashboardProfile { Customization = expected }, DashboardLayoutService.JsonOptions));

        var loaded = await service.LoadAsync();

        AssertSameCustomization(expected, loaded.Customization,
            "a profile already on the current schema must not be touched by loading it");
    }

    [Fact]
    public async Task LoadAsyncCarriesEveryFieldForward_ThroughAMigrationThatDoesNotTouchThem()
    {
        // Schema 1 -> 2 (StampThemeMode) only reads UseLightPalette/ThemeMode; every other field must
        // survive untouched. Schema 0 is deliberately excluded here: FromPreSeedEngine intentionally
        // rewrites AccentColor/SchemeVariant/UseLightPalette/ThemeContrast from the preset catalogue,
        // which is correct behaviour, not the bug this class is about.
        using var service = NewService();
        var onDisk = BuildNonDefaultSettings(schemaVersion: 1) with { ThemeMode = null };

        await File.WriteAllTextAsync(
            service.FilePathForTests,
            JsonSerializer.Serialize(new DashboardProfile { Customization = onDisk }, DashboardLayoutService.JsonOptions));

        var loaded = await service.LoadAsync();

        AssertSameCustomization(onDisk, loaded.Customization,
            "the schema-1-to-2 migration only stamps ThemeMode; every other field must carry forward",
            nameof(CustomizationSettings.ThemeMode), nameof(CustomizationSettings.SchemaVersion));
    }

    [Fact]
    public async Task ReadExistingProfileAsync_RetriesThroughATransientSharingViolation()
    {
        // Isolates the retry helper itself: a sharing violation that clears while the retry loop is
        // still running must not be reported at all. Deterministic (RemEx-w7ei was a flake from doing
        // this with a fixed Task.Delay release instead): the lock is released from inside
        // onAttemptFailed, at the exact moment the first attempt is known to have failed, so
        // contention is guaranteed and so is the release before the next attempt.
        using var service = NewService();
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);

        await File.WriteAllTextAsync(service.FilePathForTests,
            JsonSerializer.Serialize(new DashboardProfile { Customization = real }, DashboardLayoutService.JsonOptions));

        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        var observedAttempts = new List<int>();

        var profile = await DashboardLayoutService.ReadExistingProfileAsync(service.FilePathForTests, attempt =>
        {
            observedAttempts.Add(attempt);
            if (attempt == 1) block.Dispose();
        });

        observedAttempts.Should().Equal(new[] { 1 },
            "the lock must still have been held for exactly the first attempt - otherwise this test "
            + "proves nothing about the retry, or the release never mattered");
        profile.Should().NotBeNull(
            "a sharing violation that clears before the retries are exhausted must not read as a missing profile");
        AssertSameCustomization(real, profile!.Customization,
            "a transient sharing violation must not lose any field of the profile it eventually reads");
    }

    [Fact]
    public async Task ATransientReadFailureFollowedByAnyCurrentProfileSave_MustNotClobberTheRealFile()
    {
        // THIS IS THE REPRODUCTION FOR RemEx-8y3qy, end to end, through the real LoadAsync path.
        // Deterministic for the same reason as the test above: LoadAsyncForTests plumbs the same
        // attempt-observed callback through to ReadExistingProfileAsync, so the lock is released the
        // instant the first attempt is known to have failed rather than after a guessed delay.
        //
        // Before the fix, ANY read failure - transient or not - made LoadAsync substitute an
        // all-default DashboardProfile as CurrentProfile, and the very next read-modify-write save
        // anywhere in the app (ShellViewModel.CompleteTutorial's shape, reproduced below) persisted
        // that default over the real file.
        using var service = NewService();
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var realProfile = new DashboardProfile { Customization = real, Language = "de-DE" };

        await File.WriteAllTextAsync(service.FilePathForTests,
            JsonSerializer.Serialize(realProfile, DashboardLayoutService.JsonOptions));

        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        var observedAttempts = new List<int>();

        var loaded = await service.LoadAsyncForTests(attempt =>
        {
            observedAttempts.Add(attempt);
            if (attempt == 1) block.Dispose();
        });

        observedAttempts.Should().Equal(new[] { 1 }, "anti-vacuity: the lock must actually have been contended once");
        service.LoadFailureWarning.Should().BeNull(
            "a sharing violation that clears within the retry window must not surface as a load failure");
        AssertSameCustomization(real, loaded.Customization,
            "the profile LoadAsync returns after retrying through a transient lock must match what was on disk");

        // Exactly ShellViewModel.CompleteTutorial's shape: read CurrentProfile, change one field, save.
        var updated = service.CurrentProfile with { HasCompletedTutorial = true };
        service.RequestSave(updated);
        await service.FlushAsync();

        var onDiskAfter = JsonSerializer.Deserialize<DashboardProfile>(
            await File.ReadAllTextAsync(service.FilePathForTests), DashboardLayoutService.JsonOptions)!;

        onDiskAfter.Customization.BackgroundMaterial.Should().Be(real.BackgroundMaterial,
            "a transient read failure must not be able to erase a real saved profile just because "
            + "some unrelated field was changed afterwards");
        onDiskAfter.HasCompletedTutorial.Should().BeTrue("the actual field this save meant to change must still land");
        AssertSameCustomization(real, onDiskAfter.Customization,
            "every customization field the user had saved must survive a transient read failure "
            + "followed by an unrelated field change");
    }

    [Fact]
    public async Task AReadFailureThatOutlivesTheRetries_SuppressesTheNextSaveInsteadOfClobbering()
    {
        // THE RESIDUAL CLOBBER, PINNED. When the lock does NOT clear within the retry window,
        // LoadAsync still has nothing trustworthy to build CurrentProfile from and falls back to
        // defaults exactly as it always has (LoadFailureWarning gets set, unchanged behaviour) - but
        // unlike before the fallback profile must not be usable as the base of a save.
        // RequestSave/SaveInternalAsync now refuse to write while the internal fallback flag is set,
        // so the user loses only whatever they change in THIS session (nothing is written at all)
        // rather than losing the entire saved profile to a silent overwrite.
        using var service = NewService();
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var realProfile = new DashboardProfile { Customization = real, Language = "de-DE" };
        var originalBytes = JsonSerializer.Serialize(realProfile, DashboardLayoutService.JsonOptions);
        await File.WriteAllTextAsync(service.FilePathForTests, originalBytes);

        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        DashboardProfile loaded;
        try
        {
            // Never released during the read - the callback is a no-op, so every attempt sees the
            // same held lock and the read genuinely, permanently fails for this call.
            loaded = await service.LoadAsyncForTests(onReadAttemptFailed: static _ => { });
        }
        finally
        {
            block.Dispose();
        }

        service.LoadFailureWarning.Should().NotBeNull(
            "the lock was held for every attempt, so this load must have genuinely failed");
        loaded.Customization.BackgroundMaterial.Should().Be("Mica",
            "LoadAsync's existing fallback behaviour is unchanged - it still substitutes defaults");

        // Exactly ShellViewModel.CompleteTutorial's shape.
        service.RequestSave(service.CurrentProfile with { HasCompletedTutorial = true });
        await service.FlushAsync();

        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(originalBytes,
            "a save built on a fallback profile must be refused entirely, byte for byte - not partially "
            + "applied and not persisted at all");
    }

    [Fact]
    public async Task SaveAsync_WritesThroughEvenWhileTheLoadedProfileIsAFallback()
    {
        // THE HIGH FINDING FROM ROUND 2's REVIEW. RemexSavefileService.ImportDashboardLayoutAsync
        // calls SaveAsync(profile) then LoadAsync(). Round 2's guard also covered SaveAsync, so a
        // fallback profile in memory silently swallowed the import: SaveAsync no-opped, the following
        // LoadAsync then succeeded (nothing on disk had changed), cleared the flag, and the import
        // reported success while the file was still whatever was there before the import ran. SaveAsync
        // carries an explicit, real profile handed to it by its caller - never one built from
        // CurrentProfile - so it must always write, and it must leave the flag clear afterwards.
        using var service = NewService();
        var stale = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var staleProfile = new DashboardProfile { Customization = stale, Language = "stale" };
        await File.WriteAllTextAsync(service.FilePathForTests,
            JsonSerializer.Serialize(staleProfile, DashboardLayoutService.JsonOptions));

        // Fail a load (lock held for every attempt) so the fallback flag gets set.
        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            await service.LoadAsyncForTests(onReadAttemptFailed: static _ => { });
        }
        finally
        {
            block.Dispose();
        }
        service.LoadFailureWarning.Should().NotBeNull("anti-vacuity: the load must have actually failed");
        service.ProfileIsFallbackForTests.Should().BeTrue(
            "anti-vacuity: the flag must actually be set for this test to mean anything");

        // Exactly RemexSavefileService.ImportDashboardLayoutAsync's shape: an explicit save of a
        // profile that has nothing to do with CurrentProfile, while the fallback flag is set.
        var imported = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var importedProfile = new DashboardProfile { Customization = imported, Language = "imported" };
        var expectedBytes = JsonSerializer.Serialize(importedProfile, DashboardLayoutService.JsonOptions);

        await service.SaveAsync(importedProfile);

        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(expectedBytes,
            "SaveAsync must write the profile it was explicitly given, byte for byte - not be silently "
            + "swallowed because a stale fallback flag happens to be set");
        service.ProfileIsFallbackForTests.Should().BeFalse(
            "an explicit save carries a real profile - it must clear the fallback flag, not leave it set "
            + "for the next caller to trip over");
    }

    [Fact]
    public async Task SaveAsync_SurfacesAMoveFailureToItsCaller()
    {
        // THE HIGH FINDING FROM ROUND 3's RE-REVIEW. SaveInternalAsync used to swallow every
        // exception unconditionally, so a failed explicit save was invisible to its caller.
        // RemexSavefileService.ImportDashboardLayoutAsync awaits SaveAsync, then immediately calls
        // LoadAsync and reports the import applied once LoadAsync returns - if SaveAsync's own
        // failure never surfaces, that LoadAsync succeeds against the file the save never actually
        // reached, and the import reports success while nothing was written. A save that cannot land
        // after every retry must throw, so its caller's existing catch does the right thing.
        using var service = NewService();
        var original = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var originalProfile = new DashboardProfile { Customization = original, Language = "original" };
        var originalBytes = JsonSerializer.Serialize(originalProfile, DashboardLayoutService.JsonOptions);
        await File.WriteAllTextAsync(service.FilePathForTests, originalBytes);

        var attempted = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var attemptedProfile = new DashboardProfile { Customization = attempted, Language = "attempted" };

        // Held for the WHOLE call, unlike the retry tests above - every attempt's File.Move onto this
        // destination must fail with a sharing violation, since a FileShare.None handle grants no
        // delete access and this handle never releases until the save has already given up.
        //
        // UnauthorizedAccessException, NOT IOException - measured, not assumed. File.Move onto a
        // destination held open without FileShare.Delete throws UnauthorizedAccessException on
        // Windows (MoveFileEx maps ERROR_ACCESS_DENIED to it), which is exactly why
        // IsTransientMoveFailure in the production code checks for both types rather than only the
        // one a locked READ throws.
        using (new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Func<Task> act = () => service.SaveAsync(attemptedProfile);
            await act.Should().ThrowAsync<UnauthorizedAccessException>(
                "an explicit save that could not land after every retry must surface that to its "
                + "caller, not report success against a file it never actually reached");
        }

        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(originalBytes,
            "a failed move must never have touched the real file - the write-then-move split exists "
            + "exactly so a failure this far in cannot corrupt it");

        // THE REAL GUARANTEE, NOT JUST THE HAPPY PATH (round 6 finding). A bare "no .tmp remains"
        // assertion here is exactly what flaked once against the gate's own contention (an antivirus
        // scan briefly holding the fresh temp file can outlast even TryDeleteTempFileAsync's own
        // retries). What must actually hold: EITHER the write's own cleanup already removed it, OR
        // the next load's sweep finishes the job - and the real file must be untouched either way.
        var directory = Path.GetDirectoryName(service.FilePathForTests)!;
        var tempGlob = Path.GetFileName(service.FilePathForTests) + ".*.tmp";

        if (Directory.GetFiles(directory, tempGlob).Length > 0)
        {
            await service.LoadAsync();
            Directory.GetFiles(directory, tempGlob).Should().BeEmpty(
                "whatever the failed write's own cleanup could not remove under contention, the next "
                + "load's sweep must - nothing should be left to accumulate forever");
        }

        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(originalBytes,
            "the real file must still be untouched after the sweep, whichever cleanup path actually ran");
    }

    [Fact]
    public async Task SaveAsync_RestoresTheFallbackFlagWhenItsWriteFailsEntirely()
    {
        // THE HIGH FINDING FROM ROUND 4's RE-REVIEW. SaveAsync clears _profileIsFallback and
        // LoadFailureWarning BEFORE writing, and round 4 made a failed write rethrow - but nothing
        // restored the cleared state when that write then failed. Scenario: a startup load fails on a
        // locked-but-intact file (flag true, CurrentProfile fabricated); the user imports a savefile to
        // recover; the import's own SaveAsync also cannot land (a longer-lived lock, a full disk) and
        // throws - correctly - but with the flag left cleared, the very next unrelated RequestSave (the
        // next card drag) would sail past its own guard and persist the still-fabricated CurrentProfile
        // over the real file. A failed recovery attempt must leave exactly the protection it found.
        using var service = NewService();
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var realProfile = new DashboardProfile { Customization = real, Language = "de-DE" };
        var originalBytes = JsonSerializer.Serialize(realProfile, DashboardLayoutService.JsonOptions);
        await File.WriteAllTextAsync(service.FilePathForTests, originalBytes);

        // Put the service into a genuine fallback state (lock held for every retry attempt).
        var loadBlock = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            await service.LoadAsyncForTests(onReadAttemptFailed: static _ => { });
        }
        finally
        {
            loadBlock.Dispose();
        }
        service.LoadFailureWarning.Should().NotBeNull("anti-vacuity: the load must have actually failed");
        service.ProfileIsFallbackForTests.Should().BeTrue(
            "anti-vacuity: the flag must actually be set for this test to mean anything");
        var warningWhileFallback = service.LoadFailureWarning;

        // An explicit save (the import shape) that ALSO cannot land, because the destination is
        // locked for this entire call too.
        var attempted = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var attemptedProfile = new DashboardProfile { Customization = attempted, Language = "attempted" };

        using (new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Func<Task> act = () => service.SaveAsync(attemptedProfile);
            await act.Should().ThrowAsync<UnauthorizedAccessException>(
                "a save that could not land after every retry must still surface, exactly as it does "
                + "when the profile wasn't already a fallback");
        }

        service.ProfileIsFallbackForTests.Should().BeTrue(
            "the flag must be restored, not left cleared, when the explicit save it was cleared for "
            + "never actually landed");
        service.LoadFailureWarning.Should().Be(warningWhileFallback,
            "the warning must be restored too, not left null as if the profile had loaded successfully");

        // With the flag restored, an unrelated read-modify-write save must still be refused.
        service.RequestSave(service.CurrentProfile with { HasCompletedTutorial = true });
        await service.FlushAsync();

        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(originalBytes,
            "the real file must stay byte-identical - a failed recovery attempt must not have quietly "
            + "cleared the way for the next unrelated save to clobber it");
    }

    [Fact]
    public async Task LoadAsync_SweepsStaleTempFilesButLeavesTheRealFileAlone()
    {
        // THE LOW FINDING FROM ROUND 4's RE-REVIEW. The per-call GUID temp name (round 4) means two
        // writers never contend on the same temp file, but it also means a crash between creating one
        // and cleaning it up (or before either runs) leaves it behind forever, since nothing else ever
        // revisits that name. LoadAsync now sweeps them.
        using var service = NewService();
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var realProfile = new DashboardProfile { Customization = real, Language = "de-DE" };
        var originalBytes = JsonSerializer.Serialize(realProfile, DashboardLayoutService.JsonOptions);
        await File.WriteAllTextAsync(service.FilePathForTests, originalBytes);

        var directory = Path.GetDirectoryName(service.FilePathForTests)!;
        var fileName = Path.GetFileName(service.FilePathForTests);
        var stale1 = Path.Combine(directory, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var stale2 = Path.Combine(directory, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(stale1, "leftover from a crash");
        await File.WriteAllTextAsync(stale2, "leftover from a different crash");

        var loaded = await service.LoadAsync();

        File.Exists(stale1).Should().BeFalse(
            "a startup sweep must reap orphaned temp files a crash left behind mid-write");
        File.Exists(stale2).Should().BeFalse("...and every one of them, not just the first");
        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(originalBytes,
            "the sweep must only ever touch the *.tmp glob - the real file must be untouched");
        AssertSameCustomization(real, loaded.Customization, "the load itself must proceed normally alongside the sweep");
    }

    [Fact]
    public async Task AfterASubsequentSuccessfulLoad_TheFallbackFlagClearsAndSavesResume()
    {
        using var service = NewService();
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var realProfile = new DashboardProfile { Customization = real, Language = "de-DE" };
        var originalBytes = JsonSerializer.Serialize(realProfile, DashboardLayoutService.JsonOptions);
        await File.WriteAllTextAsync(service.FilePathForTests, originalBytes);

        // First load genuinely fails (the lock is held for every attempt), which sets the fallback flag.
        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            await service.LoadAsyncForTests(onReadAttemptFailed: static _ => { });
        }
        finally
        {
            block.Dispose();
        }
        service.LoadFailureWarning.Should().NotBeNull("anti-vacuity: the first load must have actually failed");

        // A save attempted while still in the fallback state is refused - proven the same way the
        // test above proves it.
        service.RequestSave(service.CurrentProfile with { HasCompletedTutorial = true });
        await service.FlushAsync();
        (await File.ReadAllTextAsync(service.FilePathForTests)).Should().Be(originalBytes,
            "anti-vacuity: the save while still in fallback must have actually been refused");

        // The lock is gone now, so a fresh load succeeds and must clear the flag.
        var reloaded = await service.LoadAsync();
        service.LoadFailureWarning.Should().BeNull("this load has nothing left to fail on");
        AssertSameCustomization(real, reloaded.Customization, "the second, successful load must read the real profile back");

        // And saving must actually work again.
        service.RequestSave(service.CurrentProfile with { HasCompletedTutorial = true });
        await service.FlushAsync();

        var onDiskAfter = JsonSerializer.Deserialize<DashboardProfile>(
            await File.ReadAllTextAsync(service.FilePathForTests), DashboardLayoutService.JsonOptions)!;
        onDiskAfter.HasCompletedTutorial.Should().BeTrue("once the profile has loaded successfully, saving must resume");
        AssertSameCustomization(real, onDiskAfter.Customization,
            "the profile the user had saved before the failure must still be there once saving resumes");
    }

    [Fact]
    public async Task FlushAsync_WritesAtomically_NoTempFileSurvivesAndContentIsComplete()
    {
        // Not a genuine concurrent-torn-write reproduction (that needs real cross-process timing,
        // which is not a deterministic test) - but the atomic move itself is directly checkable: a
        // successful save must leave nothing behind at the sibling .tmp path, and what lands at the
        // real path must be the complete, valid profile rather than whatever File.WriteAllTextAsync
        // happened to have flushed when a reader looked.
        using var service = NewService();
        await service.LoadAsync(); // establishes a real (non-fallback) baseline; nothing on disk yet

        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        service.RequestSave(service.CurrentProfile with { Customization = real, Language = "atomic-write-test" });
        await service.FlushAsync();

        // The temp name carries a per-call GUID now (RemEx-8y3qy round 4), not the fixed
        // "<file>.tmp" it used to - so the leftover check has to match the pattern rather than one
        // literal path.
        Directory.GetFiles(
                Path.GetDirectoryName(service.FilePathForTests)!,
                Path.GetFileName(service.FilePathForTests) + ".*.tmp")
            .Should().BeEmpty("the atomic write must move the temp file into place rather than leave it behind");

        var onDisk = JsonSerializer.Deserialize<DashboardProfile>(
            await File.ReadAllTextAsync(service.FilePathForTests), DashboardLayoutService.JsonOptions)!;
        onDisk.Language.Should().Be("atomic-write-test");
        AssertSameCustomization(real, onDisk.Customization, "the atomically-written file must be complete, not truncated");
    }

    /// <summary>
    /// Builds a <see cref="CustomizationSettings"/> where every persisted, non-alias property holds a
    /// value different from the record's own default — via reflection, so a field added next year is
    /// covered without anyone remembering to update this list.
    /// </summary>
    internal static CustomizationSettings BuildNonDefaultSettings(int schemaVersion)
    {
        var defaults = new CustomizationSettings();
        object settings = defaults;

        foreach (var prop in PersistedProperties())
        {
            if (prop.Name == nameof(CustomizationSettings.SchemaVersion))
            {
                prop.SetValue(settings, schemaVersion);
                continue;
            }

            var current = prop.GetValue(defaults);
            object replacement = prop.PropertyType switch
            {
                var t when t == typeof(string) => "nondefault-" + prop.Name,
                var t when t == typeof(double) => 12.5,
                var t when t == typeof(bool) => !(bool)current!,
                var t when t == typeof(bool?) => current is null ? true : !(bool)current,
                var t when t == typeof(IReadOnlyList<string>) => new List<string> { "#112233", "#445566" },
                _ => throw new NotSupportedException(
                    $"CustomizationSettings.{prop.Name} has an unhandled type {prop.PropertyType} - "
                    + "extend this builder rather than skipping the field silently."),
            };

            prop.SetValue(settings, replacement);
        }

        return (CustomizationSettings)settings;
    }

    private static PropertyInfo[] PersistedProperties() =>
        typeof(CustomizationSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToArray();

    private static void AssertSameCustomization(
        CustomizationSettings expected, CustomizationSettings actual, string because, params string[] except)
    {
        foreach (var prop in PersistedProperties())
        {
            if (except.Contains(prop.Name)) continue;

            var expectedValue = prop.GetValue(expected);
            var actualValue = prop.GetValue(actual);

            if (expectedValue is IReadOnlyList<string> expectedList && actualValue is IReadOnlyList<string> actualList)
            {
                actualList.Should().Equal(expectedList, because + $" (property: {prop.Name})");
            }
            else
            {
                actualValue.Should().Be(expectedValue, because + $" (property: {prop.Name})");
            }
        }
    }
}
