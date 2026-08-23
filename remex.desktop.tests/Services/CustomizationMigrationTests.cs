using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Migrating a profile written before the seed engine (RemEx-dbkzy).
/// </summary>
/// <remarks>
/// The bead's acceptance in two sentences: a settings file naming Cyber-NOC opens on the
/// Cyber-NOC-equivalent seed, and a corrupted value opens on the default without a crash. Both are
/// pinned below, along with the half nobody asks for until it breaks — that migrating a profile
/// twice is the same as migrating it once.
/// </remarks>
public class CustomizationMigrationTests
{
    /// <summary>
    /// A profile exactly as 2.4.0 wrote it: a theme NAME, the seed its preset arm saved, and none
    /// of the keys that did not exist yet.
    /// </summary>
    /// <remarks>
    /// BUILT FROM JSON RATHER THAN FROM THE RECORD, on purpose. Constructing a
    /// <c>CustomizationSettings</c> in C# cannot express "this key was absent" — every field gets
    /// its default whether or not 2.4 would have written one — and absence is the entire input this
    /// migration reads.
    /// <para>
    /// CAMELCASE, AND THROUGH THE SERVICE'S OWN OPTIONS. The first version of this fixture wrote
    /// PascalCase keys and deserialised with the DEFAULT options, which agree with the property
    /// names by coincidence — so it passed while testing a shape no real file has. The app reads
    /// with <c>PropertyNamingPolicy = CamelCase</c>, and the one serialization contract this bead
    /// introduces (<c>schemaVersion</c>) is exactly the one a fixture on its own options cannot pin:
    /// rename the JSON property and every test here still passes while every profile on disk
    /// deserialises to 0 and re-migrates forever.
    /// </para>
    /// <para>
    /// Verified against <c>git show v2.4.0:remex.core/Models/DashboardProfile.cs</c>: only
    /// <c>useLightPalette</c> and <c>schemaVersion</c> are genuinely new. <c>themeContrast</c>,
    /// <c>themeSeedChroma</c> and <c>customAccentColors</c> all existed in 2.4 and are therefore
    /// omitted here rather than assumed absent — an earlier revision of this comment claimed
    /// <c>themeContrast</c> was not yet invented, which was wrong and was caught in review.
    /// </para>
    /// </remarks>
    private static CustomizationSettings LegacyProfile(string themeId, string accent, string variant = "TonalSpot")
    {
        var json = $$"""
        {
          "baseTheme": "{{themeId}}",
          "accentColor": "{{accent}}",
          "schemeVariant": "{{variant}}",
          "cornerRadius": 16,
          "glowStrength": 2
        }
        """;

        var settings = JsonSerializer.Deserialize<CustomizationSettings>(json, DashboardLayoutService.JsonOptions);
        settings.Should().NotBeNull();

        // ANTI-VACUITY, and it is the reason to route through the real options at all: if the keys
        // above stopped binding, every field would silently be its default and the fixture would
        // still "work" - a legacy profile is mostly defaults.
        settings!.AccentColor.Should().Be(accent, "the fixture must actually bind, or it tests nothing");
        settings.ThemeId.Should().Be(themeId);
        settings.SchemaVersion.Should().Be(0, "an absent schemaVersion is what marks a legacy profile");
        settings.UseLightPalette.Should().BeNull("2.4 had no such key, and null is what the migration reads");
        return settings;
    }

    [Fact]
    public void TheVersionStampBindsFromRealCamelCaseJson()
    {
        // THE ONE SERIALIZATION CONTRACT THIS BEAD ADDS. Everything else here is a pure function on
        // a record; this is the round trip that decides whether any of it ever runs on a real file.
        var current = JsonSerializer.Deserialize<CustomizationSettings>(
            $$"""{"schemaVersion": {{CustomizationMigration.CurrentSchemaVersion}}}""",
            DashboardLayoutService.JsonOptions);

        current!.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion);

        var written = JsonSerializer.Serialize(
            new CustomizationSettings { SchemaVersion = CustomizationMigration.CurrentSchemaVersion },
            DashboardLayoutService.JsonOptions);

