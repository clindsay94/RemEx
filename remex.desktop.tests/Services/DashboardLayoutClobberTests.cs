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
public class DashboardLayoutClobberTests
{
    [Fact]
    public async Task LoadAsyncRoundTripsEveryCustomizationField_WhenSchemaIsAlreadyCurrent()
    {
        using var service = new DashboardLayoutService(new ThemeService());
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
        using var service = new DashboardLayoutService(new ThemeService());
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
        // still running must not be reported at all.
        using var service = new DashboardLayoutService(new ThemeService());
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);

        await File.WriteAllTextAsync(service.FilePathForTests,
            JsonSerializer.Serialize(new DashboardProfile { Customization = real }, DashboardLayoutService.JsonOptions));

        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);

        // Released well inside the retry window (three attempts, 75ms apart - see
        // DashboardLayoutService.TransientReadRetryDelayMs) so the first attempt is the one that hits
        // the violation and a later one is the one that succeeds, rather than the lock never actually
        // being contended.
        var releaseShortly = Task.Delay(20).ContinueWith(_ => block.Dispose());

        var profile = await DashboardLayoutService.ReadExistingProfileAsync(service.FilePathForTests);
        await releaseShortly;

        profile.Should().NotBeNull("a sharing violation that clears quickly must not read as a missing profile");
        AssertSameCustomization(real, profile!.Customization,
            "a transient sharing violation must not lose any field of the profile it eventually reads");
    }

    [Fact]
    public async Task ATransientReadFailureFollowedByAnyCurrentProfileSave_MustNotClobberTheRealFile()
    {
        // THIS IS THE REPRODUCTION FOR RemEx-8y3qy, end to end. A real, fully-populated profile is on
        // disk. The file is made transiently unreadable (a sharing violation, exactly like a
        // concurrent reader/writer would cause) for a few milliseconds — short enough that
        // ReadExistingProfileAsync's retry clears it, which is the ordinary case for the sharing
        // violations this guards (the auto-snapshot, a savefile import, antivirus). Before the fix,
        // ANY read failure — transient or not — made LoadAsync substitute an all-default
        // DashboardProfile as CurrentProfile, and the very next read-modify-write save anywhere in the
        // app (ShellViewModel.CompleteTutorial's shape, reproduced below) persisted that default over
        // the real file.
        using var service = new DashboardLayoutService(new ThemeService());
        var real = BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);
        var realProfile = new DashboardProfile { Customization = real, Language = "de-DE" };

        await File.WriteAllTextAsync(service.FilePathForTests,
            JsonSerializer.Serialize(realProfile, DashboardLayoutService.JsonOptions));

        var block = new FileStream(service.FilePathForTests, FileMode.Open, FileAccess.Read, FileShare.None);
        var releaseShortly = Task.Delay(20).ContinueWith(_ => block.Dispose());

        var loaded = await service.LoadAsync();
        await releaseShortly;

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