        written.Should().Contain("\"schemaVersion\"",
            "a renamed key reads back as 0 on every existing profile, and re-migrates it forever");
    }

    // ── The acceptance criteria ──────────────────────────────────────────────────────────────

    [Fact]
    public void ACyberNocProfileOpensOnTheCyberNocSeedAndScheme()
    {
        var migrated = CustomizationMigration.Migrate(LegacyProfile("CyberNOC", "#00F3FF"), out var warning);

        warning.Should().BeNull("nothing about this profile needed repairing");

        var preset = SeedPresetCatalog.Resolve("CyberNOC");
        migrated.AccentColor.Should().Be(preset.Seed);
        migrated.SchemeVariant.Should().Be(preset.SchemeVariant,
            "2.4 never wrote a variant, so the profile carries the record default and the preset's "
            + "choice is the one that reproduces the retired dictionary");
        migrated.UseLightPalette.Should().Be(preset.IsLight,
            "the mode stops being inferred from the theme's NAME the moment it is written down");
        migrated.ThemeId.Should().Be("CyberNOC", "the id is the persistence key and is never rewritten");
        migrated.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion);
    }

    [Fact]
    public void ACorruptedAccentOpensOnAUsableSeedRatherThanCrashing()
    {
        // '#FF0O00' - a capital O for a zero. Seven characters, so 2.4's length-only validation
        // accepted it, and it survives a restart to this day.
        var migrated = CustomizationMigration.Migrate(LegacyProfile("Monolith", "#FF0O00"), out var warning);

        warning.Should().NotBeNull("a repaired value is the one occurrence worth logging");
        warning.Should().Contain("#FF0O00", "the log has to name the value that was thrown away");

        CustomizationMigration.IsUsableSeed(migrated.AccentColor).Should().BeTrue();
        migrated.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion);
    }

    [Fact]
    public void AGarbageThemeNameOpensOnTheDefaultPresetWithoutThrowing()
    {
        var migrated = CustomizationMigration.Migrate(LegacyProfile("NoSuchTheme", "#00F3FF"), out _);

        // The id itself is left alone - rewriting it would destroy the evidence of what the profile
        // actually said - but everything derived from it comes off the default preset.
        migrated.ThemeId.Should().Be("NoSuchTheme");
        migrated.SchemeVariant.Should().Be(SeedPresetCatalog.Default.SchemeVariant);
        migrated.UseLightPalette.Should().Be(SeedPresetCatalog.Default.IsLight);
    }

    // ── What must NOT be overwritten ─────────────────────────────────────────────────────────

    [Fact]
    public void ASeedTheUserActuallyPickedSurvivesTheMigration()
    {
        // THE FAILURE IN THE OTHER DIRECTION, and the more insulting one: a migration that "fixes"
        // a colour the user chose on purpose is an upgrade that changed their app.
        var migrated = CustomizationMigration.Migrate(LegacyProfile("CyberNOC", "#22C55E"), out var warning);

        warning.Should().BeNull();
        migrated.AccentColor.Should().Be("#22C55E");
    }

    [Fact]
    public void AVariantAndContrastTheUserSetSurviveTheMigration()
    {
        var chosen = LegacyProfile("CyberNOC", "#22C55E", variant: "Rainbow") with { ThemeContrast = 0.6 };

        var migrated = CustomizationMigration.Migrate(chosen, out _);

        migrated.SchemeVariant.Should().Be("Rainbow");
        migrated.ThemeContrast.Should().Be(0.6);
    }

    [Fact]
    public void APreTwoFourProfileWithNoSavedSeedAdoptsItsPresets()
    {
        // Before the preset arms started saving a seed, a Cyber-NOC profile carried the record's
        // default violet - the palette came from the dictionary, not from this field. Read
        // literally that user is handed violet on an upgrade.
        var defaultAccent = new CustomizationSettings().AccentColor;

        var migrated = CustomizationMigration.Migrate(LegacyProfile("CyberNOC", defaultAccent), out _);

        migrated.AccentColor.Should().Be(SeedPresetCatalog.Resolve("CyberNOC").Seed);
        migrated.AccentColor.Should().NotBe(defaultAccent);
    }

    [Fact]
    public void ADefaultVIOLETTheUserActuallyPickedIsKept_BecauseTheSwatchListSaysSo()
    {
        // THE SIGNAL I CLAIMED DID NOT EXIST (review finding). CustomAccentColors is the colour
        // picker's saved-swatch list, it shipped in 2.4, and it is written only when someone picks
        // a colour - so the default violet APPEARING in it is evidence it was chosen rather than
        // defaulted. Without this the Monolith user below is handed Monolith's blue on upgrade,
        // which is the exact outcome the bead forbids.
        var defaultAccent = new CustomizationSettings().AccentColor;
        var chosen = LegacyProfile("Monolith", defaultAccent) with
        {
            CustomAccentColors = new[] { "#123456", defaultAccent },
        };

        CustomizationMigration.Migrate(chosen, out _).AccentColor.Should().Be(defaultAccent);
    }

    [Fact]
    public void ADefaultVioletWithAnEmptySwatchListStillAdoptsThePreset()
    {
        // THE OTHER HALF, and the reason the signal is a strict improvement rather than a trade: a
        // profile that never opened the picker has an empty list and behaves exactly as before.
        var defaultAccent = new CustomizationSettings().AccentColor;
        var untouched = LegacyProfile("Monolith", defaultAccent);

        untouched.CustomAccentColors.Should().BeEmpty("anti-vacuity for the test above");
        CustomizationMigration.Migrate(untouched, out _).AccentColor
            .Should().Be(SeedPresetCatalog.Resolve("Monolith").Seed);
    }

    [Fact]
    public void DynamicKeepsEverythingBecauseItPinsNothing()
    {
        var built = LegacyProfile("Dynamic", "#22C55E", variant: "Expressive");

        var migrated = CustomizationMigration.Migrate(built, out _);

        migrated.AccentColor.Should().Be("#22C55E");
        migrated.SchemeVariant.Should().Be("Expressive");
    }

    // ── Running it more than once ────────────────────────────────────────────────────────────

    [Fact]
    public void MigratingTwiceIsTheSameAsMigratingOnce()
    {
        // THE BUG THIS PREVENTS IS SILENT AND PERMANENT. If a save forgets the version stamp, every
        // launch re-runs the legacy arm - and the legacy arm adopts the PRESET's variant and
        // contrast wherever the profile carries a default, so a user who deliberately returns a
        // slider to 0.0 has it taken away again on the next start, forever.
        var once = CustomizationMigration.Migrate(LegacyProfile("CyberNOC", "#00F3FF"), out _);
        var twice = CustomizationMigration.Migrate(once, out _);

        twice.Should().BeEquivalentTo(once);
    }

    [Fact]
    public void AnAlreadyCurrentProfileIsReturnedUntouched()
    {
        var current = new CustomizationSettings
        {
            SchemaVersion = CustomizationMigration.CurrentSchemaVersion,
            ThemeId = "CyberNOC",
            AccentColor = "#22C55E",
            SchemeVariant = "Rainbow",
        };

        CustomizationMigration.Migrate(current, out var warning).Should().BeSameAs(current);
        warning.Should().BeNull();
    }

    [Fact]
    public void AProfileFromANewerBuildIsNotRewrittenBackwards()
    {
        // Reachable by running an older build over a newer one's settings file. Stamping our
        // version onto it would erase the newer build's record of what it had already migrated.
        var future = new CustomizationSettings { SchemaVersion = CustomizationMigration.CurrentSchemaVersion + 7 };

        var result = CustomizationMigration.Migrate(future, out _);

        result.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion + 7);
    }

    [Fact]
    public void ANullRecordYieldsAUsableCurrentOne()
    {
        var migrated = CustomizationMigration.Migrate(null, out var warning);

        migrated.Should().NotBeNull();
        migrated.SchemaVersion.Should().Be(CustomizationMigration.CurrentSchemaVersion);
        warning.Should().BeNull();
    }

    // ── The usability screen ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#FF0O00")]    // capital O for a zero - seven characters, so 2.4 saved it
    [InlineData("not a colour")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnusableSeedIsRejected(string? hex) =>
        CustomizationMigration.IsUsableSeed(hex).Should().BeFalse();

    [Fact]
    public void BlackAndFullyTRANSPARENTBlackAreBothUsable_WhichIsNotWhatIExpected()
    {
        // MEASURED, NOT ASSUMED, AND THE ASSUMPTION WAS WRONG. This was written as a rejection case
        // on the reasoning that #00000000 parses, that the generator reads only the RGB channels,
        // and that black in dark mode therefore gives a surface and a text colour that are the same
        // colour. The first two are true and the conclusion is not: a black seed has no chroma, so
        // M3 builds a NEUTRAL tonal ramp and still separates surface (~tone 6) from on-surface
        // (~tone 90). The generator is more robust here than the migration needed it to be.
        //
        // Pinned because it is the fact the screen below rests on. If a generator change ever made
        // an achromatic seed collapse, this test says so directly instead of the migration quietly
        // starting to rewrite people's black themes.
        CustomizationMigration.IsUsableSeed("#000000").Should().BeTrue();
        CustomizationMigration.IsUsableSeed("#00000000").Should().BeTrue();
        CustomizationMigration.IsUsableSeed("#FFFFFF").Should().BeTrue();
    }

    [Fact]
    public void NoParseableSeedIsRejectedToday_SoTheScreenIsAGuardOnTheGenerator()
    {
        // THE HONEST STATEMENT OF WHAT IsUsableSeed IS. A 216-point sweep of the RGB cube rejects
        // nothing: M3 guarantees a readable surface/on-surface pair for every seed it is given, in
        // both modes. So the contrast half of the screen does not filter real input - it is a
        // regression guard on that guarantee, and saying so here stops the next reader believing
        // it is load-bearing on the migration path.
        //
        // It earns its place because of what it protects: the legacy arm WRITES a seed into
        // people's profiles, so a generator that stopped guaranteeing this would have the migration
        // stamping unreadable palettes onto users who never chose one.
        var rejected = (from r in Enumerable.Range(0, 6)
                        from g in Enumerable.Range(0, 6)
                        from b in Enumerable.Range(0, 6)
                        let hex = $"#{r * 51:X2}{g * 51:X2}{b * 51:X2}"
                        where !CustomizationMigration.IsUsableSeed(hex)
                        select hex).ToArray();

        rejected.Should().BeEmpty("M3 guarantees a readable surface pair for every seed");
    }

    [Fact]
    public void EveryPresetSeedInTheCatalogIsUsable()
    {
        // A future preset whose seed generates a surface its own text cannot be read against would
        // be migrated ONTO people by the legacy arm. This is the only place that asks.
        foreach (var preset in SeedPresetCatalog.All.Where(p => p.Seed is not null))
        {
            CustomizationMigration.IsUsableSeed(preset.Seed)
                .Should().BeTrue($"{preset.Id}'s seed is written into profiles by the migration");
        }

        CustomizationMigration.IsUsableSeed(CustomizationMigration.FallbackSeed)
            .Should().BeTrue("the last-resort seed is the one that must never itself be unusable");
    }

    // ── That any of this actually runs ───────────────────────────────────────────────────────

    /// <summary>
    /// The load path migrates, and migrates BEFORE it applies.
    /// </summary>
    /// <remarks>
    /// EVERY TEST ABOVE IS VACUOUS IF NOTHING CALLS Migrate. That is not hypothetical — a pure
    /// function with a thorough suite and no caller passes every assertion it makes while the app
    /// behaves exactly as it did before. <c>LoadAsync</c> is the only caller, it posts through the
    /// Avalonia dispatcher, and this repo has no headless render, so this reads the source: the
    /// same approach <c>ThemeDictionary.AssertSelectThemeReadsTheCatalog</c> and the file-transfer
    /// virtualization guards take, and for the same reason.
    /// <para>
    /// ORDER IS ASSERTED, NOT JUST PRESENCE. Migrating after the apply would still satisfy a
    /// contains-check while painting the unmigrated palette on the first frame and correcting it
    /// only once something happened to trigger a repaint — a flash of the wrong theme on every
    /// upgrade launch, which is precisely the experience this bead exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public void MigrateProfileReportsWhetherItActuallyChangedAnything()
    {
        // The signal the write-back is guarded on. If it were always true the app would write the
        // profile to disk on every single launch; if it were always false the migration would never
        // be persisted and would re-derive itself forever, which is the defect it was added for.
        var legacy = new DashboardProfile { Customization = LegacyProfile("CyberNOC", "#00F3FF") };

        DashboardLayoutService.MigrateProfile(legacy, out var legacyOutcome);
        legacyOutcome.Changed.Should().BeTrue("a schema-0 profile is rewritten");

        var current = new DashboardProfile
        {
            Customization = CustomizationMigration.Migrate(legacy.Customization, out _),
        };

        var unchanged = DashboardLayoutService.MigrateProfile(current, out var currentOutcome);
        currentOutcome.Changed.Should().BeFalse("an already-current profile is left alone");
        unchanged.Should().BeSameAs(current, "and is not even copied");
    }

    [Fact]
    public void TheLoadPathPersistsAMigrationRatherThanRederivingItEveryLaunch()
    {
        // Behavioural coverage stops at MigrateProfile above: LoadAsync needs a ThemeService, which
        // posts to a dispatcher there is no app for in this suite. So the WIRING is read from
        // source, the same way the apply-order guard is, and for the same reason.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Services", "DashboardLayoutService.cs"));

        var body = Regex.Match(source, @"public async Task<DashboardProfile> LoadAsync\(\).*?\n    \}",
            RegexOptions.Singleline);
        body.Success.Should().BeTrue("LoadAsync moved or changed shape - this guard cannot see it");

        body.Value.Should().MatchRegex(@"if \(outcome\.Changed[^)]*\)\s*RequestSave\(",
            "a migration nobody writes back is a re-derivation: the repaired seed stays corrupt on "
            + "disk, the warning logs every launch, and the user's values follow whatever the preset "
            + "catalogue says today rather than what it said when they upgraded");

        // AND NOT ON A FRESH INSTALL. A default profile is always schema 0, so it always reports
        // Changed - writing it would raise ProfileSaved on first launch, which arms the savefile
        // service's snapshot debounce, which autosnapshots the empty default and prunes a real
        // backup from the previous install out of the rolling five (review finding).
        body.Value.Should().MatchRegex(@"if \(outcome\.Changed && !ProfileFileMissingOnLoad\)",
            "a brand-new profile has nothing worth persisting and a write costs a stale backup");
    }

    /// <summary>
    /// Anything that hands a persisted record to <c>ApplyCustomization</c> migrated it first, in the
    /// same method.
    /// </summary>
    /// <remarks>
    /// THIS GUARD HAS NOW BEEN WRONG TWICE, IN OPPOSITE DIRECTIONS, AND BOTH WAYS ARE WORTH KEEPING
    /// WRITTEN DOWN.
    /// <list type="number">
    /// <item>V1 scanned only <c>LoadAsync</c> and asserted an occurrence COUNT. It passed while the
    /// real defect sat in <c>App.axaml.cs</c>, which had its own deserialize-and-apply for the
    /// pre-window paint and never migrated at all. A guard scoped to the file you were thinking
    /// about cannot see the file you were not.</item>
    /// <item>V2 fixed the scope and broke the scope. It searched <c>source[..apply]</c> — the whole
    /// file before the call — so the DECLARATIONS of <c>MigrateProfile(</c> and
    /// <c>ReadAndMigrate(</c>, which sit near the top of <c>DashboardLayoutService.cs</c>, satisfied
    /// it for every apply in that file forever. Emptying <c>LoadAsync</c>'s migration entirely left
    /// it green. A guard that cannot fail for the reason its name gives is worse than a deleted
    /// one, because it reads as coverage.</item>
    /// </list>
    /// So the scope is now the ENCLOSING METHOD, found by brace matching rather than by a regex
    /// window — that distinction cost three vacuous tests on RemEx-5u0vy and is the same mistake.
    /// The anti-vacuity assertions at the end are what would have caught V2: they demand that the
    /// scan actually located bodies and applies, in a known quantity.
    /// </remarks>
    [Fact]
    public void EveryThemeApplyInTheAppIsPrecededByAMigrationInTheSameMethod()
    {
        var offenders = new List<string>();
        var examined = 0;

        foreach (var relative in new[]
                 {
                     Path.Combine("remex.desktop", "Services", "DashboardLayoutService.cs"),
                     Path.Combine("remex.desktop", "App.axaml.cs"),
                 })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), relative));

            var applies = Regex.Matches(source, @"ApplyCustomization\(").Select(m => m.Index).ToArray();
            applies.Should().NotBeEmpty(
                $"anti-vacuity: {relative} is listed here because it applies a persisted theme");

            foreach (var apply in applies)
            {
                var body = EnclosingBody(source, apply);
                body.Should().NotBeNull($"{relative}: no enclosing method body found around offset {apply}");
                examined++;

                // MigrateProfile / ReadAndMigrate / CustomizationMigration.Migrate all count — the
                // property is that a migration happened, not which spelling was used.
                //
                // NO LOOKBEHIND EXCLUDING DECLARATIONS, because the body scoping already does that
                // and does it properly: a C# method declaration cannot appear inside another
                // method's body. The first attempt at this line DID carry one — `(?<=[=(,]\s*)` —
                // and it silently failed to match the one fully-qualified call in App.axaml.cs,
                // where the character before the name is a '.'. Two mechanisms for one job, and the
                // redundant one was the broken one.
                var before = body!.Value.Text[..(apply - body.Value.Start)];
                var migrated = Regex.IsMatch(
                    before, @"\b(CustomizationMigration\.Migrate|MigrateProfile|ReadAndMigrate)\(");

                if (!migrated)
                    offenders.Add($"{relative}: an ApplyCustomization at offset {apply} with no migration before it in the same method");
            }
        }

        offenders.Should().BeEmpty(
            "a record applied before it is migrated paints the theme the migration exists to replace, "
            + "and on the pre-window path that decides which palette the window OPENS on");

        // ANTI-VACUITY, and the half that would have caught the version of this guard that could not
        // fail: three apply sites exist today — LoadAsync's success path, LoadAsync's corrupt-file
        // path, and App.ApplyThemeBeforeWindowShown. Fewer means the scan stopped finding them.
        examined.Should().BeGreaterOrEqualTo(3, "the scan must actually reach every apply site");
    }

    /// <summary>The brace-matched body of the method containing <paramref name="index"/>.</summary>
    /// <remarks>
    /// A REGEX WINDOW IS NOT A SCOPE (RemEx-5u0vy, where a fixed 1200-character "method body" quietly
    /// swallowed the two handlers below an empty one). Walking braces outward is the only way to get
    /// the real extent, and it is about ten lines.
    /// </remarks>
    private static (int Start, string Text)? EnclosingBody(string source, int index)
    {
        var depth = 0;
        for (var i = index; i >= 0; i--)
        {
            if (source[i] == '}') depth++;
            else if (source[i] == '{')
            {
                if (depth == 0)
                {
                    // Found the opening brace of the innermost block containing index. Walk forward
                    // to its match so the body has a real end as well as a real start.
                    var close = MatchingClose(source, i);
                    return close < 0 ? null : (i, source[i..close]);
                }

                depth--;
            }
        }

        return null;
    }

    private static int MatchingClose(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    [Fact]
    public void TheStampedVersionIsWhatTheRecordDefaultIsNot()
    {
        // If these ever coincide the whole mechanism silently stops working: every fresh record
        // would look migrated and no legacy profile would ever be repaired.
        new CustomizationSettings().SchemaVersion
            .Should().NotBe(CustomizationMigration.CurrentSchemaVersion,
                "the record's default is the marker for 'never migrated'");
    }


    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
