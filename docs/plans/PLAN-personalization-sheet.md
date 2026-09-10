# Personalization Sheet Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the PC Personalization sheet around one colour path (source, then shape, then
strategy), add the Aurora and real-Wallpaper background modes, retire Mica, persist saved palettes,
and flip the splash default to Cosmic Zoom.

**Architecture:** The profile record in `remex.core` gains the new fields and a schema-3 migration
arm; the colour engine in `remex.desktop` maps Android's seven strategy names onto the
MaterialColorUtilities styles (Monochrome is built by zeroing every tonal palette's chroma, because
the 0.3.0 package has no `Monochrome` style); a small always-alive coordinator follows the Windows
accent through a fake-clock-testable poller; the background control gains an Aurora panel painted
from three new seed-derived resources and a Wallpaper panel that draws a cached bitmap behind a
surface veil; the view is reflowed into six cards pinned by source-text guards. Every task ends
with a building, running app.

**Tech Stack:** .NET 10, Avalonia 12.1.1, Material.Avalonia 3.19.0, MaterialColorUtilities 0.3.0
(`CorePalette.Fill(uint, Style)`, `TonalPalette.FromHueAndChroma`), CommunityToolkit.Mvvm
(`[ObservableProperty]`, `[RelayCommand]`), SkiaSharp, xUnit 2.9.3 + FluentAssertions 7.0.0.

**Spec:** `docs/SPEC-personalization-sheet.md` (approved 2026-09-04). Read it alongside this plan.

**Beads served:** RemEx-ddynd (Aurora and Wallpaper modes), RemEx-z94c7 (Mica), plus the sheet
reflow. Commit messages below use the placeholder `(RemEx-<bead>)`; the id is assigned when a task
becomes a bead.

## Global Constraints

Every task's requirements implicitly include this section.

- **Never use `ConfigureAwait(false)` anywhere** (`AGENTS.md:268-274`; `ConfigureAwaitBanTests` enforces it).
- **`remex.core` is NativeAOT-constrained**: no reflection, no `JsonSerializer` without the
  source-generated `RemexJsonSerializerContext`, `init` properties, camelCase JSON names like the
  neighbours (`AGENTS.md:283-294`).
- **Every user-visible string lives in all nine resx files**: `remex.desktop/Localization/Strings.resx`
  plus `.es`, `.fr`, `.hi`, `.id`, `.pl`, `.pt-BR`, `.tr`, `.uk`. `scripts/check-localization.ps1`
  must report zero errors and no new warnings. "Orphaned translation" (a key removed from English
  but left in a locale) is an **error**; "identical to English" is a **warning** — so every new
  value below differs from its English text, and every removal hits all nine files.
- **Bulk-edit resx files with a Python script and explicit UTF-8, never PowerShell string
  interpolation** (`AGENTS.md:324-329`). Task 2 adds `scripts/resx_add_keys.py`; Task 10 adds
  `scripts/resx_remove_keys.py`. Every later task reuses them. Run Python as `uv run python`.
- **Never bump any version number** (.NET `<Version>`, Android versionCode/versionName).
- **Never `git checkout -- .`** or `git checkout -- <file>` to undo anything (`AGENTS.md:109-118`).
- **Never construct a `git add` path from memory** — copy the exact case from `git status`.
- **Build:** `dotnet build Remex.sln -c Release --nologo` must finish with 0 warnings. **Build one
  project per `dotnet build` invocation** when building projects individually.
- **Read the test TOTAL, not just the colour.** A green run whose count did not rise did not run
  your new tests.
- **The commit gate is `pwsh scripts/verify.ps1`** (force-clean, rebuild, full suite, edit guard,
  translations). Run it before every commit below; `pwsh scripts/verify.ps1 -Check` must say VALID.
  The targeted `dotnet test --filter` commands in each task are the fast inner loop, not the gate.
- **Commit format:** conventional prefix + bead id, e.g. `feat(desktop): … (RemEx-<bead>)`.
- **`docs/CHANGELOG.md` lines are written by the gate/landing step, not by these tasks.** No task
  below edits the changelog; the board-drain landing writes the entry from the commit.
- **Profile file rules (RemEx-8y3qy, `DashboardLayoutService.cs`):** saves are atomic; a fallback
  profile is never persisted (`RequestSave` refuses while the profile is a fallback); the migration
  uses `with` expressions so no field is dropped; `DashboardLayoutClobberTests.BuildNonDefaultSettings`
  (`remex.desktop.tests/Services/DashboardLayoutClobberTests.cs:507-536`) enumerates every
  `CustomizationSettings` property by reflection and throws on an unhandled type — every new
  field must get a non-default value there.
- **`CustomizationSettingsRoundTripTests.ApplyAndSaveAssignsEveryPersistedCustomizationField`**
  (`remex.desktop.tests/ViewModels/CustomizationSettingsRoundTripTests.cs:40-52`) scans the
  `new CustomizationSettings { … }` initializer in `CustomizationViewModel.ApplyAndSave`
  (`:905-943`) for every persisted property name. Every new field must be assigned there in the
  same task that adds it.
- **Eyes passes** (looking at the live window) use only `pwsh scripts/ui-hotreload.ps1 -Start
  [-AppArgs '--view <Name>']`, `pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree`, and
  `pwsh scripts/ui-hotreload.ps1 -Stop`. **No injected keystrokes.** Task 8 adds a `Personalize`
  `--view` name so the sheet can be opened without one.
- **Read `docs/REGRESSION-GUARDS.md` "Desktop shell"** (`:492-560`) before touching
  `DashboardBackgroundControl.axaml` or any `Transitions`/`Style.Animations`: an unset
  Material.Avalonia template property is not neutral; `RenderTransform` is not keyframe-animatable;
  a palette-transition suppressor must carry an activator and be declared after the crossfade.
- Line numbers cited below were verified on 2026-09-04 at commit `8f80a18` on `v2.5-board-drain`.
  Re-verify with `grep -n` before editing; they drift as earlier tasks land.

## Execution notes

- **Localisation rides with the task that introduces the string.** `LocalizationKeyReferenceTests`
  and check-localization's "referenced but undefined" axis fail on a key referenced anywhere in
  code/XAML but absent from `Strings.resx`, so a single late localisation task cannot pass the
  per-task gate. Tasks 2, 4, 5, 7 and 8 add their own keys (all nine languages, real translations,
  listed in the task). Task 10 removes the retired keys and runs the full check.
- **Two transient states are deliberate.** Task 1 lands the schema-3 migration while the record's
  `BackgroundMaterial` default stays `Mica` (Task 4 flips it to `Aurora` when Aurora can render)
  and `SplashStyle` stays `RemexCommand` (Task 9 flips it). A profile *created* on a developer
  machine between those tasks is created at `CustomizationMigration.CurrentSchemaVersion` directly
  (`DashboardLayoutService.FreshProfile`, RemEx-8twk0.1) rather than run through `Migrate`, so it
  keeps the record's own defaults and is never migrated — only a dev box running an interim build
  sees this; every profile that existed before Task 1 goes through the arm exactly once.
  `DashboardLayoutClobberTests.AFreshInstallStartsOnTheWindowsAccentSourceRatherThanMigratingToCustom`
  pins it: a fresh install must persist `ColorSource: WindowsAccent`, not the arm's `Custom`.
- **`SavedPalette` and the `CustomAccentColors` migration land in Task 1**, not Task 7, because
  the spec requires the whole 2-to-3 arm in one `with` expression and the reflection round-trip
  needs the record type on day one. Task 7 is the saved-palettes UI, apply/save/delete, and the
  export/import format extension.

## File structure

New files:

| File | Responsibility |
|---|---|
| `remex.desktop/Models/SchemeVariants.cs` | The seven Android strategy names, their order, and `Normalize` (retired/unknown names to Tonal Spot) |
| `remex.desktop/Services/WindowsAccentWatcher.cs` | Polls the Windows accent through a `TimeProvider` timer while the window is visible; raises `AccentChanged` |
| `remex.desktop/Services/ColorSourceCoordinator.cs` | Always-alive consumer of the watcher: rewrites `AccentColor` when the source is Windows accent, applies and saves through the existing paths |
| `remex.desktop/Services/WallpaperBackdrop.cs` | Pure helpers: blur mapping, path resolution |
| `remex.desktop/Services/WallpaperImageStore.cs` | Copies a picked image under the per-user directory, downscaled to 2560 px |
| `remex.desktop/Converters/BlurRadiusToEffectConverter.cs` | `double` radius to `BlurEffect` for the wallpaper `Image` |
| `remex.desktop/ViewModels/SavedPaletteTileViewModel.cs` | One user-palette tile, painted from its own recipe |
| `scripts/resx_add_keys.py`, `scripts/resx_remove_keys.py` | The only way resx files are bulk-edited in this plan |
| Tests, one file per concern, named in each task |

Modified files (owner task in brackets): `remex.core/Models/DashboardProfile.cs` [1, 4, 9],
`remex.core/Serialization/RemexJsonSerializerContext.cs` [1],
`remex.desktop/Services/CustomizationMigration.cs` [1],
`remex.desktop/Services/DynamicColorGenerator.cs` [2, 4],
`remex.desktop/Services/PaletteExchange.cs` [2, 7],
`remex.desktop/Services/ThemeService.cs` [4], `remex.desktop/Services/SystemSeedSources.cs` [5],
`remex.desktop/Models/SeedPreset.cs` [2, 9], `remex.desktop/ViewModels/CustomizationViewModel.cs`
[1, 2, 3, 4, 5, 6, 7, 8], `remex.desktop/ViewModels/ShellViewModel.cs` [5, 8],
`remex.desktop/Views/PersonalizationPanelView.axaml` [3, 5, 7, 8],
`remex.desktop/Views/PersonalizationPanelView.axaml.cs` [5],
`remex.desktop/Views/ShellView.axaml.cs` [8],
`remex.desktop/Controls/DashboardBackgroundControl.axaml` [4, 5, 6],
`remex.desktop/Controls/Splash/SkiaSplashControl.cs` [8, 9],
`remex.desktop/Converters/StringMatchConverter.cs` [4, 6],
`remex.desktop/Converters/PrefixedLabelConverter.cs` [6],
`remex.desktop/Themes/Shared/FallbackPalette.axaml` [4], `remex.desktop/MainWindow.axaml` [6],
`remex.desktop/MainWindow.axaml.cs` [3, 6], `remex.desktop/App.axaml.cs` [3],
`remex.desktop/Services/StartupViewArgument.cs` [8], nine `Strings*.resx` [2, 4, 5, 7, 8, 10],
`scripts/ui-palette-sweep.ps1` and `docs/UI-PALETTE-SWEEP.md` [11].

---

### Task 1: Model fields, `SavedPalette`, and the schema-3 migration

**Files:**
- Modify: `remex.core/Models/DashboardProfile.cs:172-185` (add `ColorSources`, `WallpaperSources`, `SavedPalette` after `ThemeModes`), `:240-244` (new properties after `CustomAccentColors`/`SchemeVariant`)
- Modify: `remex.core/Serialization/RemexJsonSerializerContext.cs:60` (add `[JsonSerializable(typeof(SavedPalette))]`)
- Modify: `remex.desktop/Services/CustomizationMigration.cs:36` (`CurrentSchemaVersion = 3`), `:63-80` (`Migrate` gains arm 3), new `FromSchemaTwo`
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs:905-943` (`ApplyAndSave` carries the six new fields forward)
- Modify: `remex.desktop.tests/Services/DashboardLayoutClobberTests.cs:507-536` (`BuildNonDefaultSettings` handles `int` and `IReadOnlyList<SavedPalette>`), `:545-565` (`AssertSameCustomization` compares palette lists)
- Test: `remex.desktop.tests/Services/CustomizationMigrationTests.cs` (append arm-3 tests)
- Test: `remex.core.tests/DashboardProfileTests.cs` (append a source-gen round trip)

**Interfaces:**
- Consumes: `CustomizationSettings` (`DashboardProfile.cs:190-318`), `CustomizationMigration.Migrate` (`:63-80`), `ThemeModes` constant style (`:180-185`).
- Produces (later tasks rely on these exact names):
  - `Remex.Core.Models.ColorSources` — `const string WindowsAccent = "WindowsAccent"`, `Wallpaper = "Wallpaper"`, `Custom = "Custom"`.
  - `Remex.Core.Models.WallpaperSources` — `const string Desktop = "Desktop"`, `Image = "Image"`.
  - `Remex.Core.Models.SavedPalette` — `record { string Name; string ColorSource; string Seed; double Vibrancy; double Contrast; string Strategy; }` with `const string DefaultNamePrefix = "Palette "`.
  - `CustomizationSettings.ColorSource` (string, default `"WindowsAccent"`), `WallpaperSeedIndex` (int, 0), `WallpaperSource` (string, `"Desktop"`), `WallpaperImagePath` (string?, null), `WallpaperBlur` (double, 0.6), `SavedPalettes` (`IReadOnlyList<SavedPalette>`, empty).
  - `CustomizationMigration.CurrentSchemaVersion == 3`; `private static CustomizationSettings FromSchemaTwo(CustomizationSettings)`.
  - `Remex.Desktop.Models.SchemeVariants.Normalize(string?)` is *consumed* here but *defined in Task 2* — Task 1 ships a private local `NormalizeVariant` inside `CustomizationMigration` that Task 2 replaces with the shared one (see Task 2 Step 4).

- [ ] **Step 1: Write the failing migration tests**

Append to `remex.desktop.tests/Services/CustomizationMigrationTests.cs` (inside the class, after the arm-2 block that ends near `:130`):

```csharp
    // ─── Arm 3: the personalization sheet (RemEx-ddynd / RemEx-z94c7) ──────────────────────────

    private static CustomizationSettings SchemaTwo() => new()
    {
        SchemaVersion = 2,
        ThemeMode = ThemeModes.Dark,
        AccentColor = "#00F3FF",
        ThemeSeedChroma = 61.0,
        ThemeContrast = 0.25,
        SchemeVariant = "Vibrant",
    };

    [Fact]
    public void AMicaProfileBecomesWallpaperOverTheDesktopAtHighBlur()
    {
        var migrated = CustomizationMigration.Migrate(SchemaTwo() with { BackgroundMaterial = "Mica" }, out _);

        migrated.BackgroundMaterial.Should().Be("Wallpaper", "Mica never rendered on this Avalonia build (RemEx-z94c7)");
        migrated.WallpaperSource.Should().Be(WallpaperSources.Desktop);
        migrated.WallpaperBlur.Should().Be(0.9);
    }

    [Fact]
    public void ANonMicaBackgroundKeepsItsOwnWallpaperFields()
    {
        var migrated = CustomizationMigration.Migrate(
            SchemaTwo() with { BackgroundMaterial = "Gradient", WallpaperBlur = 0.3, WallpaperSource = WallpaperSources.Image }, out _);

        migrated.BackgroundMaterial.Should().Be("Gradient");
        migrated.WallpaperBlur.Should().Be(0.3);
        migrated.WallpaperSource.Should().Be(WallpaperSources.Image);
    }

    [Theory]
    [InlineData("Spritz", "Neutral")]
    [InlineData("Content", "TonalSpot")]
    [InlineData("Fidelity", "TonalSpot")]
    [InlineData("", "TonalSpot")]
    [InlineData("Vibrant", "Vibrant")]
    [InlineData("FruitSalad", "FruitSalad")]
    public void RetiredAndUnknownVariantsNormaliseToTheSevenAndroidNames(string stored, string expected)
    {
        CustomizationMigration.Migrate(SchemaTwo() with { SchemeVariant = stored }, out _)
            .SchemeVariant.Should().Be(expected);
    }

    [Fact]
    public void TheOldSplashDefaultBecomesCosmicZoom()
    {
        CustomizationMigration.Migrate(SchemaTwo() with { SplashStyle = "RemexCommand" }, out _)
            .SplashStyle.Should().Be("CosmicZoom");
        CustomizationMigration.Migrate(SchemaTwo() with { SplashStyle = "Pong" }, out _)
            .SplashStyle.Should().Be("Pong", "only the old default is flipped; a choice is a choice");
    }

    [Fact]
    public void EachSavedSwatchBecomesANamedCustomPaletteAndTheSwatchListIsEmptied()
    {
        var migrated = CustomizationMigration.Migrate(
            SchemaTwo() with { CustomAccentColors = new[] { "#112233", "#445566" } }, out _);

        migrated.CustomAccentColors.Should().BeEmpty("the swatches moved into SavedPalettes");
        migrated.SavedPalettes.Should().HaveCount(2);
        migrated.SavedPalettes[0].Should().Be(new SavedPalette
        {
            Name = "Palette 1", ColorSource = ColorSources.Custom, Seed = "#112233",
            Vibrancy = 61.0, Contrast = 0.25, Strategy = "Vibrant",
        });
        migrated.SavedPalettes[1].Name.Should().Be("Palette 2");
        migrated.SavedPalettes[1].Seed.Should().Be("#445566");
    }

    [Fact]
    public void AMigratedProfileIsOnTheCustomSource()
    {
        // The file's seed was chosen by hand or by a preset; only a NEW profile starts on the
        // Windows accent.
        CustomizationMigration.Migrate(SchemaTwo(), out _).ColorSource.Should().Be(ColorSources.Custom);
        new CustomizationSettings().ColorSource.Should().Be(ColorSources.WindowsAccent);
    }

    [Fact]
    public void AProfileWithAllFourOldValuesAtOnceMigratesEveryOne()
    {
        var old = SchemaTwo() with
        {
            BackgroundMaterial = "Mica",
            SchemeVariant = "Spritz",
            SplashStyle = "RemexCommand",
            CustomAccentColors = new[] { "#ABCDEF" },
        };

        var migrated = CustomizationMigration.Migrate(old, out var warning);

        warning.Should().BeNull("nothing had to be repaired, only translated");
        migrated.BackgroundMaterial.Should().Be("Wallpaper");
        migrated.SchemeVariant.Should().Be("Neutral");
        migrated.SplashStyle.Should().Be("CosmicZoom");
        migrated.SavedPalettes.Should().ContainSingle(p => p.Seed == "#ABCDEF" && p.Strategy == "Neutral");
        migrated.CustomAccentColors.Should().BeEmpty();
        migrated.ColorSource.Should().Be(ColorSources.Custom);
        migrated.SchemaVersion.Should().Be(3);
    }

    [Fact]
    public void ArmThreeDropsNoField()
    {
        // The RemEx-8y3qy guard: the arm is one `with` expression, so every field it does not
        // name survives verbatim. Built by reflection so a field added next year is covered.
        var before = DashboardLayoutClobberTests.BuildNonDefaultSettings(schemaVersion: 2) with
        {
            BackgroundMaterial = "Gradient", SchemeVariant = "Rainbow", SplashStyle = "Pong",
            CustomAccentColors = Array.Empty<string>(), SavedPalettes = Array.Empty<SavedPalette>(),
        };

        var after = CustomizationMigration.Migrate(before, out _);

        after.Should().BeEquivalentTo(before with { SchemaVersion = 3, ColorSource = ColorSources.Custom },
            "arm 3 rewrites only the fields the spec names");
    }

    [Fact]
    public void ASchemaThreeProfileIsUntouched()
    {
        var current = SchemaTwo() with { SchemaVersion = 3, BackgroundMaterial = "Mica", SplashStyle = "RemexCommand" };

        CustomizationMigration.Migrate(current, out _).Should().BeSameAs(current,
            "a profile already at the current schema is returned as the same instance");
    }
```

Add `using System;` at the top of the file if it is not already there (it currently imports
`System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Runtime.CompilerServices`,
`System.Text.RegularExpressions`, `System.Text.Json` — `Array.Empty` needs `System`).

Append to `remex.core.tests/DashboardProfileTests.cs` (after `DashboardProfile_SourceGen_RoundTripsCardCustomization` near `:127`):

```csharp
    [Fact]
    public void CustomizationSettings_SourceGen_RoundTripsSavedPalettes()
    {
        var settings = new CustomizationSettings
        {
            ColorSource = ColorSources.Wallpaper,
            WallpaperSeedIndex = 2,
            WallpaperSource = WallpaperSources.Image,
            WallpaperImagePath = @"C:\Users\x\wallpaper-abc.png",
            WallpaperBlur = 0.35,
            SavedPalettes = new[]
            {
                new SavedPalette { Name = "Dusk", ColorSource = ColorSources.Custom, Seed = "#123456", Vibrancy = 40, Contrast = -0.2, Strategy = "Expressive" },
            },
        };

        var json = JsonSerializer.Serialize(settings, RemexJsonSerializerContext.Default.CustomizationSettings);
        var back = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.CustomizationSettings);

        json.Should().Contain("\"colorSource\"").And.Contain("\"savedPalettes\"").And.Contain("\"wallpaperBlur\"");
        back.Should().BeEquivalentTo(settings);
    }
```

(`DashboardProfileTests.cs` already uses `JsonSerializer` and `RemexJsonSerializerContext` for the
card round trip at `:127`; keep the same `using` lines. If it uses `Assert.*` rather than
FluentAssertions, write the two assertions as `Assert.Contains("\"colorSource\"", json)` and
`Assert.Equal(settings, back)` — `CustomizationSettings` is a record, but its list property makes
`Equal` compare by reference, so compare `back!.SavedPalettes[0]` to `settings.SavedPalettes[0]`
and each scalar field individually in that case.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo`
Expected: build errors — `ColorSources`, `WallpaperSources`, `SavedPalette`, `WallpaperBlur` etc. do not exist.

- [ ] **Step 3: Add the model**

In `remex.core/Models/DashboardProfile.cs`, after the `ThemeModes` class (`:180-185`), add:

```csharp
/// <summary>The values <see cref="CustomizationSettings.ColorSource"/> can carry.</summary>
/// <remarks>String constants for the same reason as <see cref="ThemeModes"/>: the record tolerates
/// unknown values by design, and the desktop resolves an unavailable source to Custom at load.</remarks>
public static class ColorSources
{
    public const string WindowsAccent = "WindowsAccent";
    public const string Wallpaper = "Wallpaper";
    public const string Custom = "Custom";
}

/// <summary>The values <see cref="CustomizationSettings.WallpaperSource"/> can carry.</summary>
public static class WallpaperSources
{
    public const string Desktop = "Desktop";
    public const string Image = "Image";
}

/// <summary>
/// A whole palette recipe the person chose to keep: where the seed came from, the seed itself, and
/// the three shaping inputs. Applying one reproduces the palette; it is not the palette.
/// </summary>
public record SavedPalette
{
    /// <summary>The English prefix the 2→3 migration names converted swatches with ("Palette 1",
    /// "Palette 2", …). Plain English on purpose: this assembly has no localisation, and the person
    /// renames the palette on the sheet.</summary>
    public const string DefaultNamePrefix = "Palette ";

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>A <see cref="ColorSources"/> value.</summary>
    [JsonPropertyName("colorSource")]
    public string ColorSource { get; init; } = ColorSources.Custom;

    /// <summary>The seed hex, e.g. "#6C4CFF".</summary>
    [JsonPropertyName("seed")]
    public string Seed { get; init; } = "#6C4CFF";

    /// <summary>The seed chroma (the Vibrancy slider).</summary>
    [JsonPropertyName("vibrancy")]
    public double Vibrancy { get; init; } = 48.0;

    /// <summary>The contrast target, -1.0 to 1.0.</summary>
    [JsonPropertyName("contrast")]
    public double Contrast { get; init; }

    /// <summary>One of the seven Android strategy names (the desktop normalises anything else).</summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = "TonalSpot";
}
```

Inside `CustomizationSettings`, immediately after `SchemeVariant` (`:243-244`), add:

```csharp
    /// <summary>Who writes <see cref="AccentColor"/>: a <see cref="ColorSources"/> value. New
    /// profiles start on the Windows accent; the desktop resolves an unavailable source (Linux) to
    /// Custom without persisting it (RemEx-ddynd).</summary>
    [JsonPropertyName("colorSource")]
    public string ColorSource { get; init; } = ColorSources.WindowsAccent;

    /// <summary>Which extracted wallpaper candidate was chosen. Out of range resets to 0 at load.</summary>
    [JsonPropertyName("wallpaperSeedIndex")]
    public int WallpaperSeedIndex { get; init; }

    /// <summary>A <see cref="WallpaperSources"/> value: the desktop's own wallpaper, or a picked image.</summary>
    [JsonPropertyName("wallpaperSource")]
    public string WallpaperSource { get; init; } = WallpaperSources.Desktop;

    /// <summary>Path of the APP-OWNED copy of a picked image, never the original file.</summary>
    [JsonPropertyName("wallpaperImagePath")]
    public string? WallpaperImagePath { get; init; }

    /// <summary>Wallpaper blur, 0 to 1, mapped to a blur radius by the desktop.</summary>
    [JsonPropertyName("wallpaperBlur")]
    public double WallpaperBlur { get; init; } = 0.6;

    /// <summary>The person's saved palettes, in the order they were saved.</summary>
    [JsonPropertyName("savedPalettes")]
    public IReadOnlyList<SavedPalette> SavedPalettes { get; init; } = Array.Empty<SavedPalette>();
```

Update the `CustomAccentColors` summary (`:240`) to: `/// <summary>Recently-used seeds (hex strings). Schema 3 converted the pre-existing entries into <see cref="SavedPalettes"/> and emptied this list once; the Custom source's recents row writes it again afterwards.</summary>`.

In `remex.core/Serialization/RemexJsonSerializerContext.cs`, before `[JsonSerializable(typeof(CustomizationSettings))]` (`:60`), add `[JsonSerializable(typeof(SavedPalette))]`.

- [ ] **Step 4: Write the migration arm**

In `remex.desktop/Services/CustomizationMigration.cs`:

Change `:34-36` to:

```csharp
    /// History: 1 = the seed engine (RemEx-dbkzy), 2 = tri-state ThemeMode (RemEx-zk5bc),
    /// 3 = the personalization sheet: colour source, wallpaper, saved palettes (RemEx-ddynd).
    /// </remarks>
    public const int CurrentSchemaVersion = 3;
```

In `Migrate` (`:76-79`), after `if (migrated.SchemaVersion < 2) migrated = StampThemeMode(migrated);` add:

```csharp
        if (migrated.SchemaVersion < 3) migrated = FromSchemaTwo(migrated);
```

Add after `StampThemeMode` (`:93-99`):

```csharp
    /// <summary>
    /// Schema 2 → 3: the personalization sheet (RemEx-ddynd, RemEx-z94c7). ONE <c>with</c>
    /// EXPRESSION, so a field this arm does not name cannot be dropped (the RemEx-8y3qy guard).
    /// </summary>
    /// <remarks>
    /// Mica never rendered on this Avalonia build, so a Mica profile becomes the real wallpaper,
    /// heavily blurred — the closest thing to what the person thought they had. Spritz is what
    /// Android calls Neutral; Content has no Android name and falls back to Tonal Spot, as does
    /// anything outside the seven. The old splash default flips because the person who asked for
    /// the new default is the person whose file holds the old one. Each saved swatch becomes a
    /// whole palette carrying the file's current shaping inputs, and the swatch list is emptied.
    /// A migrated file is on the Custom source: its seed was chosen by hand or by a preset.
    /// </remarks>
    private static CustomizationSettings FromSchemaTwo(CustomizationSettings settings)
    {
        var wasMica = string.Equals(settings.BackgroundMaterial, "Mica", StringComparison.OrdinalIgnoreCase);
        var variant = NormalizeVariant(settings.SchemeVariant);

        var seeds = settings.CustomAccentColors ?? Array.Empty<string>();
        var palettes = new List<SavedPalette>(settings.SavedPalettes ?? Array.Empty<SavedPalette>());
        for (var i = 0; i < seeds.Count; i++)
        {
            palettes.Add(new SavedPalette
            {
                Name = SavedPalette.DefaultNamePrefix + (i + 1),
                ColorSource = ColorSources.Custom,
                Seed = seeds[i],
                Vibrancy = settings.ThemeSeedChroma,
                Contrast = settings.ThemeContrast,
                Strategy = variant,
            });
        }

        return settings with
        {
            BackgroundMaterial = wasMica ? "Wallpaper" : settings.BackgroundMaterial,
            WallpaperSource = wasMica ? WallpaperSources.Desktop : settings.WallpaperSource,
            WallpaperBlur = wasMica ? 0.9 : settings.WallpaperBlur,
            SchemeVariant = variant,
            SplashStyle = string.Equals(settings.SplashStyle, "RemexCommand", StringComparison.Ordinal)
                ? "CosmicZoom"
                : settings.SplashStyle,
            SavedPalettes = palettes,
            CustomAccentColors = Array.Empty<string>(),
            ColorSource = ColorSources.Custom,
        };
    }

    /// <summary>Retired and unknown strategy names to the seven Android names. Replaced by
    /// <c>SchemeVariants.Normalize</c> in Task 2; kept private here so Task 1 builds alone.</summary>
    private static string NormalizeVariant(string? variant) => variant switch
    {
        "Spritz" => "Neutral",
        "TonalSpot" or "Expressive" or "FruitSalad" or "Rainbow" or "Vibrant" or "Neutral" or "Monochrome" => variant,
        _ => "TonalSpot",
    };
```

Add `using System.Collections.Generic;` and `using System.Linq;` to the file's usings if
missing (`Any` at `:199` already implies `System.Linq` is present via implicit usings — check the
project's `<ImplicitUsings>`; if the build says `List<>` is unknown, add the using).

- [ ] **Step 5: Carry the new fields through `ApplyAndSave` and the round-trip builder**

In `remex.desktop/ViewModels/CustomizationViewModel.cs`, inside the `new CustomizationSettings { … }`
initializer (`:905-943`), after `CustomAccentColors = CustomAccentColors.Take(MaxRecentSeeds).ToList()`
add (mind the trailing comma on the previous line):

```csharp
            // Task 1 carries these forward verbatim; Tasks 3, 5 and 7 replace each `carried.X`
            // with the view model's own live value as the sheet gains the control for it.
            ColorSource = carried.ColorSource,
            WallpaperSeedIndex = carried.WallpaperSeedIndex,
            WallpaperSource = carried.WallpaperSource,
            WallpaperImagePath = carried.WallpaperImagePath,
            WallpaperBlur = carried.WallpaperBlur,
            SavedPalettes = carried.SavedPalettes,
```

In `remex.desktop.tests/Services/DashboardLayoutClobberTests.cs`, in `BuildNonDefaultSettings`
(`:520-530`) extend the `switch`:

```csharp
                var t when t == typeof(int) => 7,
                var t when t == typeof(IReadOnlyList<SavedPalette>) => new List<SavedPalette>
                {
                    new() { Name = "nondefault", ColorSource = ColorSources.Wallpaper, Seed = "#778899", Vibrancy = 33.0, Contrast = 0.5, Strategy = "Rainbow" },
                },
```

and in `AssertSameCustomization` (`:545-565`) add a branch before the generic `else`:

```csharp
            else if (expectedValue is IReadOnlyList<SavedPalette> expectedPalettes && actualValue is IReadOnlyList<SavedPalette> actualPalettes)
            {
                actualPalettes.Should().Equal(expectedPalettes, because + $" (property: {prop.Name})");
            }
```

- [ ] **Step 6: Run the tests**

Run:
```
dotnet build remex.core.tests/remex.core.tests.csproj -c Release --nologo
dotnet test remex.core.tests/remex.core.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~DashboardProfileTests"
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~CustomizationMigrationTests|FullyQualifiedName~DashboardLayoutClobberTests|FullyQualifiedName~CustomizationSettingsRoundTripTests"
```
Expected: 0 warnings on both builds; every test in the three desktop classes and the core class
passes; the desktop count is at least 10 higher than before this task.

- [ ] **Step 7: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `pwsh scripts/verify.ps1 -Check`
Expected: VALID.

```bash
git add remex.core/Models/DashboardProfile.cs remex.core/Serialization/RemexJsonSerializerContext.cs remex.desktop/Services/CustomizationMigration.cs remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop.tests/Services/CustomizationMigrationTests.cs remex.desktop.tests/Services/DashboardLayoutClobberTests.cs remex.core.tests/DashboardProfileTests.cs
git commit -m "feat(core): colour source, wallpaper and saved-palette fields with the schema-3 migration (RemEx-<bead>)"
```

---

### Task 2: Engine strategies — the seven Android names, Neutral and Monochrome

**Files:**
- Create: `remex.desktop/Models/SchemeVariants.cs`
- Create: `scripts/resx_add_keys.py`
- Modify: `remex.desktop/Services/DynamicColorGenerator.cs:82-94` (`Generate` uses `SeedCoreFor` for the user seed), `:182-185` (`GenerateTonalRamps` likewise), `:197-213` (`StyleFor`, new `SeedCoreFor`)
- Modify: `remex.desktop/Services/PaletteExchange.cs:31-35` (`ValidVariants` → `SchemeVariants.All`), `:79` (normalise instead of reject)
- Modify: `remex.desktop/Services/CustomizationMigration.cs` (delete the private `NormalizeVariant` from Task 1; call `SchemeVariants.Normalize`)
- Modify: `remex.desktop/Models/SeedPreset.cs:99` and `:118` (`"Spritz"` → `"Neutral"` for Monolith and Sorbet)
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs:137-140` (`AvailableSchemeVariants` from `SchemeVariants.All`), `:543` (`_schemeVariant = SchemeVariants.Normalize(settings.SchemeVariant)`), `:1056-1057` (fallback uses `SchemeVariants.TonalSpot`)
- Modify: `remex.desktop.tests/Services/SeedPaletteTests.cs:51` and `remex.desktop.tests/Services/SeedPresetCatalogTests.cs:186` (variant lists → `SchemeVariants.All`)
- Modify: `remex.desktop.tests/Services/PaletteExchangeTests.cs:69` (`TryParseJson_RejectsUnknownVariant` becomes a normalisation test)
- Modify: nine `remex.desktop/Localization/Strings*.resx` (add `Custom_Scheme_Neutral`, `Custom_Scheme_Monochrome`)
- Test: `remex.desktop.tests/Services/SchemeStrategyTests.cs`

**Interfaces:**
- Consumes: `DynamicColorGenerator.Generate(Color, string, bool, double)` (`:82`), `GenerateTonalRamps(Color, string)` (`:182`), `CorePalette.Fill(uint, Style)`, `TonalPalette.FromHueAndChroma(double, double)`, `Hct.FromInt(uint)` (all from MaterialColorUtilities 0.3.0, verified by reflection: `Style` = {Spritz, TonalSpot, Vibrant, Expressive, Rainbow, FruitSalad, Content}; `CorePalette.Primary/Secondary/Tertiary/Neutral/NeutralVariant/Error` are settable `TonalPalette` properties). `SeedHct.FromColor(Color) → (Hue, Chroma, Tone)` (`remex.desktop/Services/SeedHct.cs:35`).
- Produces:
  - `Remex.Desktop.Models.SchemeVariants` — `const string TonalSpot = "TonalSpot"`, `Expressive`, `FruitSalad`, `Rainbow`, `Vibrant`, `Neutral`, `Monochrome`; `static IReadOnlyList<string> All` (that order); `static string Normalize(string? variant)`.
  - `DynamicColorGenerator.StyleFor` stays private; behaviour is observed through `Generate`/`GenerateTonalRamps`.
  - Resx keys `Custom_Scheme_Neutral`, `Custom_Scheme_Monochrome` (the strip's `DisplayName` reads `Custom_Scheme_{Variant}`, `SchemeVariantStripViewModel.cs:49`).
  - `scripts/resx_add_keys.py <keys.json>` — adds every key in the JSON map to all nine files; refuses duplicates and missing languages.

- [ ] **Step 1: Write the failing tests**

Create `remex.desktop.tests/Services/SchemeStrategyTests.cs`:

```csharp
using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The seven strategies the sheet offers are Android's seven, in Android's order, and the two
/// that have no MaterialColorUtilities 0.3.0 style — Neutral and Monochrome — render as the spec
/// says: Neutral is the library's low-chroma Spritz, Monochrome zeroes every tonal palette.
/// </summary>
public class SchemeStrategyTests
{
    private static readonly Color Seed = Color.Parse("#6C4CFF");

    [Fact]
    public void TheSevenStrategiesAreAndroidsInAndroidsOrder()
    {
        SchemeVariants.All.Should().Equal(
            "TonalSpot", "Expressive", "FruitSalad", "Rainbow", "Vibrant", "Neutral", "Monochrome");
    }

    [Theory]
    [InlineData("Spritz", "Neutral")]
    [InlineData("Content", "TonalSpot")]
    [InlineData("Fidelity", "TonalSpot")]
    [InlineData(null, "TonalSpot")]
    [InlineData("", "TonalSpot")]
    [InlineData("tonalspot", "TonalSpot")]
    [InlineData("Monochrome", "Monochrome")]
    public void NormalizeMapsRetiredAndUnknownNamesOntoTheSeven(string? stored, string expected)
    {
        SchemeVariants.Normalize(stored).Should().Be(expected);
    }

    [Fact]
    public void NeutralIsLowChromaWhereVibrantIsNot()
    {
        double ChromaAt50(string variant) =>
            SeedHct.FromColor(DynamicColorGenerator.GenerateTonalRamps(Seed, variant).Primary.Single(t => t.Tone == 50).Color).Chroma;

        ChromaAt50("Neutral").Should().BeLessThan(16, "Neutral is the library's Spritz style, chroma 12 on primary");
        ChromaAt50("Vibrant").Should().BeGreaterThan(30, "Vibrant pins primary chroma at 48");
    }

    [Fact]
    public void MonochromeHasNoChromaOnAnyTonalPalette()
    {
        var ramps = DynamicColorGenerator.GenerateTonalRamps(Seed, "Monochrome");

        foreach (var ramp in new[] { ramps.Primary, ramps.Secondary, ramps.Tertiary, ramps.Neutral })
        foreach (var (tone, color) in ramp)
        {
            if (tone is 0 or 100) continue; // black and white have no chroma whatever the ramp
            SeedHct.FromColor(color).Chroma.Should().BeLessThan(1.5, $"tone {tone} must be grey");
        }
    }

    [Fact]
    public void MonochromeKeepsSuccessGreenAndWarningAmber()
    {
        var palette = DynamicColorGenerator.Generate(Seed, "Monochrome", isDark: true);

        SeedHct.FromColor(palette.Success).Chroma.Should().BeGreaterThan(20, "success keeps its own seed (Theme.kt:110)");
        SeedHct.FromColor(palette.Warning).Chroma.Should().BeGreaterThan(20);
        SeedHct.FromColor(palette.Primary).Chroma.Should().BeLessThan(1.5);
    }

    [Fact]
    public void AnythingOutsideTheSevenRendersAsTonalSpot()
    {
        DynamicColorGenerator.Generate(Seed, "Fidelity", isDark: true)
            .Should().Be(DynamicColorGenerator.Generate(Seed, "TonalSpot", isDark: true));
        DynamicColorGenerator.Generate(Seed, "Content", isDark: false)
            .Should().Be(DynamicColorGenerator.Generate(Seed, "TonalSpot", isDark: false),
                "Content is no longer a user-facing strategy");
    }

    [Fact]
    public void EveryStrategyStillProducesAReadableSurfacePair()
    {
        foreach (var variant in SchemeVariants.All)
        foreach (var isDark in new[] { true, false })
        {
            var palette = DynamicColorGenerator.Generate(Seed, variant, isDark);
            DynamicColorGenerator.ContrastRatio(palette.Surface, palette.OnSurface)
                .Should().BeGreaterOrEqualTo(4.5, $"{variant} {(isDark ? "dark" : "light")}");
        }
    }
}
```

In `remex.desktop.tests/Services/PaletteExchangeTests.cs`, replace `TryParseJson_RejectsUnknownVariant`
(`:69`) with:

```csharp
    [Theory]
    [InlineData("Spritz", "Neutral")]
    [InlineData("Content", "TonalSpot")]
    [InlineData("Bogus", "TonalSpot")]
    public void TryParseJson_NormalisesRetiredAndUnknownVariants(string stored, string expected)
    {
        var json = PaletteExchange.ToJson(new PaletteRecipe("#FF00F3FF", stored, ThemeModes_Dark, 0.0, 40.0));

        PaletteExchange.TryParseJson(json, out var parsed).Should().BeTrue(
            "an older file imports with the section-4 migration rules rather than being refused");
        parsed!.Variant.Should().Be(expected);
    }
```

In `SeedPaletteTests.cs:51` and `SeedPresetCatalogTests.cs:186`, replace the literal seven-name
array with `SchemeVariants.All` (in `SeedPresetCatalogTests` the set is a `HashSet<string>` —
construct it as `new HashSet<string>(SchemeVariants.All, StringComparer.Ordinal)`). Add
`using Remex.Desktop.Models;` where missing.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo`
Expected: error — `SchemeVariants` does not exist.

- [ ] **Step 3: Write `SchemeVariants`**

Create `remex.desktop/Models/SchemeVariants.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Remex.Desktop.Models;

/// <summary>
/// The seven palette strategies the sheet offers — Android's seven, in Android's order
/// (<c>remex.android/.../ui/theme/Theme.kt</c>) — and the one place a persisted strategy string
/// is brought onto that list.
/// </summary>
/// <remarks>
/// NEUTRAL AND MONOCHROME HAVE NO STYLE OF THEIR OWN in MaterialColorUtilities 0.3.0 (its
/// <c>Style</c> enum is Spritz, TonalSpot, Vibrant, Expressive, Rainbow, FruitSalad, Content).
/// Neutral is the library's Spritz — the low-chroma style, which is what Android's Neutral is —
/// and Monochrome is built by <c>DynamicColorGenerator</c> zeroing every tonal palette's chroma.
/// Content is retired as a user-facing name; Spritz is retired as a NAME but not as a look.
/// <para>
/// Persisted strings stay English; the sheet localises them through <c>Custom_Scheme_*</c>.
/// </para>
/// </remarks>
public static class SchemeVariants
{
    public const string TonalSpot = "TonalSpot";
    public const string Expressive = "Expressive";
    public const string FruitSalad = "FruitSalad";
    public const string Rainbow = "Rainbow";
    public const string Vibrant = "Vibrant";
    public const string Neutral = "Neutral";
    public const string Monochrome = "Monochrome";

    /// <summary>Android's order. This is the order the strategy chips render in.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        TonalSpot, Expressive, FruitSalad, Rainbow, Vibrant, Neutral, Monochrome,
    };

    /// <summary>
    /// The strategy a persisted string means: Spritz → Neutral, Content → TonalSpot, anything
    /// not on <see cref="All"/> (case-sensitive, like every other persisted name) → TonalSpot.
    /// </summary>
    public static string Normalize(string? variant)
    {
        if (string.Equals(variant, "Spritz", StringComparison.Ordinal)) return Neutral;
        foreach (var known in All)
            if (string.Equals(known, variant, StringComparison.Ordinal)) return known;
        return TonalSpot;
    }
}
```

- [ ] **Step 4: Wire the engine, the exchange format, the migration, the catalog and the VM**

`remex.desktop/Services/DynamicColorGenerator.cs`:

Replace `StyleFor` and `CoreFor` (`:197-213`) with:

```csharp
    /// <summary>The library style behind a strategy name. Neutral AND Monochrome both start from
    /// Spritz; Monochrome then has its chroma removed in <see cref="SeedCoreFor"/>.</summary>
    private static Style StyleFor(string variant) => variant switch
    {
        SchemeVariants.Vibrant    => Style.Vibrant,
        SchemeVariants.Expressive => Style.Expressive,
        SchemeVariants.Rainbow    => Style.Rainbow,
        SchemeVariants.FruitSalad => Style.FruitSalad,
        SchemeVariants.Neutral    => Style.Spritz,
        SchemeVariants.Monochrome => Style.Spritz,
        _                         => Style.TonalSpot,
    };

    /// <summary>A core palette for a SEMANTIC seed (success, warning): the library style only.</summary>
    private static CorePalette CoreFor(uint argb, Style style)
    {
        var core = new CorePalette();
        core.Fill(argb, style);
        return core;
    }

    /// <summary>
    /// A core palette for the USER'S seed. Monochrome is not a library style in 0.3.0, so it is
    /// built here: every tonal palette except Error is re-created at chroma 0 on the seed's hue,
    /// which is exactly what Android's <c>SchemeMonochrome</c> produces. Error stays red — a grey
    /// error is not an error.
    /// </summary>
    private static CorePalette SeedCoreFor(uint argb, string variant)
    {
        var core = CoreFor(argb, StyleFor(variant));
        if (!string.Equals(variant, SchemeVariants.Monochrome, StringComparison.Ordinal)) return core;

        var hue = Hct.FromInt(argb).Hue;
        core.Primary = TonalPalette.FromHueAndChroma(hue, 0);
        core.Secondary = TonalPalette.FromHueAndChroma(hue, 0);
        core.Tertiary = TonalPalette.FromHueAndChroma(hue, 0);
        core.Neutral = TonalPalette.FromHueAndChroma(hue, 0);
        core.NeutralVariant = TonalPalette.FromHueAndChroma(hue, 0);
        return core;
    }
```

In `Generate` (`:84-85`) change `var core = CoreFor(ToArgb(seed), style);` to
`var core = SeedCoreFor(ToArgb(seed), variant);` (keep `var style = StyleFor(variant);` — the
success/warning cores still use it). In `GenerateTonalRamps` (`:184-185`) replace the two lines
with `var core = SeedCoreFor(ToArgb(seed), variant);`. Add `using System;` and
`using Remex.Desktop.Models;` to the file's usings. Update the `BackgroundStart` comment at
`:157` from "Styles whose palettes carry no chroma at all (Spritz)" to "(Neutral, Monochrome)".

`remex.desktop/Services/PaletteExchange.cs`: replace `:31-35` with
`private static IReadOnlyList<string> ValidVariants => SchemeVariants.All;` (add
`using Remex.Desktop.Models;`, `using System.Collections.Generic;`), and replace `:79`
(`if (Array.IndexOf(ValidVariants, dto.Variant) < 0) return false;`) with nothing — instead, at
`:83` build the recipe with `SchemeVariants.Normalize(dto.Variant)` in place of `dto.Variant`.
Update the doc comment at `:58-60` to say an unknown variant normalises to Tonal Spot rather than
failing. Keep `ValidVariants` referenced by `ToJson`'s callers only if something still uses it;
if nothing does after this edit, delete the property (the build's 0-warning rule flags unused
private members only under analyzers — check the build output).

`remex.desktop/Services/CustomizationMigration.cs`: delete the private `NormalizeVariant` added in
Task 1 and change its single call to `SchemeVariants.Normalize(settings.SchemeVariant)`
(`using Remex.Desktop.Models;` is already imported at `:4`).

`remex.desktop/Models/SeedPreset.cs`: `:99` `SchemeVariant: "Spritz"` → `SchemeVariant: "Neutral"`
(Monolith); `:118` likewise (Sorbet). Update the comments at `:80` and `:105-106` ("graphite wants
Spritz", "Sorbet is Spritz in light mode") to say Neutral. The rendered look is byte-identical:
Neutral maps to the same library style.

`remex.desktop/ViewModels/CustomizationViewModel.cs`:
- `:137-140` → `public ObservableCollection<string> AvailableSchemeVariants { get; } = new(SchemeVariants.All);`
- `:543` → `_schemeVariant = SchemeVariants.Normalize(settings.SchemeVariant);`
- `:1056-1057` → `s => string.Equals(s.Variant, SchemeVariants.TonalSpot, StringComparison.Ordinal)`, and
  rewrite the comment at `:1048-1053` to: an unrecognised persisted string is normalised at
  construction now, so this fallback only fires for a value assigned at runtime.

- [ ] **Step 5: Add the resx helper script and the two strategy names**

Create `scripts/resx_add_keys.py`:

```python
"""Add localisation keys to all nine Strings*.resx files from one JSON map.

Usage:  uv run python scripts/resx_add_keys.py <keys.json>

JSON shape (every key needs all nine languages; the script refuses otherwise):
{
  "Custom_Example": {"en": "…", "es": "…", "fr": "…", "hi": "…", "id": "…",
                     "pl": "…", "pt-BR": "…", "tr": "…", "uk": "…"}
}
Text-level insertion before </root>, preserving each file's BOM and line endings, so the diff is
only the added entries. Refuses a key that already exists in any file and refuses to write a NUL.
"""
import json
import os
import re
import sys
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOC = os.path.join(ROOT, "remex.desktop", "Localization")
FILES = {
    "en": "Strings.resx", "es": "Strings.es.resx", "fr": "Strings.fr.resx",
    "hi": "Strings.hi.resx", "id": "Strings.id.resx", "pl": "Strings.pl.resx",
    "pt-BR": "Strings.pt-BR.resx", "tr": "Strings.tr.resx", "uk": "Strings.uk.resx",
}


def main(path: str) -> None:
    with open(path, "rb") as f:
        keys = json.loads(f.read().decode("utf-8"))
    for lang in FILES:
        missing = [k for k, v in keys.items() if lang not in v or not v[lang].strip()]
        if missing:
            sys.exit(f"no {lang} text for {missing}")
    for lang, name in FILES.items():
        full = os.path.join(LOC, name)
        with open(full, "rb") as f:
            raw = f.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        nl = "\r\n" if "\r\n" in text else "\n"
        block = ""
        for key, values in keys.items():
            if re.search(r'<data name="' + re.escape(key) + '"', text):
                sys.exit(f"{name}: key {key} already exists")
            block += (f'  <data name="{key}" xml:space="preserve">{nl}'
                      f'    <value>{escape(values[lang])}</value>{nl}'
                      f'  </data>{nl}')
        idx = text.rfind("</root>")
        if idx < 0:
            sys.exit(f"{name}: no </root>")
        text = text[:idx] + block + text[idx:]
        if "\x00" in text:
            sys.exit(f"{name}: refusing to write a NUL byte")
        with open(full, "wb") as f:
            f.write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        print(f"{name}: +{len(keys)}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
```

Write `C:\Users\Connor\AppData\Local\Temp\claude\Z--RemEx\409da69b-6399-410c-843b-9201cec28c7e\scratchpad\task2-keys.json`
(any scratch path is fine; it is not committed):

```json
{
  "Custom_Scheme_Neutral": {"en": "Neutral", "es": "Neutro", "fr": "Neutre", "hi": "तटस्थ", "id": "Netral", "pl": "Neutralny", "pt-BR": "Neutro", "tr": "Nötr", "uk": "Нейтральна"},
  "Custom_Scheme_Monochrome": {"en": "Monochrome", "es": "Monocromo", "fr": "Monochromatique", "hi": "एकवर्णी", "id": "Monokrom", "pl": "Monochromatyczny", "pt-BR": "Monocromático", "tr": "Tek renkli", "uk": "Монохромна"}
}
```

Run: `uv run python scripts/resx_add_keys.py <that path>`
Expected: nine lines `Strings….resx: +2`.

- [ ] **Step 6: Run the tests**

Run:
```
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~SchemeStrategyTests|FullyQualifiedName~SeedPaletteTests|FullyQualifiedName~SeedPresetCatalogTests|FullyQualifiedName~PaletteExchangeTests|FullyQualifiedName~CustomizationMigrationTests|FullyQualifiedName~LocalizationKeyReferenceTests"
pwsh scripts/check-localization.ps1
```
Expected: 0 build warnings; all listed classes pass (`SeedPaletteTests.EveryVariantProducesADistinctPaletteFromTheSameSeed`
now iterates the seven new names — Neutral and Monochrome are distinct from every other, and from
each other, because Monochrome's ramps are grey); the localisation summary line ends
`errors=0` with the same warning count as before this task.

- [ ] **Step 7: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `pwsh scripts/verify.ps1 -Check` → VALID.

```bash
git add remex.desktop/Models/SchemeVariants.cs remex.desktop/Services/DynamicColorGenerator.cs remex.desktop/Services/PaletteExchange.cs remex.desktop/Services/CustomizationMigration.cs remex.desktop/Models/SeedPreset.cs remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop.tests/Services/SchemeStrategyTests.cs remex.desktop.tests/Services/SeedPaletteTests.cs remex.desktop.tests/Services/SeedPresetCatalogTests.cs remex.desktop.tests/Services/PaletteExchangeTests.cs scripts/resx_add_keys.py remex.desktop/Localization/Strings.resx remex.desktop/Localization/Strings.es.resx remex.desktop/Localization/Strings.fr.resx remex.desktop/Localization/Strings.hi.resx remex.desktop/Localization/Strings.id.resx remex.desktop/Localization/Strings.pl.resx remex.desktop/Localization/Strings.pt-BR.resx remex.desktop/Localization/Strings.tr.resx remex.desktop/Localization/Strings.uk.resx
git commit -m "feat(desktop): Android's seven palette strategies, Neutral and Monochrome in the engine (RemEx-<bead>)"
```

---

### Task 3: Colour sources — `ColorSource` state, the Windows-accent watcher, wallpaper candidate index

**Files:**
- Create: `remex.desktop/Services/WindowsAccentWatcher.cs`
- Create: `remex.desktop/Services/ColorSourceCoordinator.cs`
- Modify: `remex.desktop/App.axaml.cs:71-72` (register the two services after `HardwareThemeService`)
- Modify: `remex.desktop/MainWindow.axaml.cs:13` (field), `:40-43` (resolve the coordinator; hook `Opened`, `Activated`, `IsVisible`/`WindowState` changes), `:81-85` (nothing to detach — singleton)
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs`: ctor `:534-547` (load `ColorSource`, `WallpaperSeedIndex`, source availability), `:622-627` (`_onCustomizationApplied` adopts an accent the coordinator wrote), `:905-943` (`ApplyAndSave` writes `ColorSource`, `WallpaperSeedIndex`), `:1092-1137` (replace `SetAccent`'s neighbours: `MatchWindowsAccent`/`SeedFromWallpaperAsync` become source switches; add `AdoptSourceSeed`, `SelectWallpaperSeed`, `RefreshWallpaperSeedsAsync`)
- Modify: `remex.desktop/Views/PersonalizationPanelView.axaml:167` (wallpaper candidate swatches bind `SelectWallpaperSeedCommand`)
- Test: `remex.desktop.tests/Services/WindowsAccentWatcherTests.cs`, `remex.desktop.tests/Services/ColorSourceCoordinatorTests.cs`, `remex.desktop.tests/ViewModels/WallpaperSeedIndexTests.cs`

**Interfaces:**
- Consumes: `ColorSources` (Task 1), `SystemSeedSources.TryGetWindowsAccent()` (`:52`), `SystemSeedSources.ExtractWallpaperSeeds()` (`:83`), `SeedHct.FromColor/ToHex/ChromaOf`, `ThemeService.ApplyCustomization` (`:245`), `DashboardLayoutService.CurrentProfile`/`RequestSave` (`:121`, `:501`), `System.TimeProvider` (BCL, .NET 8+; no package).
- Produces:
  - `WindowsAccentWatcher(Func<string?> readAccent, TimeProvider timeProvider, TimeSpan? interval = null)`; `static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2)`; `void Start()`; `void SetVisible(bool)`; `void PollNow()`; `string? Current { get; }`; `event Action<string>? AccentChanged`; `IDisposable`.
  - `ColorSourceCoordinator(DashboardLayoutService, ThemeService, WindowsAccentWatcher)`; `void Start()`; `void SetWindowVisible(bool)`; `void PollNow()`; `internal void Apply(string hex)`; `internal static CustomizationSettings ShapedBySource(CustomizationSettings, string sourceHex)`.
  - `CustomizationViewModel`: `string ColorSource` (observable), `ObservableCollection<string> AvailableColorSources`, `bool IsWindowsAccentSource`, `bool IsWallpaperSource`, `bool IsCustomSource`, `string? SourceAccentHex` (observable; the raw Windows accent for the swatch), `int WallpaperSeedIndex` (observable), `SelectWallpaperSeedCommand(string hex)`, `RefreshWallpaperSeedsCommand`, `internal static int ResolveWallpaperSeedIndex(int stored, int count)`, private `AdoptSourceSeed(string hex)`.

- [ ] **Step 1: Write the failing watcher tests**

Create `remex.desktop.tests/Services/WindowsAccentWatcherTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The Windows-accent poller (RemEx-ddynd): a change is seen within two seconds while the window
/// is visible, nothing is polled while it is hidden, and a failed read keeps the last seed. Driven
/// by a hand-rolled <see cref="TimeProvider"/> so no test waits on a wall clock.
/// </summary>
public class WindowsAccentWatcherTests
{
    /// <summary>Fires timer callbacks only when the test advances it. Synchronous, single-threaded.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            foreach (var timer in _timers.ToArray()) timer.Advance(by);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _due;
            private TimeSpan _period;
            private TimeSpan _elapsed;

            public ManualTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            {
                _callback = callback; _state = state; _due = due; _period = period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime; _period = period; _elapsed = TimeSpan.Zero;
                return true;
            }

            public void Advance(TimeSpan by)
            {
                if (_due == Timeout.InfiniteTimeSpan) return;
                _elapsed += by;
                while (_due != Timeout.InfiniteTimeSpan && _elapsed >= _due)
                {
                    _elapsed -= _due;
                    _callback(_state);
                    if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                    {
                        _due = Timeout.InfiniteTimeSpan;
                        break;
                    }
                    _due = _period;
                }
            }

            public void Dispose() => _due = Timeout.InfiniteTimeSpan;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static (WindowsAccentWatcher Watcher, ManualTimeProvider Clock, List<string> Raised, Func<string?> Source) Build(string? initial)
    {
        var current = initial;
        var clock = new ManualTimeProvider();
        string? Read() => current;
        var watcher = new WindowsAccentWatcher(Read, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;
        return (watcher, clock, raised, () => current);
    }

    [Fact]
    public void AChangeIsRaisedWithinTwoSecondsWhileVisible()
    {
        var current = "#111111";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        current = "#222222";
        clock.Advance(TimeSpan.FromSeconds(2));

        raised.Should().Equal("#222222", "the spec's latency budget is two seconds");
        watcher.Current.Should().Be("#222222");
    }

    [Fact]
    public void NothingIsPolledWhileHidden()
    {
        var reads = 0;
        var current = "#111111";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => { reads++; return current; }, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();          // one read to seed Current
        watcher.SetVisible(false);
        var readsAfterStart = reads;
        current = "#333333";
        clock.Advance(TimeSpan.FromSeconds(30));

        reads.Should().Be(readsAfterStart, "the poll stops while the window is hidden");
        raised.Should().BeEmpty();
    }

    [Fact]
    public void BecomingVisibleAgainPollsImmediately()
    {
        var current = "#111111";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(false);
        current = "#444444";
        watcher.SetVisible(true);   // no clock advance: resume-from-sleep / restore-from-tray

        raised.Should().Equal("#444444");
    }

    [Fact]
    public void AnUnchangedAccentRaisesNothingHoweverLongItIsWatched()
    {
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => "#111111", clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        clock.Advance(TimeSpan.FromMinutes(5));

        raised.Should().BeEmpty();
    }

    [Fact]
    public void CaseOnlyDifferencesAreNotAChange()
    {
        var current = "#ABCDEF";
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => current, clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        current = "#abcdef";
        clock.Advance(TimeSpan.FromSeconds(2));

        raised.Should().BeEmpty();
    }

    [Fact]
    public void AFailedOrEmptyReadKeepsTheLastSeed()
    {
        var mode = 0;
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(
            () => mode switch { 0 => "#111111", 1 => null, _ => throw new InvalidOperationException("registry gone") },
            clock);
        var raised = new List<string>();
        watcher.AccentChanged += raised.Add;

        watcher.Start();
        watcher.SetVisible(true);
        mode = 1;
        clock.Advance(TimeSpan.FromSeconds(2));
        mode = 2;
        clock.Advance(TimeSpan.FromSeconds(2));

        raised.Should().BeEmpty("a failed registry read keeps the last seed (spec section 9)");
        watcher.Current.Should().Be("#111111");
    }

    [Fact]
    public void BeforeStartNothingIsPolledEvenWhenVisible()
    {
        var reads = 0;
        var clock = new ManualTimeProvider();
        using var watcher = new WindowsAccentWatcher(() => { reads++; return "#111111"; }, clock);

        watcher.SetVisible(true);
        clock.Advance(TimeSpan.FromSeconds(10));

        reads.Should().Be(0);
    }
}
```

Create `remex.desktop.tests/Services/ColorSourceCoordinatorTests.cs`:

```csharp
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The pure half of the coordinator: a source colour supplies hue and tone, the profile's own
/// vibrancy supplies chroma, so the Vibrancy slider keeps shaping a seed the person cannot edit.
/// </summary>
public class ColorSourceCoordinatorTests
{
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
}
```

Create `remex.desktop.tests/ViewModels/WallpaperSeedIndexTests.cs`:

```csharp
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class WallpaperSeedIndexTests
{
    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(5, 5, 0)]   // wallpaper changed, fewer candidates: first candidate, index reset
    [InlineData(-1, 5, 0)]
    [InlineData(2, 0, 0)]
    public void AnOutOfRangeStoredIndexFallsBackToTheFirstCandidate(int stored, int count, int expected)
    {
        CustomizationViewModel.ResolveWallpaperSeedIndex(stored, count).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo`
Expected: errors — `WindowsAccentWatcher`, `ColorSourceCoordinator`, `ResolveWallpaperSeedIndex` do not exist.

- [ ] **Step 3: Write the watcher**

Create `remex.desktop/Services/WindowsAccentWatcher.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using Remex.Core.Guards;

namespace Remex.Desktop.Services;

/// <summary>
/// Follows the Windows accent colour while the window is visible (RemEx-ddynd): a two-second poll
/// of the DWM registry key, paused while hidden, re-read immediately on becoming visible again.
/// </summary>
/// <remarks>
/// A POLL, NOT A REGISTRY CHANGE NOTIFICATION, on purpose. <c>RegNotifyChangeKeyValue</c> needs a
/// dedicated thread per watched key and its own resume-from-sleep handling; a two-second poll of
/// one dword is cheaper than the thread, meets the spec's latency budget, and stops costing
/// anything at all while the window is hidden. Resume from sleep needs no special case: the timer
/// resumes with the process, and the window's <c>Activated</c> hook calls <see cref="PollNow"/>.
/// <para>
/// THE TIMER COMES FROM A <see cref="TimeProvider"/> so the two-second budget and the stop-while-
/// hidden rule are pinned by tests on a fake clock rather than a wall clock. Callbacks arrive on a
/// thread-pool thread; the consumer marshals to the UI thread.
/// </para>
/// </remarks>
public sealed class WindowsAccentWatcher : IDisposable
{
    /// <summary>The spec's budget: a Settings change shows within two seconds.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly Func<string?> _readAccent;
    private readonly ITimer _timer;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private string? _last;
    private bool _visible;
    private bool _running;

    /// <summary>Raised with the new "#RRGGBB" when the accent differs from the last one seen.</summary>
    public event Action<string>? AccentChanged;

    /// <summary>The last accent successfully read, or null before <see cref="Start"/>.</summary>
    public string? Current { get { lock (_gate) return _last; } }

    public WindowsAccentWatcher(Func<string?> readAccent, TimeProvider timeProvider, TimeSpan? interval = null)
    {
        _readAccent = Guard.NotNull(readAccent);
        _interval = interval ?? DefaultInterval;
        _timer = Guard.NotNull(timeProvider).CreateTimer(
            _ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Reads the accent once to seed <see cref="Current"/> and arms the poll (if visible).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _running = true;
            _last = TryRead() ?? _last;
        }
        ApplySchedule();
    }

    /// <summary>Tell the watcher whether the window can be seen. Hidden stops the poll; visible re-reads at once.</summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            if (_visible == visible) return;
            _visible = visible;
        }
        ApplySchedule();
        if (visible) Poll();
    }

    /// <summary>An out-of-band read — the window's Activated hook uses it after a resume.</summary>
    public void PollNow() => Poll();

    private void ApplySchedule()
    {
        bool on;
        lock (_gate) on = _running && _visible;
        var period = on ? _interval : Timeout.InfiniteTimeSpan;
        _timer.Change(period, period);
    }

    private void Poll()
    {
        string? hex;
        lock (_gate)
        {
            if (!_running) return;
            hex = TryRead();
            if (hex is null || string.Equals(hex, _last, StringComparison.OrdinalIgnoreCase)) return;
            _last = hex;
        }
        AccentChanged?.Invoke(hex);
    }

    /// <summary>A failed read is "no answer": the last seed stands (spec section 9).</summary>
    private string? TryRead()
    {
        try
        {
            return _readAccent();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WindowsAccentWatcher: accent read failed — {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _timer.Dispose();
}
```

(`Remex.Core.Guards.Guard.NotNull` is the repo's argument guard, `remex.core/Guards/Guard.cs`.)

- [ ] **Step 4: Write the coordinator and register both**

Create `remex.desktop/Services/ColorSourceCoordinator.cs`:

```csharp
using System;
using Avalonia.Media;
using Avalonia.Threading;
using Remex.Core.Guards;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// The always-alive consumer of <see cref="WindowsAccentWatcher"/>: when the profile's colour
/// source is the Windows accent, a changed accent rewrites <c>AccentColor</c>, repaints through
/// <see cref="ThemeService.ApplyCustomization"/> and saves through the debounced path.
/// </summary>
/// <remarks>
/// NOT THE CUSTOMIZATION VIEW MODEL, because that is built lazily by <c>ShellViewModel</c> the
/// first time the sheet opens — a person who never opens it would never follow the accent. The
/// view model, when it exists, hears the resulting <c>CustomizationApplied</c> and adopts the new
/// seed without saving again.
/// </remarks>
public sealed class ColorSourceCoordinator : IDisposable
{
    private readonly DashboardLayoutService _layout;
    private readonly ThemeService _theme;
    private readonly WindowsAccentWatcher _watcher;

    public ColorSourceCoordinator(DashboardLayoutService layout, ThemeService theme, WindowsAccentWatcher watcher)
    {
        _layout = Guard.NotNull(layout);
        _theme = Guard.NotNull(theme);
        _watcher = Guard.NotNull(watcher);
        _watcher.AccentChanged += OnAccentChanged;
    }

    /// <summary>Windows only; elsewhere there is no accent to follow and nothing is armed.</summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        _watcher.Start();
        // The accent may have changed while the app was closed: adopt it now if the profile follows it.
        if (_watcher.Current is { } hex) Apply(hex);
    }

    public void SetWindowVisible(bool visible) => _watcher.SetVisible(visible);

    public void PollNow() => _watcher.PollNow();

    private void OnAccentChanged(string hex) => Dispatcher.UIThread.Post(() => Apply(hex));

    /// <summary>UI thread. No-op unless the profile's source is the Windows accent.</summary>
    internal void Apply(string hex)
    {
        var profile = _layout.CurrentProfile;
        var settings = profile.Customization;
        if (!string.Equals(settings.ColorSource, ColorSources.WindowsAccent, StringComparison.Ordinal)) return;

        var shaped = ShapedBySource(settings, hex);
        if (string.Equals(shaped.AccentColor, settings.AccentColor, StringComparison.OrdinalIgnoreCase)) return;

        _theme.ApplyCustomization(shaped);
        _layout.RequestSave(profile with { Customization = shaped });
    }

    /// <summary>
    /// The seed a source colour becomes: the source's hue and tone, the profile's own vibrancy
    /// (<c>ThemeSeedChroma</c>) as chroma — so the Vibrancy slider keeps shaping a seed the person
    /// cannot type. Returns the same instance when the source is not a colour.
    /// </summary>
    internal static CustomizationSettings ShapedBySource(CustomizationSettings settings, string sourceHex)
    {
        if (!Color.TryParse(sourceHex, out var source)) return settings;

        var (hue, _, tone) = SeedHct.FromColor(source);
        var seed = SeedHct.ToHex(hue, settings.ThemeSeedChroma, tone);
        return settings with
        {
            AccentColor = seed,
            ThemeSeedChroma = SeedHct.ChromaOf(seed, settings.ThemeSeedChroma),
        };
    }

    public void Dispose() => _watcher.AccentChanged -= OnAccentChanged;
}
```

In `remex.desktop/App.axaml.cs`, after `collection.AddSingleton<HardwareThemeService>();` (`:72`):

```csharp
        collection.AddSingleton(_ => new WindowsAccentWatcher(SystemSeedSources.TryGetWindowsAccent, TimeProvider.System));
        collection.AddSingleton<ColorSourceCoordinator>();
```

In `remex.desktop/MainWindow.axaml.cs`: add a field `private ColorSourceCoordinator? _colorSources;`
next to `_themeService` (`:13`); in the constructor after the `CustomizationApplied` subscription
(`:40-43`) add:

```csharp
        _colorSources = App.Services.GetService<ColorSourceCoordinator>(); // optional, like the theme service
        if (_colorSources is not null)
        {
            Opened += (_, _) =>
            {
                _colorSources.Start();
                _colorSources.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
            };
            Activated += (_, _) => _colorSources.PollNow();
        }
```

and override property changes so hide-to-tray (`App.axaml.cs:563` calls `MainWindow.Hide()`) and
minimise stop the poll:

```csharp
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
            _colorSources?.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
    }
```

(`using Avalonia;` for `AvaloniaPropertyChangedEventArgs` and `using Avalonia.Controls;` for
`WindowState` — check which are already imported.)

- [ ] **Step 5: Give the view model its source state**

In `remex.desktop/ViewModels/CustomizationViewModel.cs`:

Replace the block `:1092-1137` (`SetAccent` through `SeedFromWallpaperAsync`) with:

```csharp
    /// <summary>Clicking a recently-used seed under the Custom source. The only seed setter left
    /// on the sheet, and it lives inside the Custom source on purpose (spec section 1).</summary>
    [RelayCommand]
    private void SetAccent(string hex) => AccentColor = hex;

    // ─── Colour source (RemEx-ddynd) ─────────────────────────────────────────────────────────────
    //
    // ONE SEED, ONE PATH. AccentColor stays the seed for every source; the source only decides who
    // writes it. Windows accent and Wallpaper hand over a hue and a tone; the Vibrancy slider
    // (SeedChroma) stays the chroma; PushSeedToAccent recombines the three exactly as a wheel drag
    // does. Custom lets the person write all three.

    /// <summary>A <see cref="ColorSources"/> value. Persisted; the picker binds it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowsAccentSource))]
    [NotifyPropertyChangedFor(nameof(IsWallpaperSource))]
    [NotifyPropertyChangedFor(nameof(IsCustomSource))]
    private string _colorSource = ColorSources.Custom;

    /// <summary>The sources this platform can offer, in picker order. Windows: all three; Linux: Custom only —
    /// the seed extraction in SystemSeedSources is Windows-only (spec section 9).</summary>
    public ObservableCollection<string> AvailableColorSources { get; } = new();

    public bool IsWindowsAccentSource => ColorSource == ColorSources.WindowsAccent;
    public bool IsWallpaperSource => ColorSource == ColorSources.Wallpaper;
    public bool IsCustomSource => ColorSource == ColorSources.Custom;

    /// <summary>The raw Windows accent as read from the registry, for the swatch. Null when unavailable.</summary>
    [ObservableProperty]
    private string? _sourceAccentHex;

    /// <summary>Which extracted wallpaper candidate is in use. Persisted.</summary>
    [ObservableProperty]
    private int _wallpaperSeedIndex;

    /// <summary>Whether the "from this PC" sources exist on this platform.</summary>
    public bool IsSystemSeedAvailable => OperatingSystem.IsWindows();

    /// <summary>The wallpaper's top seed candidates, best first, as hex strings for the swatch template.</summary>
    public ObservableCollection<string> WallpaperSeedCandidates { get; } = new();

    public bool HasWallpaperSeedCandidates => WallpaperSeedCandidates.Count > 0;

    partial void OnColorSourceChanged(string value)
    {
        switch (value)
        {
            case ColorSources.WindowsAccent:
                SourceAccentHex = SystemSeedSources.TryGetWindowsAccent();
                // A missing accent (a stripped-down Windows can lack the key) leaves the seed alone
                // and still records the choice, so the coordinator picks it up if the key appears.
                if (SourceAccentHex is { } hex) AdoptSourceSeed(hex);
                else ApplyAndSave();
                break;
            case ColorSources.Wallpaper:
                _ = RefreshWallpaperSeedsAsync();
                break;
            default:
                ApplyAndSave();
                break;
        }
    }

    /// <summary>The stored index, or 0 when the candidate list no longer reaches it.</summary>
    internal static int ResolveWallpaperSeedIndex(int stored, int count) =>
        stored >= 0 && stored < count ? stored : 0;

    /// <summary>
    /// Takes hue and tone from a source colour, keeps the Vibrancy slider's chroma, and recombines
    /// them into <see cref="AccentColor"/> through the same path a wheel drag uses.
    /// </summary>
    private void AdoptSourceSeed(string hex)
    {
        if (!Color.TryParse(hex, out var source)) return;

        var (hue, _, tone) = SeedHct.FromColor(source);
        _isSyncingSeed = true;
        try
        {
            SeedHue = hue;
            SeedTone = tone;
        }
        finally
        {
            _isSyncingSeed = false;
        }

        PushSeedToAccent();
    }

    /// <summary>The old "Match Windows accent" button and the picker both land here.</summary>
    [RelayCommand]
    private void MatchWindowsAccent() => ColorSource = ColorSources.WindowsAccent;

    /// <summary>The old "Seed from wallpaper" button and the picker both land here.</summary>
    [RelayCommand]
    private void SeedFromWallpaper() => ColorSource = ColorSources.Wallpaper;

    /// <summary>A candidate swatch click: remember which one, and adopt it.</summary>
    [RelayCommand]
    private void SelectWallpaperSeed(string hex)
    {
        var index = WallpaperSeedCandidates.IndexOf(hex);
        if (index < 0) return;
        WallpaperSeedIndex = index;
        AdoptSourceSeed(hex);
    }

    /// <summary>Re-extracts the candidates (the Refresh action, and every switch to the Wallpaper source).</summary>
    [RelayCommand]
    private Task RefreshWallpaperSeeds() => RefreshWallpaperSeedsAsync();

    private async Task RefreshWallpaperSeedsAsync()
    {
        // Decode + quantize + score is CPU-bound and a 4K wallpaper is a real file — off the UI
        // thread, the same rule the palette solve follows.
        var seeds = await Task.Run(SystemSeedSources.ExtractWallpaperSeeds);

        WallpaperSeedCandidates.Clear();
        foreach (var seed in seeds) WallpaperSeedCandidates.Add(seed);
        OnPropertyChanged(nameof(HasWallpaperSeedCandidates));

        // The list may be shorter than it was when the index was stored (the wallpaper changed):
        // the first candidate is used and the index reset (spec section 5).
        WallpaperSeedIndex = ResolveWallpaperSeedIndex(WallpaperSeedIndex, seeds.Count);
        if (seeds.Count > 0 && IsWallpaperSource) AdoptSourceSeed(seeds[WallpaperSeedIndex]);
        else if (IsWallpaperSource) ApplyAndSave();
    }
```

(`IsSystemSeedAvailable`, `WallpaperSeedCandidates` and `HasWallpaperSeedCandidates` are moved
into this block unchanged from `:1097-1112`; delete the originals.)

In the constructor (`:534-547`), after `_themeContrast = …` add:

```csharp
        _wallpaperSeedIndex = Math.Max(0, settings.WallpaperSeedIndex);

        // Which sources this platform offers, then the stored choice resolved onto them without a
        // save: a Windows profile opened on Linux runs on Custom for the session (spec section 9).
        if (OperatingSystem.IsWindows())
        {
            AvailableColorSources.Add(ColorSources.WindowsAccent);
            AvailableColorSources.Add(ColorSources.Wallpaper);
        }
        AvailableColorSources.Add(ColorSources.Custom);
        _colorSource = AvailableColorSources.Contains(settings.ColorSource) ? settings.ColorSource : ColorSources.Custom;
        if (_colorSource == ColorSources.WindowsAccent) _sourceAccentHex = SystemSeedSources.TryGetWindowsAccent();
```

At the end of the constructor (after `ValidateFonts();`, `:633`) add: if the source is Wallpaper,
repopulate the candidates so the swatches show on open: `if (IsWallpaperSource) _ = RefreshWallpaperSeedsAsync();`
— this calls `AdoptSourceSeed` which saves only if the seed actually changes (`PushSeedToAccent`
assigns `AccentColor`; the generated setter is a no-op for an equal string).

In `_onCustomizationApplied` (`:622-626`) extend the lambda so an accent written by the
coordinator reaches the sliders without a second save:

```csharp
        _onCustomizationApplied = settings =>
        {
            // A seed the coordinator wrote (Windows accent changed) has to reach the wheel and the
            // hex box, or the next slider nudge writes the old seed back over it. _isApplyingPreset
            // short-circuits ApplyAndSave, so this is a sync, not a second save.
            if (!IsCustomSource && !string.Equals(settings.AccentColor, AccentColor, StringComparison.OrdinalIgnoreCase))
            {
                _isApplyingPreset = true;
                try { AccentColor = settings.AccentColor; }
                finally { _isApplyingPreset = false; }
            }
            if (IsWindowsAccentSource) SourceAccentHex = SystemSeedSources.TryGetWindowsAccent();

            RefreshPresetPreviews(onlyVarying: true);
            RefreshSchemeVariantStrips();
        };
```

In `ApplyAndSave` (`:905-943`) replace `ColorSource = carried.ColorSource,` with
`ColorSource = ColorSource,` and `WallpaperSeedIndex = carried.WallpaperSeedIndex,` with
`WallpaperSeedIndex = WallpaperSeedIndex,`.

In `remex.desktop/Views/PersonalizationPanelView.axaml:167`, change the wallpaper candidate
swatch's command from `SetAccentCommand` to `SelectWallpaperSeedCommand` (the whole attribute:
`Command="{Binding $parent[UserControl].DataContext.SelectWallpaperSeedCommand}"`). The
`MatchWindowsAccentCommand`/`SeedFromWallpaperCommand` buttons at `:153-154` keep their names and
now switch the source.

- [ ] **Step 6: Run the tests**

Run:
```
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~WindowsAccentWatcherTests|FullyQualifiedName~ColorSourceCoordinatorTests|FullyQualifiedName~WallpaperSeedIndexTests|FullyQualifiedName~CustomizationSettingsRoundTripTests|FullyQualifiedName~PaletteStudioWiringTests"
```
Expected: 0 warnings; all pass; the count rises by 16.

- [ ] **Step 7: Eyes pass**

Run: `pwsh scripts/ui-hotreload.ps1 -Start`, open the Personalize drawer by clicking the gear
FAB (a click on the running window is not an injected keystroke), click **Match Windows accent**,
then change the accent in Windows Settings > Personalization > Colours. Expected: the app
recolours within two seconds. `pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree` to record it, then
`pwsh scripts/ui-hotreload.ps1 -Stop`. Record the observation in the bead.

- [ ] **Step 8: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `pwsh scripts/verify.ps1 -Check` → VALID.

```bash
git add remex.desktop/Services/WindowsAccentWatcher.cs remex.desktop/Services/ColorSourceCoordinator.cs remex.desktop/App.axaml.cs remex.desktop/MainWindow.axaml.cs remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop/Views/PersonalizationPanelView.axaml remex.desktop.tests/Services/WindowsAccentWatcherTests.cs remex.desktop.tests/Services/ColorSourceCoordinatorTests.cs remex.desktop.tests/ViewModels/WallpaperSeedIndexTests.cs
git commit -m "feat(desktop): colour sources — Windows accent follows Settings live, wallpaper candidate is remembered (RemEx-<bead>)"
```

---

### Task 4: Aurora mode — its own value, bolder mesh, light/dark colour sets from the ramp

**Files:**
- Modify: `remex.desktop/Converters/StringMatchConverter.cs:31-34` (add `IsAurora` next to `IsWallpaper`)
- Modify: `remex.desktop/Services/DynamicColorGenerator.cs:171-192` (add `AuroraSet` record and `AuroraColors` after `GenerateTonalRamps`)
- Modify: `remex.desktop/Services/ThemeService.cs:365-372` (push `AuroraPrimary/Secondary/Tertiary` after the accent overrides; `isLightTheme` is in scope from `:310`)
- Modify: `remex.desktop/Themes/Shared/FallbackPalette.axaml:40-42` (three fallback colours after `AccentPressed`)
- Modify: `remex.desktop/Controls/DashboardBackgroundControl.axaml:113-219` (the mesh panel becomes Aurora)
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs:636-657` (`RefreshBackgroundTypes` offers `Aurora` first; fallback `Aurora`)
- Modify: `remex.core/Models/DashboardProfile.cs:276-278` (`BackgroundMaterial` default `"Aurora"`)
- Modify: `remex.desktop.tests/Services/DashboardLayoutClobberTests.cs:221` (`"Mica"` → `"Aurora"`)
- Modify: nine `Strings*.resx` (`Custom_BgType_Aurora`)
- Test: `remex.desktop.tests/Controls/AuroraMeshTests.cs`, `remex.desktop.tests/Services/AuroraColorsTests.cs`

**Interfaces:**
- Consumes: `DynamicColorGenerator.GenerateTonalRamps(Color, string)` (`:182`, tones 0,10,…,100), `ThemeService.ResolveIsLight` result `isLightTheme` (`ThemeService.cs:310`), `ShellViewModel.IsReducedMotion` (`:319`), `StringMatchConverter` pattern (`:31-34`).
- Produces:
  - `StringMatchConverter.IsAurora` (`"Aurora"`).
  - `DynamicColorGenerator.AuroraSet(Color Primary, Color Secondary, Color Tertiary)`; `static AuroraSet AuroraColors(Color seed, string variant, bool isLight)` — dark: tone 30 of each ramp; light: tone 90.
  - Resource keys `AuroraPrimary`, `AuroraSecondary`, `AuroraTertiary` (Colors), overridden by `ThemeService` on every apply.
  - Background type string `"Aurora"`; resx `Custom_BgType_Aurora`.

- [ ] **Step 1: Write the failing tests**

Create `remex.desktop.tests/Services/AuroraColorsTests.cs`:

```csharp
using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Aurora's two colour sets come straight off the tonal ramp (spec section 6): the primary,
/// secondary and tertiary palettes at tone 30 on a dark surface, tone 90 on a light one — the
/// tones Material uses for its containers.
/// </summary>
public class AuroraColorsTests
{
    private static readonly Color Seed = Color.Parse("#6C4CFF");

    [Fact]
    public void TheDarkSetIsTheThreeRampsAtToneThirty()
    {
        var ramps = DynamicColorGenerator.GenerateTonalRamps(Seed, "TonalSpot");
        var set = DynamicColorGenerator.AuroraColors(Seed, "TonalSpot", isLight: false);

        set.Primary.Should().Be(ramps.Primary.Single(t => t.Tone == 30).Color);
        set.Secondary.Should().Be(ramps.Secondary.Single(t => t.Tone == 30).Color);
        set.Tertiary.Should().Be(ramps.Tertiary.Single(t => t.Tone == 30).Color);
    }

    [Fact]
    public void TheLightSetIsTheThreeRampsAtToneNinety()
    {
        var ramps = DynamicColorGenerator.GenerateTonalRamps(Seed, "Vibrant");
        var set = DynamicColorGenerator.AuroraColors(Seed, "Vibrant", isLight: true);

        set.Primary.Should().Be(ramps.Primary.Single(t => t.Tone == 90).Color);
        set.Secondary.Should().Be(ramps.Secondary.Single(t => t.Tone == 90).Color);
        set.Tertiary.Should().Be(ramps.Tertiary.Single(t => t.Tone == 90).Color);
    }

    [Fact]
    public void TheTwoSetsDifferSoSystemModeVisiblyFlipsWithTheOs()
    {
        DynamicColorGenerator.AuroraColors(Seed, "TonalSpot", isLight: false)
            .Should().NotBe(DynamicColorGenerator.AuroraColors(Seed, "TonalSpot", isLight: true));
    }

    [Fact]
    public void MonochromeAuroraIsGrey()
    {
        var set = DynamicColorGenerator.AuroraColors(Seed, "Monochrome", isLight: false);
        SeedHct.FromColor(set.Primary).Chroma.Should().BeLessThan(1.5);
    }
}
```

Create `remex.desktop.tests/Controls/AuroraMeshTests.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// The Aurora mesh in <c>DashboardBackgroundControl.axaml</c> (RemEx-ddynd): its own mode value,
/// blobs half again as large and visibly bolder than the old Wallpaper-named mesh, colours from
/// the seed-derived Aurora resources, and reduced motion FREEZING the mesh at its first keyframe
/// rather than hiding it. Source-text, because this test project has no headless render.
/// </summary>
public class AuroraMeshTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    // The old mesh's numbers, so "up by half" is a measurement against something.
    private const double OldMaxRadiusX = 0.70, OldMaxRadiusY = 0.75;
    private const double OldPeakOpacityLayer1 = 0.70, OldPeakOpacityLayer2 = 0.55, OldPeakOpacityLayer3 = 0.42;

    private static XElement AuroraPanel()
    {
        var doc = XDocument.Load(ControlPath());
        return doc.Descendants(XName.Get("Panel", Avalonia))
            .Single(p => (p.Attribute("IsVisible")?.Value ?? "").Contains("StringMatchConverter.IsAurora"));
    }

    private static XElement[] Layers() => AuroraPanel()
        .Elements(XName.Get("Rectangle", Avalonia))
        .Where(r => (r.Attribute("Name")?.Value ?? "").StartsWith("AuroraLayer", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void TheMeshIsItsOwnModeAndNoLongerAnswersToWallpaper()
    {
        var text = File.ReadAllText(ControlPath());
        text.Should().Contain("StringMatchConverter.IsAurora");
        AuroraPanel().ToString().Should().NotContain("IsWallpaper",
            "Wallpaper is the real desktop wallpaper now (Task 5); the mesh is Aurora");
    }

    [Fact]
    public void ThreeLayersEachHalfAgainAsLargeAsBefore()
    {
        var layers = Layers();
        layers.Should().HaveCount(3);

        foreach (var layer in layers)
        {
            var brush = layer.Descendants(XName.Get("RadialGradientBrush", Avalonia)).Single();
            double.Parse(brush.Attribute("RadiusX")!.Value, CultureInfo.InvariantCulture)
                .Should().BeGreaterOrEqualTo(OldMaxRadiusX * 1.5 * 0.9,
                    "the spec asks for blob radius up by half, measured against the old largest blob");
            double.Parse(brush.Attribute("RadiusY")!.Value, CultureInfo.InvariantCulture)
                .Should().BeGreaterOrEqualTo(OldMaxRadiusY * 1.5 * 0.8);
        }
    }

    [Fact]
    public void PeakOpacitiesReadOnADarkSurfaceAtAGlance()
    {
        var peaks = Layers().Select(l => l.Descendants(XName.Get("Setter", Avalonia))
            .Where(s => s.Attribute("Property")?.Value == "Opacity")
            .Max(s => double.Parse(s.Attribute("Value")!.Value, CultureInfo.InvariantCulture))).ToArray();

        peaks[0].Should().BeGreaterThan(OldPeakOpacityLayer1);
        peaks[1].Should().BeGreaterThan(OldPeakOpacityLayer2);
        peaks[2].Should().BeGreaterThan(OldPeakOpacityLayer3);
        peaks.Should().OnlyContain(p => p <= 1.0);
    }

    [Fact]
    public void ColoursComeFromTheAuroraResourcesAndEndOnTheSurface()
    {
        var stops = AuroraPanel().Descendants(XName.Get("GradientStop", Avalonia)).Select(s => s.Attribute("Color")!.Value).ToArray();

        stops.Should().Contain(s => s.Contains("AuroraPrimary"))
            .And.Contain(s => s.Contains("AuroraSecondary"))
            .And.Contain(s => s.Contains("AuroraTertiary"));
        stops.Where(s => s.Contains("GlassBaseDark")).Should().HaveCount(3,
            "each blob fades to the surface so the glow ends invisibly against the base rectangle");
        stops.Should().NotContain(s => s.Contains("AccentPrimary") || s.Contains("AccentHover") || s.Contains("AccentPressed"),
            "the mesh no longer borrows the chrome accents; it has its own low/high-tone set");
    }

    [Fact]
    public void ReducedMotionFreezesTheMeshAtItsFirstKeyframeInsteadOfHidingIt()
    {
        foreach (var layer in Layers())
        {
            layer.Attribute("IsVisible").Should().BeNull("the layers stay visible under reduced motion; only the animation stops");
            (layer.Attribute("Classes.aurora-animated")?.Value ?? "").Should().Contain("!IsReducedMotion",
                "the animation is gated by a class bound to the inverse of the reduced-motion flag");

            var staticOpacity = double.Parse(layer.Attribute("Opacity")!.Value, CultureInfo.InvariantCulture);
            var firstKeyframe = layer.Descendants(XName.Get("KeyFrame", Avalonia)).First()
                .Elements(XName.Get("Setter", Avalonia)).Single(s => s.Attribute("Property")?.Value == "Opacity");
            double.Parse(firstKeyframe.Attribute("Value")!.Value, CultureInfo.InvariantCulture)
                .Should().Be(staticOpacity, "frozen means 'at the first keyframe', so the static value must equal it");

            layer.Descendants(XName.Get("Style", Avalonia)).Single().Attribute("Selector")!.Value
                .Should().Contain(".aurora-animated", "an ungated style would animate through reduced motion");
        }
    }

    private static string ControlPath() =>
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

(`RepoRoot` is the same walk-up helper `PaletteStudioWiringTests.cs:222` uses — copy its body if
it differs from the above.)

In `DashboardLayoutClobberTests.cs:221` change `.Should().Be("Mica",` to `.Should().Be("Aurora",`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo`
Expected: error — `AuroraColors` does not exist.

- [ ] **Step 3: The colour set, the resources, the converter, the default**

`remex.desktop/Services/DynamicColorGenerator.cs`, after `GenerateTonalRamps` (`:192`):

```csharp
    /// <summary>The three blob colours the Aurora background mesh paints with.</summary>
    public record AuroraSet(Color Primary, Color Secondary, Color Tertiary);

    /// <summary>
    /// Aurora's colours straight off the tonal ramp (spec section 6): the primary, secondary and
    /// tertiary palettes at tone 30 over a dark surface, tone 90 over a light one — the same
    /// tones Material's containers sit at, so the mesh reads as part of the palette rather than
    /// as chrome laid over it. Contrast does not apply: these are raw tones, not role pairs.
    /// </summary>
    public static AuroraSet AuroraColors(Color seed, string variant, bool isLight)
    {
        var ramps = GenerateTonalRamps(seed, variant);
        var tone = isLight ? 90 : 30;
        return new AuroraSet(
            Primary:   ramps.Primary.First(t => t.Tone == tone).Color,
            Secondary: ramps.Secondary.First(t => t.Tone == tone).Color,
            Tertiary:  ramps.Tertiary.First(t => t.Tone == tone).Color);
    }
```

`remex.desktop/Services/ThemeService.cs`, after `:372` (`GlassBaseDarkBrush` override):

```csharp
        // The Aurora mesh's own set, following the SAME light/dark answer the palette does, so
        // System mode flips it with the OS (spec section 6).
        var aurora = DynamicColorGenerator.AuroraColors(accentColor, settings.SchemeVariant, isLightTheme);
        SetResourceOverrideInternal("AuroraPrimary", aurora.Primary);
        SetResourceOverrideInternal("AuroraSecondary", aurora.Secondary);
        SetResourceOverrideInternal("AuroraTertiary", aurora.Tertiary);
```

(`accentColor` is the parsed seed at `:337-345`; confirm the variable name with
`grep -n "accentColor" remex.desktop/Services/ThemeService.cs` before editing.)

`remex.desktop/Themes/Shared/FallbackPalette.axaml`, after `:42`:

```xml
    <!-- Aurora mesh set (RemEx-ddynd): overridden from the seed on every apply; these are the
         tone-30 values of the default seed so the first frame is not transparent. -->
    <Color x:Key="AuroraPrimary">#3F2E9E</Color>
    <Color x:Key="AuroraSecondary">#463F6E</Color>
    <Color x:Key="AuroraTertiary">#6B3A5C</Color>
```

`remex.desktop/Converters/StringMatchConverter.cs`, after `IsWallpaper` (`:34`):

```csharp
    /// <summary>Returns true when the bound string equals "Aurora".</summary>
    public static readonly IValueConverter IsAurora =
        new StringEqualsConverter("Aurora");
```

`remex.core/Models/DashboardProfile.cs:276-278`: default `"Mica"` → `"Aurora"`; summary:
`/// <summary>Requested background treatment: Aurora (default), Wallpaper, Acrylic, Glass, Gradient or Solid. The desktop resolves anything else at load.</summary>`.

`remex.desktop/ViewModels/CustomizationViewModel.cs:636-657` becomes:

```csharp
    private void RefreshBackgroundTypes()
    {
        AvailableBackgroundTypes.Clear();
        AvailableBackgroundTypes.Add("Aurora");
        AvailableBackgroundTypes.Add("Wallpaper");
        if (OperatingSystem.IsWindows())
        {
            AvailableBackgroundTypes.Add("Mica");
            AvailableBackgroundTypes.Add("Acrylic");
        }
        else if (OperatingSystem.IsLinux())
        {
            AvailableBackgroundTypes.Add("Glass");
        }
        AvailableBackgroundTypes.Add("Gradient");
        AvailableBackgroundTypes.Add("Solid");

        // A mode this platform cannot offer resolves to the default for the session.
        if (!AvailableBackgroundTypes.Contains(CanvasBackgroundType))
            CanvasBackgroundType = "Aurora";
    }
```

(Mica stays in the list until Task 6 removes it.)

- [ ] **Step 4: Rebuild the mesh panel**

In `remex.desktop/Controls/DashboardBackgroundControl.axaml`, replace `:113-219` (the comment
"═══ Wallpaper Mode — Animated Aurora Mesh ═══" through the closing `</Panel>`) with the panel
below. Changes against the old markup: the panel binds `IsAurora`; each layer's `IsVisible`
multibinding is gone and replaced by a static `Opacity` equal to its first keyframe plus
`Classes.aurora-animated="{Binding !IsReducedMotion}"`; each `Style` selector gains
`.aurora-animated`; radii are the old values × 1.5; peak opacities rise; the three stops read
`AuroraPrimary/Secondary/Tertiary`. Loop durations (8/12/15 s), centres and easing are unchanged.

```xml
                    <!-- ═══ Aurora Mode — the animated mesh (RemEx-ddynd) ═══
                         Three radial blobs, each two DynamicResource stops: an Aurora tone at
                         Offset 0 (tone 30 of its ramp on a dark surface, tone 90 on a light one —
                         ThemeService picks the set with the same ResolveIsLight answer the palette
                         uses) and the Surface base (GlassBaseDark, the same brush as the base
                         rectangle) at Offset 1, so the glow fades to nothing against it.

                         REDUCED MOTION FREEZES, IT DOES NOT HIDE. Each layer's static Opacity IS
                         its first keyframe; the animation lives on a class bound to the inverse of
                         IsReducedMotion, so removing the class stops the animation and the property
                         falls back to that static value. AuroraMeshTests pins the pairing. -->
                    <Panel IsVisible="{Binding Customization.CanvasBackgroundType, Converter={x:Static converters:StringMatchConverter.IsAurora}}">
                        <Rectangle Classes="palette-bg" Fill="{DynamicResource GlassBaseDarkBrush}" />

                        <Rectangle Name="AuroraLayer1" Opacity="0.55" Classes.aurora-animated="{Binding !IsReducedMotion}">
                            <Rectangle.Fill>
                                <RadialGradientBrush Center="25%,35%" RadiusX="0.98" RadiusY="1.13">
                                    <GradientStop Offset="0" Color="{DynamicResource AuroraPrimary}" />
                                    <GradientStop Offset="1" Color="{DynamicResource GlassBaseDark}" />
                                </RadialGradientBrush>
                            </Rectangle.Fill>
                            <Rectangle.Styles>
                                <Style Selector="Rectangle#AuroraLayer1.aurora-animated">
                                    <Style.Animations>
                                        <Animation Duration="0:0:8" IterationCount="Infinite" PlaybackDirection="Alternate" Easing="SineEaseInOut">
                                            <KeyFrame KeyTime="0:0:0"><Setter Property="Opacity" Value="0.55"/></KeyFrame>
                                            <KeyFrame KeyTime="0:0:4"><Setter Property="Opacity" Value="0.90"/></KeyFrame>
                                            <KeyFrame KeyTime="0:0:8"><Setter Property="Opacity" Value="0.60"/></KeyFrame>
                                        </Animation>
                                    </Style.Animations>
                                </Style>
                            </Rectangle.Styles>
                        </Rectangle>

                        <Rectangle Name="AuroraLayer2" Opacity="0.42" Classes.aurora-animated="{Binding !IsReducedMotion}">
                            <Rectangle.Fill>
                                <RadialGradientBrush Center="72%,65%" RadiusX="1.05" RadiusY="0.90">
                                    <GradientStop Offset="0" Color="{DynamicResource AuroraSecondary}" />
                                    <GradientStop Offset="1" Color="{DynamicResource GlassBaseDark}" />
                                </RadialGradientBrush>
                            </Rectangle.Fill>
                            <Rectangle.Styles>
                                <Style Selector="Rectangle#AuroraLayer2.aurora-animated">
                                    <Style.Animations>
                                        <Animation Duration="0:0:12" IterationCount="Infinite" PlaybackDirection="Alternate" Easing="SineEaseInOut">
                                            <KeyFrame KeyTime="0:0:0"><Setter Property="Opacity" Value="0.42"/></KeyFrame>
                                            <KeyFrame KeyTime="0:0:6"><Setter Property="Opacity" Value="0.80"/></KeyFrame>
                                            <KeyFrame KeyTime="0:0:12"><Setter Property="Opacity" Value="0.35"/></KeyFrame>
                                        </Animation>
                                    </Style.Animations>
                                </Style>
                            </Rectangle.Styles>
                        </Rectangle>

                        <Rectangle Name="AuroraLayer3" Opacity="0.30" Classes.aurora-animated="{Binding !IsReducedMotion}">
                            <Rectangle.Fill>
                                <RadialGradientBrush Center="50%,8%" RadiusX="0.83" RadiusY="0.68">
                                    <GradientStop Offset="0" Color="{DynamicResource AuroraTertiary}" />
                                    <GradientStop Offset="1" Color="{DynamicResource GlassBaseDark}" />
                                </RadialGradientBrush>
                            </Rectangle.Fill>
                            <Rectangle.Styles>
                                <Style Selector="Rectangle#AuroraLayer3.aurora-animated">
                                    <Style.Animations>
                                        <Animation Duration="0:0:15" IterationCount="Infinite" PlaybackDirection="Alternate" Easing="SineEaseInOut">
                                            <KeyFrame KeyTime="0:0:0"><Setter Property="Opacity" Value="0.30"/></KeyFrame>
                                            <KeyFrame KeyTime="0:0:15"><Setter Property="Opacity" Value="0.65"/></KeyFrame>
                                        </Animation>
                                    </Style.Animations>
                                </Style>
                            </Rectangle.Styles>
                        </Rectangle>
                    </Panel>
```

`DashboardBackgroundPaletteTests` (`EveryGradientStop_UsesADynamicResourceColor`,
`EveryAnimation_EasesWithSineEaseInOut`, `NoColorOrFillAttribute_IsAHexLiteral`) keep passing:
every stop is a `DynamicResource`, every animation is `SineEaseInOut`, no literal hex.

- [ ] **Step 5: Add the resx key**

Write a scratch `task4-keys.json`:

```json
{
  "Custom_BgType_Aurora": {"en": "Aurora", "es": "Aurora boreal", "fr": "Aurore", "hi": "ऑरोरा", "id": "Cahaya aurora", "pl": "Zorza", "pt-BR": "Aurora boreal", "tr": "Aurora ışığı", "uk": "Сяйво"}
}
```

Run: `uv run python scripts/resx_add_keys.py <path>` → nine `+1` lines.

- [ ] **Step 6: Run the tests**

Run:
```
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~AuroraColorsTests|FullyQualifiedName~AuroraMeshTests|FullyQualifiedName~DashboardBackgroundPaletteTests|FullyQualifiedName~DashboardBackdropTintTests|FullyQualifiedName~ThemeKeyCoverageTests|FullyQualifiedName~DashboardLayoutClobberTests"
pwsh scripts/check-localization.ps1
```
Expected: 0 warnings; all pass (count +9); `errors=0`, warning count unchanged.

- [ ] **Step 7: Eyes pass**

`pwsh scripts/ui-hotreload.ps1 -Start`; open the drawer with the gear FAB; set Background Mode to
Aurora; `pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree`. Switch Base mode Light, snapshot again.
Toggle Reduced motion on: the blobs must stay visible and stop moving. Expected: three clearly
coloured blobs on both modes. `pwsh scripts/ui-hotreload.ps1 -Stop`.

- [ ] **Step 8: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add remex.desktop/Converters/StringMatchConverter.cs remex.desktop/Services/DynamicColorGenerator.cs remex.desktop/Services/ThemeService.cs remex.desktop/Themes/Shared/FallbackPalette.axaml remex.desktop/Controls/DashboardBackgroundControl.axaml remex.desktop/ViewModels/CustomizationViewModel.cs remex.core/Models/DashboardProfile.cs remex.desktop.tests/Services/DashboardLayoutClobberTests.cs remex.desktop.tests/Controls/AuroraMeshTests.cs remex.desktop.tests/Services/AuroraColorsTests.cs remex.desktop/Localization/Strings.resx remex.desktop/Localization/Strings.es.resx remex.desktop/Localization/Strings.fr.resx remex.desktop/Localization/Strings.hi.resx remex.desktop/Localization/Strings.id.resx remex.desktop/Localization/Strings.pl.resx remex.desktop/Localization/Strings.pt-BR.resx remex.desktop/Localization/Strings.tr.resx remex.desktop/Localization/Strings.uk.resx
git commit -m "feat(desktop): Aurora background mode — bolder mesh painted from the ramp, frozen under reduced motion (RemEx-<bead>)"
```

---

### Task 5: Wallpaper mode — the real wallpaper, blurred, behind a surface veil

**Files:**
- Create: `remex.desktop/Services/WallpaperBackdrop.cs`, `remex.desktop/Services/WallpaperImageStore.cs`, `remex.desktop/Converters/BlurRadiusToEffectConverter.cs`
- Modify: `remex.desktop/Services/SystemSeedSources.cs:150` (`TryGetWallpaperPath` → `internal static`)
- Modify: `remex.desktop/ViewModels/ShellViewModel.cs:368-369` (new backdrop properties beside `Customization`), `:379-388` (refresh the backdrop from the applied handler and the ctor)
- Modify: `remex.desktop/Controls/DashboardBackgroundControl.axaml` (every mode panel binds `EffectiveBackgroundType`; new Wallpaper panel)
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs` (wallpaper source/blur/pick state; `ApplyAndSave`; `IsWindowOpacityRelevant`)
- Modify: `remex.desktop/Views/PersonalizationPanelView.axaml:316-339` (wallpaper block under the background picker; window-opacity visibility)
- Modify: nine `Strings*.resx` (seven keys)
- Test: `remex.desktop.tests/Services/WallpaperBackdropTests.cs`, `remex.desktop.tests/Services/WallpaperImageStoreTests.cs`, `remex.desktop.tests/Controls/WallpaperPanelTests.cs`

**Interfaces:**
- Consumes: `WallpaperSources`, `CustomizationSettings.WallpaperSource/WallpaperImagePath/WallpaperBlur` (Task 1), `RemexDataPaths.PerUserDirectory` (`remex.core/Services/RemexDataPaths.cs:121`), `NotificationService.Instance.Notify(NotificationImportance.Outcome, title, message)` (the existing snackbar path, `CustomizationViewModel.cs:407-410`), `PickOpenFileAsync` seam (`CustomizationViewModel.cs:372`), `ShellViewModel.Customization` (`:368`).
- Produces:
  - `WallpaperBackdrop` (static): `const double MaxBlurRadius = 48.0`; `static double BlurRadiusFor(double blur)`; `static string? ResolvePath(CustomizationSettings settings, Func<string?> desktopWallpaperPath)`.
  - `WallpaperImageStore` (static): `const int MaxEdge = 2560`; `static string DirectoryFor(string perUserRoot)` (`<root>\wallpapers`); `static bool TryCopyDownscaled(string sourcePath, string directory, out string? copyPath)`.
  - `BlurRadiusToEffectConverter.Instance` (`double` → `BlurEffect`).
  - `ShellViewModel`: `Bitmap? WallpaperBitmap`, `double WallpaperBlurRadius`, `string EffectiveBackgroundType` (all observable).
  - `CustomizationViewModel`: `string WallpaperSource`, `ObservableCollection<string> AvailableWallpaperSources`, `double WallpaperBlur`, `bool IsWallpaperBackgroundSelected`, `bool IsWindowOpacityRelevant`, `bool IsImageWallpaperSource`, `PickWallpaperImageCommand`.
  - Resx: `Custom_WallpaperSource`, `Custom_WallpaperSource_Desktop`, `Custom_WallpaperSource_Image`, `Custom_ChooseWallpaperImage`, `Custom_WallpaperBlur`, `Custom_WallpaperUnavailable`, `Custom_WallpaperImageCopyFailed`.

- [ ] **Step 1: Write the failing tests**

Create `remex.desktop.tests/Services/WallpaperBackdropTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

public class WallpaperBackdropTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.6, 28.8)]
    [InlineData(1.0, 48.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(3.0, 48.0)]
    [InlineData(double.NaN, 0.0)]
    public void BlurMapsZeroToOneOntoZeroToFortyEightPixelsAndClamps(double blur, double expected)
    {
        WallpaperBackdrop.BlurRadiusFor(blur).Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void DesktopSourceAsksTheDesktopForItsPath()
    {
        var settings = new CustomizationSettings { WallpaperSource = WallpaperSources.Desktop, WallpaperImagePath = @"C:\ignored.png" };

        WallpaperBackdrop.ResolvePath(settings, () => @"C:\wall.jpg").Should().Be(@"C:\wall.jpg");
        WallpaperBackdrop.ResolvePath(settings, () => null).Should().BeNull("no wallpaper set is 'no answer', never a throw");
    }

    [Fact]
    public void ImageSourceUsesTheAppOwnedCopyOnlyWhileItExists()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"remex-wp-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(existing, new byte[] { 1, 2, 3 });
        try
        {
            var settings = new CustomizationSettings { WallpaperSource = WallpaperSources.Image, WallpaperImagePath = existing };
            WallpaperBackdrop.ResolvePath(settings, () => @"C:\wall.jpg").Should().Be(existing);

            var gone = settings with { WallpaperImagePath = existing + ".missing" };
            WallpaperBackdrop.ResolvePath(gone, () => @"C:\wall.jpg").Should().BeNull(
                "a missing copy falls back to Solid for the session (spec section 6), not to the desktop wallpaper");
            WallpaperBackdrop.ResolvePath(settings with { WallpaperImagePath = null }, () => @"C:\wall.jpg").Should().BeNull();
        }
        finally
        {
            File.Delete(existing);
        }
    }
}
```

Create `remex.desktop.tests/Services/WallpaperImageStoreTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using Remex.Desktop.Services;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>Copying a picked image under the app's own directory, downscaled to 2560 px (spec section 6).</summary>
public class WallpaperImageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"remex-wpstore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string WriteImage(int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"remex-wpsrc-{Guid.NewGuid():N}.png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(0x20, 0x60, 0xA0));
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void ALargeImageIsCopiedWithItsLongestEdgeAtMostTwentyFiveSixty()
    {
        var source = WriteImage(4000, 2000);
        try
        {
            WallpaperImageStore.TryCopyDownscaled(source, _dir, out var copy).Should().BeTrue();

            copy.Should().StartWith(_dir).And.NotBe(source, "the app stores its own copy, never the original's path");
            using var decoded = SKBitmap.Decode(copy!);
            decoded.Width.Should().Be(2560);
            decoded.Height.Should().Be(1280);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void ASmallImageIsCopiedWithoutUpscaling()
    {
        var source = WriteImage(640, 480);
        try
        {
            WallpaperImageStore.TryCopyDownscaled(source, _dir, out var copy).Should().BeTrue();
            using var decoded = SKBitmap.Decode(copy!);
            (decoded.Width, decoded.Height).Should().Be((640, 480));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void AnUnreadableFileFailsWithoutThrowingAndWritesNothing()
    {
        var source = Path.Combine(Path.GetTempPath(), $"remex-wpsrc-{Guid.NewGuid():N}.png");
        File.WriteAllText(source, "not an image");
        try
        {
            WallpaperImageStore.TryCopyDownscaled(source, _dir, out var copy).Should().BeFalse();
            copy.Should().BeNull();
            (Directory.Exists(_dir) ? Directory.GetFiles(_dir).Length : 0).Should().Be(0);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void AMissingFileFailsWithoutThrowing()
    {
        WallpaperImageStore.TryCopyDownscaled(Path.Combine(_dir, "nope.png"), _dir, out var copy).Should().BeFalse();
        copy.Should().BeNull();
    }

    [Fact]
    public void TheDirectoryIsAWallpapersFolderUnderThePerUserRoot()
    {
        WallpaperImageStore.DirectoryFor(@"C:\Users\x\AppData\Local\RemEx")
            .Should().Be(Path.Combine(@"C:\Users\x\AppData\Local\RemEx", "wallpapers"));
    }
}
```

Create `remex.desktop.tests/Controls/WallpaperPanelTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>The Wallpaper panel draws the bitmap, blurs it once, and veils it with the surface (spec section 6).</summary>
public class WallpaperPanelTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    private static XElement WallpaperPanel() => XDocument.Load(ControlPath())
        .Descendants(XName.Get("Panel", Avalonia))
        .Single(p => (p.Attribute("IsVisible")?.Value ?? "").Contains("StringMatchConverter.IsWallpaper"));

    [Fact]
    public void EveryModePanelFollowsTheEffectiveTypeSoAFailedWallpaperCanFallBackToSolid()
    {
        var panels = XDocument.Load(ControlPath()).Descendants()
            .Where(e => (e.Attribute("IsVisible")?.Value ?? "").Contains("StringMatchConverter.Is")).ToArray();

        panels.Should().NotBeEmpty();
        panels.Select(p => p.Attribute("IsVisible")!.Value)
            .Should().OnlyContain(v => v.Contains("{Binding EffectiveBackgroundType,"),
                "Customization.CanvasBackgroundType is the SETTING; EffectiveBackgroundType is what renders this session");
    }

    [Fact]
    public void TheImageIsBlurredThroughTheConverterAndVeiledAtTheWindowOpacity()
    {
        var panel = WallpaperPanel();
        var image = panel.Elements(XName.Get("Image", Avalonia)).Single();

        image.Attribute("Source")!.Value.Should().Be("{Binding WallpaperBitmap}");
        image.Attribute("Stretch")!.Value.Should().Be("UniformToFill", "stretched to fill");
        image.Attribute("Effect")!.Value.Should().Contain("WallpaperBlurRadius").And.Contain("BlurRadiusToEffectConverter");

        var veil = panel.Elements(XName.Get("Rectangle", Avalonia))
            .Single(r => (r.Attribute("Fill")?.Value ?? "").Contains("GlassBaseDarkBrush"));
        veil.Attribute("Opacity")!.Value.Should().Be("{Binding Customization.AppWindowOpacity}",
            "the surface sits over the image at the window opacity so text keeps its contrast");
        panel.Descendants(XName.Get("Animation", Avalonia)).Should().BeEmpty(
            "nothing animates in this panel, so the blurred bitmap is not re-rendered per frame");
    }

    private static string ControlPath() =>
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo`
Expected: errors — `WallpaperBackdrop`, `WallpaperImageStore` do not exist.

- [ ] **Step 3: The pure services and the converter**

Create `remex.desktop/Services/WallpaperBackdrop.cs`:

```csharp
using System;
using System.IO;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>The Wallpaper background mode's pure decisions (RemEx-ddynd): blur mapping and which file to draw.</summary>
public static class WallpaperBackdrop
{
    /// <summary>Blur 1.0 in device pixels. 48 is where a 4K wallpaper becomes colour fields rather than a picture.</summary>
    public const double MaxBlurRadius = 48.0;

    /// <summary><c>WallpaperBlur</c> (0–1) to a blur radius (0–48 px). NaN and out-of-range clamp.</summary>
    public static double BlurRadiusFor(double blur)
    {
        if (double.IsNaN(blur)) return 0.0;
        return Math.Clamp(blur, 0.0, 1.0) * MaxBlurRadius;
    }

    /// <summary>
    /// The file to draw: the desktop's own wallpaper, or the app-owned copy — and null when there is
    /// nothing to draw, which the caller renders as Solid for the session without touching the
    /// setting. An Image source whose copy is gone does NOT fall through to the desktop wallpaper:
    /// showing a different picture than the one the person picked is a silent substitution.
    /// </summary>
    public static string? ResolvePath(CustomizationSettings settings, Func<string?> desktopWallpaperPath)
    {
        if (string.Equals(settings.WallpaperSource, WallpaperSources.Image, StringComparison.Ordinal))
        {
            var copy = settings.WallpaperImagePath;
            return !string.IsNullOrWhiteSpace(copy) && File.Exists(copy) ? copy : null;
        }

        try
        {
            return desktopWallpaperPath();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

Create `remex.desktop/Services/WallpaperImageStore.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Remex.Desktop.Services;

/// <summary>
/// Copies a picked wallpaper image under the per-user data directory, downscaled so the longest
/// edge is at most <see cref="MaxEdge"/> pixels (spec section 6). The profile stores the COPY's
/// path, never the original's: the original can move, and the copy is sized for the window.
/// </summary>
/// <remarks>EVERY PATH DEGRADES TO FALSE, NEVER TO A THROW — the same rule SystemSeedSources
/// follows; the caller raises the snackbar and keeps the previous image.</remarks>
public static class WallpaperImageStore
{
    public const int MaxEdge = 2560;

    /// <summary>The folder under the same root the profile file uses.</summary>
    public static string DirectoryFor(string perUserRoot) => Path.Combine(perUserRoot, "wallpapers");

    public static bool TryCopyDownscaled(string sourcePath, string directory, out string? copyPath)
    {
        copyPath = null;
        try
        {
            if (!File.Exists(sourcePath)) return false;

            using var decoded = SKBitmap.Decode(sourcePath);
            if (decoded is null) return false;

            var scale = Math.Min(1.0, MaxEdge / (double)Math.Max(decoded.Width, decoded.Height));
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));

            using var sized = scale < 1.0
                ? decoded.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default) ?? decoded.Copy()
                : decoded.Copy();
            using var image = SKImage.FromBitmap(sized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) return false;

            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, $"wallpaper-{Guid.NewGuid():N}.png");
            var temp = target + ".tmp";
            using (var stream = File.Create(temp)) data.SaveTo(stream);
            File.Move(temp, target, overwrite: true);

            copyPath = target;
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WallpaperImageStore: could not copy '{sourcePath}' — {ex.Message}");
            return false;
        }
    }

    /// <summary>Best-effort removal of a superseded copy. Only files this store wrote are touched.</summary>
    public static void TryDeleteCopy(string? copyPath, string directory)
    {
        if (string.IsNullOrWhiteSpace(copyPath)) return;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(copyPath)) == Path.GetFullPath(directory) && File.Exists(copyPath))
                File.Delete(copyPath);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"WallpaperImageStore: could not delete '{copyPath}' — {ex.Message}");
        }
    }
}
```

Create `remex.desktop/Converters/BlurRadiusToEffectConverter.cs`:

```csharp
using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Remex.Desktop.Converters;

/// <summary>A blur radius in device pixels to the <see cref="BlurEffect"/> the wallpaper Image carries.
/// A radius of 0 yields null (no effect at all) so an unblurred wallpaper costs nothing.</summary>
public sealed class BlurRadiusToEffectConverter : IValueConverter
{
    public static readonly BlurRadiusToEffectConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value is double d && !double.IsNaN(d) ? Math.Max(0.0, d) : 0.0;
        return radius <= 0.0 ? null : new BlurEffect { Radius = radius };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

`remex.desktop/Services/SystemSeedSources.cs:150`: `private static string? TryGetWallpaperPath()`
→ `internal static string? TryGetWallpaperPath()` (the assembly already has
`InternalsVisibleTo("Remex.Desktop.Tests")`, `Remex.Desktop.csproj:56`).

- [ ] **Step 4: The shell's backdrop state**

In `remex.desktop/ViewModels/ShellViewModel.cs`, after `_customization` (`:368-369`):

```csharp
    /// <summary>The decoded wallpaper for the Wallpaper background mode, or null. Decoded once per
    /// path change on a worker thread and cached here; the blur is an Effect on the Image, so
    /// nothing re-renders per frame (spec section 9).</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _wallpaperBitmap;

    /// <summary><c>WallpaperBlur</c> mapped through <see cref="WallpaperBackdrop.BlurRadiusFor"/>.</summary>
    [ObservableProperty]
    private double _wallpaperBlurRadius;

    /// <summary>
    /// What the background control renders THIS SESSION: the setting, except that a Wallpaper
    /// mode whose file cannot be read renders Solid while the setting stays Wallpaper so the
    /// person can pick again (spec section 6).
    /// </summary>
    [ObservableProperty]
    private string _effectiveBackgroundType = "Aurora";

    private string? _wallpaperPathLoaded;
    private string? _wallpaperPathFailed;

    /// <summary>Re-resolves the wallpaper for <paramref name="settings"/>. UI thread.</summary>
    private void RefreshWallpaperBackdrop(Remex.Core.Models.CustomizationSettings settings)
    {
        WallpaperBlurRadius = WallpaperBackdrop.BlurRadiusFor(settings.WallpaperBlur);

        if (settings.BackgroundMaterial != "Wallpaper")
        {
            EffectiveBackgroundType = settings.BackgroundMaterial;
            return;
        }

        var path = WallpaperBackdrop.ResolvePath(settings, SystemSeedSources.TryGetWallpaperPath);
        if (path is null)
        {
            FailWallpaper(settings, path: null);
            return;
        }

        if (string.Equals(path, _wallpaperPathLoaded, StringComparison.OrdinalIgnoreCase) && WallpaperBitmap is not null)
        {
            EffectiveBackgroundType = "Wallpaper";
            return;
        }

        _ = LoadWallpaperAsync(settings, path);
    }

    private async Task LoadWallpaperAsync(Remex.Core.Models.CustomizationSettings settings, string path)
    {
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var codec = SkiaSharp.SKCodec.Create(path);
                if (codec is null) return null;
                using var stream = File.OpenRead(path);
                // A 4K desktop wallpaper is decoded at most 2560 wide; a picked image is already that size.
                return codec.Info.Width > WallpaperImageStore.MaxEdge
                    ? Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, WallpaperImageStore.MaxEdge)
                    : new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Wallpaper backdrop: could not decode '{path}' — {ex.Message}");
                return null;
            }
        });

        // The setting may have moved on while decoding; only the current one is honoured.
        if (Customization.BackgroundMaterial != "Wallpaper") { bitmap?.Dispose(); return; }

        if (bitmap is null)
        {
            FailWallpaper(settings, path);
            return;
        }

        var previous = WallpaperBitmap;
        WallpaperBitmap = bitmap;
        _wallpaperPathLoaded = path;
        _wallpaperPathFailed = null;
        EffectiveBackgroundType = "Wallpaper";
        previous?.Dispose();
    }

    /// <summary>Solid for the session, one snackbar per failing path, the setting untouched.</summary>
    private void FailWallpaper(Remex.Core.Models.CustomizationSettings settings, string? path)
    {
        EffectiveBackgroundType = "Solid";
        var key = path ?? $"{settings.WallpaperSource}|{settings.WallpaperImagePath}";
        if (string.Equals(key, _wallpaperPathFailed, StringComparison.OrdinalIgnoreCase)) return;
        _wallpaperPathFailed = key;
        NotificationService.Instance.Notify(
            NotificationImportance.Outcome,
            LocalizationService.Instance["Custom_BgType_Wallpaper"],
            LocalizationService.Instance["Custom_WallpaperUnavailable"]);
    }
```

In the constructor's `_onCustomizationApplied` lambda (`:379-383`) add `RefreshWallpaperBackdrop(settings);`
after `Customization = settings;`, and after the initial `Customization = _layoutService.CurrentProfile.Customization;`
(`:387`) add `RefreshWallpaperBackdrop(Customization);`. `CustomizationApplied` is raised inside a
posted UI-thread lambda (`ThemeService.cs:565`), so no extra dispatch is needed. Add
`using System.IO;` and `using System.Threading.Tasks;` if missing.

- [ ] **Step 5: The control**

In `remex.desktop/Controls/DashboardBackgroundControl.axaml`, change every mode `IsVisible`
binding from `{Binding Customization.CanvasBackgroundType, Converter=…}` to
`{Binding EffectiveBackgroundType, Converter=…}` — the gradient base (`:88`), the gradient
animated rectangle's `<Binding Path="Customization.CanvasBackgroundType" …/>` (`:93` →
`Path="EffectiveBackgroundType"`), the Aurora panel (Task 4), Acrylic (`:236`), Mica (`:248`),
Glass (`:258`), Solid (`:266`). Then insert the Wallpaper panel directly after the Aurora panel's
closing `</Panel>`:

```xml
                    <!-- ═══ Wallpaper Mode — the real desktop wallpaper or a picked image (RemEx-ddynd) ═══
                         The bitmap is decoded once per path change (ShellViewModel) and blurred by an
                         Effect on the Image — nothing here animates, so the blur is not re-rendered per
                         frame. The surface veil sits over it at the WINDOW opacity, the same knob Glass
                         mode uses for "how much shows through", so text keeps its contrast. -->
                    <Panel IsVisible="{Binding EffectiveBackgroundType, Converter={x:Static converters:StringMatchConverter.IsWallpaper}}">
                        <Rectangle Classes="palette-bg" Fill="{DynamicResource GlassBaseDarkBrush}" />
                        <Image Source="{Binding WallpaperBitmap}" Stretch="UniformToFill"
                               Effect="{Binding WallpaperBlurRadius, Converter={x:Static converters:BlurRadiusToEffectConverter.Instance}}" />
                        <Rectangle Classes="palette-bg" Fill="{DynamicResource GlassBaseDarkBrush}" Opacity="{Binding Customization.AppWindowOpacity}" />
                    </Panel>
```

Update the comment at `:221-233` ("TINTS OVER OS BACKDROPS…") to mention Acrylic and Glass only.
`MainWindow.axaml.cs:119-131` already routes Wallpaper to the opaque, non-transparent branch;
nothing changes there.

- [ ] **Step 6: The view model and the (pre-reflow) view**

`remex.desktop/ViewModels/CustomizationViewModel.cs`, after `_canvasBackgroundType` (`:701-702`):

```csharp
    /// <summary>A <see cref="WallpaperSources"/> value. Persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageWallpaperSource))]
    private string _wallpaperSource = WallpaperSources.Desktop;

    /// <summary>Desktop wallpaper only where the registry can be read (Windows); Pick an image everywhere.</summary>
    public ObservableCollection<string> AvailableWallpaperSources { get; } = new();

    public bool IsImageWallpaperSource => WallpaperSource == WallpaperSources.Image;

    /// <summary>0 to 1; the shell maps it to a blur radius. Persisted.</summary>
    [ObservableProperty]
    private double _wallpaperBlur = 0.6;

    /// <summary>The app-owned copy's path, carried and replaced by <see cref="PickWallpaperImageAsync"/>.</summary>
    private string? _wallpaperImagePath;

    public bool IsWallpaperBackgroundSelected => CanvasBackgroundType == "Wallpaper";

    /// <summary>Glass and Wallpaper are the two modes the window-opacity slider shapes.</summary>
    public bool IsWindowOpacityRelevant => CanvasBackgroundType is "Glass" or "Wallpaper";

    partial void OnWallpaperSourceChanged(string value) => ApplyAndSave();

    partial void OnWallpaperBlurChanged(double value) => ApplyAndSave();

    /// <summary>Pick an image: copy it under the per-user directory, downscaled; on failure keep the
    /// previous image, say so, and write nothing (spec section 9).</summary>
    [RelayCommand]
    private async Task PickWallpaperImageAsync()
    {
        if (PickOpenFileAsync is null) return;

        var files = await PickOpenFileAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Custom_ChooseWallpaperImage"],
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        if (files.Count == 0) return;

        var source = files[0].TryGetLocalPath();
        if (source is null) return;

        var directory = WallpaperImageStore.DirectoryFor(RemexDataPaths.PerUserDirectory);
        var (ok, copy) = await Task.Run(() =>
        {
            var success = WallpaperImageStore.TryCopyDownscaled(source, directory, out var path);
            return (success, path);
        });

        if (!ok || copy is null)
        {
            NotificationService.Instance.Notify(
                NotificationImportance.Outcome,
                LocalizationService.Instance["Custom_ChooseWallpaperImage"],
                LocalizationService.Instance["Custom_WallpaperImageCopyFailed"]);
            return;
        }

        var previous = _wallpaperImagePath;
        _wallpaperImagePath = copy;
        WallpaperSource = WallpaperSources.Image;   // saves through OnWallpaperSourceChanged
        if (WallpaperSource == WallpaperSources.Image && previous != copy) ApplyAndSave(); // the setter is a no-op when already Image
        WallpaperImageStore.TryDeleteCopy(previous, directory);
    }
```

(`using Remex.Core.Services;` for `RemexDataPaths` — confirm the namespace with
`grep -n "^namespace" remex.core/Services/RemexDataPaths.cs`. `FilePickerFileTypes` and
`TryGetLocalPath` are in `Avalonia.Platform.Storage`, already imported at `:8`.)

Constructor (`:534-547`): add `_wallpaperSource = settings.WallpaperSource; _wallpaperBlur = Math.Clamp(settings.WallpaperBlur, 0.0, 1.0); _wallpaperImagePath = settings.WallpaperImagePath;`
and populate `AvailableWallpaperSources`: `if (OperatingSystem.IsWindows()) AvailableWallpaperSources.Add(WallpaperSources.Desktop); AvailableWallpaperSources.Add(WallpaperSources.Image);`
then `if (!AvailableWallpaperSources.Contains(_wallpaperSource)) _wallpaperSource = WallpaperSources.Image;`.

`OnCanvasBackgroundTypeChanged` (`:829-833`): also raise `OnPropertyChanged(nameof(IsWallpaperBackgroundSelected));`
and `OnPropertyChanged(nameof(IsWindowOpacityRelevant));`.

`ApplyAndSave` (`:905-943`): replace the three carried lines with
`WallpaperSource = WallpaperSource,`, `WallpaperImagePath = _wallpaperImagePath,`, `WallpaperBlur = Math.Clamp(WallpaperBlur, 0.0, 1.0),`.

`remex.desktop/Views/PersonalizationPanelView.axaml`: change `:333` `IsVisible="{Binding IsGlassModeSelected}"`
to `IsVisible="{Binding IsWindowOpacityRelevant}"`, and insert after the background-mode `</Grid>`
(`:332`):

```xml
                <!-- Wallpaper mode's own controls (RemEx-ddynd): where the picture comes from, and how
                     blurred it is. Same PrefixedLabelConverter pattern as the mode picker above. -->
                <StackPanel Spacing="10" IsVisible="{Binding IsWallpaperBackgroundSelected}">
                    <Grid ColumnDefinitions="*,2*">
                        <TextBlock Text="{local:Localize Custom_WallpaperSource}" VerticalAlignment="Center"/>
                        <ComboBox Grid.Column="1" SelectedItem="{Binding WallpaperSource}" ItemsSource="{Binding AvailableWallpaperSources}" HorizontalAlignment="Left" Width="160" AutomationProperties.Name="{local:Localize Custom_WallpaperSource}">
                            <ComboBox.ItemTemplate>
                                <DataTemplate x:CompileBindings="False">
                                    <TextBlock>
                                        <TextBlock.Text>
                                            <MultiBinding Converter="{x:Static local:PrefixedLabelConverter.Instance}" ConverterParameter="Custom_WallpaperSource_">
                                                <Binding Path="CultureTag" Source="{x:Static svc:LocalizationService.Instance}"/>
                                                <Binding/>
                                            </MultiBinding>
                                        </TextBlock.Text>
                                    </TextBlock>
                                </DataTemplate>
                            </ComboBox.ItemTemplate>
                        </ComboBox>
                    </Grid>
                    <Button Classes="compact" Content="{local:Localize Custom_ChooseWallpaperImage}" Command="{Binding PickWallpaperImageCommand}" IsVisible="{Binding IsImageWallpaperSource}" AutomationProperties.Name="{local:Localize Custom_ChooseWallpaperImage}"/>
                    <Grid ColumnDefinitions="*,2*">
                        <TextBlock Text="{local:Localize Custom_WallpaperBlur}" VerticalAlignment="Center"/>
                        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="12">
                            <Slider Minimum="0" Maximum="1" SmallChange="0.05" LargeChange="0.2" Value="{Binding WallpaperBlur}" Width="160" VerticalAlignment="Center" AutomationProperties.Name="{local:Localize Custom_WallpaperBlur}"/>
                            <TextBlock Text="{Binding WallpaperBlur, StringFormat={}{0:P0}}" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondaryBrush}"/>
                        </StackPanel>
                    </Grid>
                </StackPanel>
```

- [ ] **Step 7: Add the resx keys**

Scratch `task5-keys.json`:

```json
{
  "Custom_WallpaperSource": {"en": "Wallpaper source", "es": "Origen del fondo", "fr": "Source du fond d'écran", "hi": "वॉलपेपर स्रोत", "id": "Sumber wallpaper", "pl": "Źródło tapety", "pt-BR": "Origem do papel de parede", "tr": "Duvar kağıdı kaynağı", "uk": "Джерело шпалер"},
  "Custom_WallpaperSource_Desktop": {"en": "Desktop wallpaper", "es": "Fondo del escritorio", "fr": "Fond d'écran du bureau", "hi": "डेस्कटॉप वॉलपेपर", "id": "Wallpaper desktop", "pl": "Tapeta pulpitu", "pt-BR": "Papel de parede da área de trabalho", "tr": "Masaüstü duvar kağıdı", "uk": "Шпалери робочого столу"},
  "Custom_WallpaperSource_Image": {"en": "Pick an image", "es": "Elegir una imagen", "fr": "Choisir une image", "hi": "एक छवि चुनें", "id": "Pilih gambar", "pl": "Wybierz obraz", "pt-BR": "Escolher uma imagem", "tr": "Bir görsel seç", "uk": "Вибрати зображення"},
  "Custom_ChooseWallpaperImage": {"en": "Choose image…", "es": "Elegir imagen…", "fr": "Choisir l'image…", "hi": "छवि चुनें…", "id": "Pilih berkas gambar…", "pl": "Wybierz plik obrazu…", "pt-BR": "Escolher imagem…", "tr": "Görsel seç…", "uk": "Вибрати файл…"},
  "Custom_WallpaperBlur": {"en": "Blur", "es": "Desenfoque", "fr": "Flou", "hi": "धुंधलापन", "id": "Buram", "pl": "Rozmycie", "pt-BR": "Desfoque", "tr": "Bulanıklık", "uk": "Розмиття"},
  "Custom_WallpaperUnavailable": {"en": "The wallpaper image could not be loaded, so a solid background is shown for now. Pick another image to try again.", "es": "No se pudo cargar la imagen del fondo, así que por ahora se muestra un fondo sólido. Elige otra imagen para volver a intentarlo.", "fr": "L'image du fond d'écran n'a pas pu être chargée ; un fond uni est affiché pour l'instant. Choisissez une autre image pour réessayer.", "hi": "वॉलपेपर छवि लोड नहीं हो सकी, इसलिए अभी एक ठोस पृष्ठभूमि दिखाई जा रही है। फिर से प्रयास करने के लिए दूसरी छवि चुनें।", "id": "Gambar wallpaper tidak dapat dimuat, jadi latar belakang polos ditampilkan untuk saat ini. Pilih gambar lain untuk mencoba lagi.", "pl": "Nie udało się wczytać obrazu tapety, więc na razie wyświetlane jest jednolite tło. Wybierz inny obraz, aby spróbować ponownie.", "pt-BR": "Não foi possível carregar a imagem do papel de parede, então um fundo sólido está sendo exibido por enquanto. Escolha outra imagem para tentar novamente.", "tr": "Duvar kağıdı görseli yüklenemedi, bu yüzden şimdilik düz bir arka plan gösteriliyor. Yeniden denemek için başka bir görsel seçin.", "uk": "Не вдалося завантажити зображення шпалер, тому поки що показано суцільний фон. Виберіть інше зображення, щоб спробувати ще раз."},
  "Custom_WallpaperImageCopyFailed": {"en": "That image could not be copied, so the previous one is kept.", "es": "No se pudo copiar esa imagen, así que se conserva la anterior.", "fr": "Cette image n'a pas pu être copiée ; la précédente est conservée.", "hi": "उस छवि की प्रतिलिपि नहीं बनाई जा सकी, इसलिए पिछली छवि रखी गई है।", "id": "Gambar itu tidak dapat disalin, jadi gambar sebelumnya tetap dipakai.", "pl": "Nie udało się skopiować tego obrazu, więc zachowano poprzedni.", "pt-BR": "Não foi possível copiar essa imagem, então a anterior foi mantida.", "tr": "Bu görsel kopyalanamadı, bu yüzden öncekisi korundu.", "uk": "Не вдалося скопіювати це зображення, тому збережено попереднє."}
}
```

Run: `uv run python scripts/resx_add_keys.py <path>` → nine `+7` lines.

- [ ] **Step 8: Run the tests**

Run:
```
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~WallpaperBackdropTests|FullyQualifiedName~WallpaperImageStoreTests|FullyQualifiedName~WallpaperPanelTests|FullyQualifiedName~AuroraMeshTests|FullyQualifiedName~DashboardBackdropTintTests|FullyQualifiedName~CustomizationSettingsRoundTripTests|FullyQualifiedName~LocalizationKeyReferenceTests"
pwsh scripts/check-localization.ps1
```
Expected: 0 warnings; all pass (count +16); `errors=0`, no new warnings.

- [ ] **Step 9: Eyes pass**

`pwsh scripts/ui-hotreload.ps1 -Start`; open the drawer; Background Mode → Wallpaper: the real
desktop wallpaper appears, blurred; drag Blur to 0 and to 1 (visible difference); drag Window
opacity down (more picture). Choose image… with a JPEG: it appears; restart the Debug host
(`-Stop` then `-Start`): the picked image survives. Rename the copy under
`%LOCALAPPDATA%\RemEx\wallpapers\` while running and switch modes away and back: a solid
background and one snackbar; the mode picker still says Wallpaper. Snapshot each state with
`pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree`; `pwsh scripts/ui-hotreload.ps1 -Stop`.

- [ ] **Step 10: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add remex.desktop/Services/WallpaperBackdrop.cs remex.desktop/Services/WallpaperImageStore.cs remex.desktop/Converters/BlurRadiusToEffectConverter.cs remex.desktop/Services/SystemSeedSources.cs remex.desktop/ViewModels/ShellViewModel.cs remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop/Controls/DashboardBackgroundControl.axaml remex.desktop/Views/PersonalizationPanelView.axaml remex.desktop.tests/Services/WallpaperBackdropTests.cs remex.desktop.tests/Services/WallpaperImageStoreTests.cs remex.desktop.tests/Controls/WallpaperPanelTests.cs remex.desktop/Localization/Strings.resx remex.desktop/Localization/Strings.es.resx remex.desktop/Localization/Strings.fr.resx remex.desktop/Localization/Strings.hi.resx remex.desktop/Localization/Strings.id.resx remex.desktop/Localization/Strings.pl.resx remex.desktop/Localization/Strings.pt-BR.resx remex.desktop/Localization/Strings.tr.resx remex.desktop/Localization/Strings.uk.resx
git commit -m "feat(desktop): Wallpaper background mode draws the real wallpaper or a picked image, blurred, behind the surface (RemEx-<bead>)"
```

---

### Task 6: Mica removal and the transparency plumbing

**Files:**
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs` (`RefreshBackgroundTypes`, the Task 4 version: drop `"Mica"`)
- Modify: `remex.desktop/MainWindow.axaml.cs:99-104` (delete the Mica branch), `:21` (comment)
- Modify: `remex.desktop/MainWindow.axaml:16` (`TransparencyLevelHint="Mica, AcrylicBlur, None"` → `"AcrylicBlur, None"`)
- Modify: `remex.desktop/Controls/DashboardBackgroundControl.axaml:247-253` (delete the Mica panel; fix the `:221-233` comment)
- Modify: `remex.desktop/Converters/StringMatchConverter.cs:36-38` (delete `IsMica`)
- Modify: `remex.desktop/Converters/PrefixedLabelConverter.cs:9-16` (doc comment examples → `Aurora`)
- Modify: `remex.desktop.tests/Controls/DashboardBackdropTintTests.cs:79` (delete the `IsMica` InlineData; fix the `:11-37` remarks)
- Test: `remex.desktop.tests/Controls/MicaIsGoneTests.cs`

**Interfaces:**
- Consumes: the Task 4 `RefreshBackgroundTypes`, `StringMatchConverter`.
- Produces: no new members. Removes `StringMatchConverter.IsMica` and the `"Mica"` background type. `TrayBalloonWindow.axaml:8` keeps its own `Mica` hint — it is a separate tray window, not the background-mode plumbing, and the spec retires the mode, not every DWM hint in the app.

- [ ] **Step 1: Write the failing guard**

Create `remex.desktop.tests/Controls/MicaIsGoneTests.cs`:

```csharp
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Mica never rendered on this Avalonia build (RemEx-z94c7: window pixels invariant to wallpaper
/// changes). The spec removes it from the list, the converter and the window plumbing rather than
/// repairing it. Source-text, because the failure it guards is a silent flat surface.
/// </summary>
public class MicaIsGoneTests
{
    [Theory]
    [InlineData("remex.desktop/ViewModels/CustomizationViewModel.cs")]
    [InlineData("remex.desktop/Converters/StringMatchConverter.cs")]
    [InlineData("remex.desktop/Controls/DashboardBackgroundControl.axaml")]
    [InlineData("remex.desktop/MainWindow.axaml.cs")]
    [InlineData("remex.desktop/MainWindow.axaml")]
    public void NoMicaLiteralSurvivesInTheBackgroundModePlumbing(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Regex.IsMatch(text, "\"Mica\"|IsMica|WindowTransparencyLevel\\.Mica|TransparencyLevelHint=\"[^\"]*Mica")
            .Should().BeFalse($"{relativePath} still offers or plumbs Mica, a mode that cannot render (RemEx-z94c7)");
    }

    [Fact]
    public void TheDefaultBackgroundIsAuroraAndMicaIsNotAnOption()
    {
        new Remex.Core.Models.CustomizationSettings().BackgroundMaterial.Should().Be("Aurora");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~MicaIsGoneTests"`
Expected: five `NoMicaLiteralSurvives…` cases FAIL.

- [ ] **Step 3: Remove Mica**

- `CustomizationViewModel.RefreshBackgroundTypes`: delete `AvailableBackgroundTypes.Add("Mica");`. The Windows branch now adds only `Acrylic`.
- `MainWindow.axaml.cs:99-104`: delete the whole `if (OperatingSystem.IsWindows() && settings.BackgroundMaterial == "Mica") { … }` branch so the `else if (… "Acrylic")` becomes the first `if`. Rewrite the comment at `:21` ("the app then renders a flat surface while reporting that Mica is active") to say Acrylic.
- `MainWindow.axaml:16`: `TransparencyLevelHint="AcrylicBlur, None"`. Edit the comment at `:20-21` to drop Mica.
- `DashboardBackgroundControl.axaml:247-253`: delete the Mica panel. Rewrite the `:221-233`
  comment so it names Acrylic and Glass only (keep the RemEx-c437b/RemEx-mmrgc history; replace
  "Mica went 63%→100%" with "Acrylic went 63%→100%" and drop "Mica and Acrylic (RemEx-mmrgc) scale…"
  to "Acrylic (RemEx-mmrgc) scales…").
- `StringMatchConverter.cs:36-38`: delete `IsMica`.
- `PrefixedLabelConverter.cs:9-16`: change the example to `Custom_BgType_` + `Aurora` → `Custom_BgType_Aurora`, and the `MainWindow.axaml.cs` note to `== "Acrylic"`.
- `DashboardBackdropTintTests.cs:79`: delete `[InlineData("IsMica", 0.30)]`; in the remarks
  `:11-37` replace "Mica / Acrylic / Glass" with "Acrylic / Glass" and delete the sentence measuring
  the Mica canvas.
- `remex.desktop.tests/Converters/MultiplyConverterTests.cs` keeps its `Mica_*`-named converter
  tests: they test the converter's arithmetic with the 0.30 parameter, not the mode. Leave them.

- [ ] **Step 4: Run the tests**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~MicaIsGoneTests|FullyQualifiedName~DashboardBackdropTintTests|FullyQualifiedName~WallpaperPanelTests|FullyQualifiedName~WindowChromeBackdropTests"`
Expected: 0 warnings; all pass.

- [ ] **Step 5: Eyes pass**

`pwsh scripts/ui-hotreload.ps1 -Start`; open the drawer; the Background Mode list reads Aurora,
Wallpaper, Acrylic, Gradient, Solid on Windows. Pick Acrylic: the desktop still shows through.
`pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree`; `pwsh scripts/ui-hotreload.ps1 -Stop`.

- [ ] **Step 6: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop/MainWindow.axaml.cs remex.desktop/MainWindow.axaml remex.desktop/Controls/DashboardBackgroundControl.axaml remex.desktop/Converters/StringMatchConverter.cs remex.desktop/Converters/PrefixedLabelConverter.cs remex.desktop.tests/Controls/DashboardBackdropTintTests.cs remex.desktop.tests/Controls/MicaIsGoneTests.cs
git commit -m "refactor(desktop): retire the Mica background mode and its transparency plumbing (RemEx-<bead>)"
```

---

### Task 7: Saved palettes — tiles, Save current, apply/delete, presets alongside, import/export format

**Files:**
- Create: `remex.desktop/ViewModels/SavedPaletteTileViewModel.cs`
- Modify: `remex.desktop/Services/PaletteExchange.cs:15` (`PaletteRecipe` gains `ColorSource`, `Name`), `:29` (`FormatVersion = 2`), `:46-47` (DTO), `:50-55`, `:62-85`
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs`: `:376-387` (`RecipeFromCurrent` fills the new fields), `:505-510` (import adds a tile then applies), constructor (load tiles), `:966-1003` (`SelectTheme` sets the source), `:1009-1037` (refresh tiles alongside the presets), `ApplyAndSave` (writes `SavedPalettes`), `Dispose` (dispose tiles), new saved-palette members
- Modify: `remex.desktop/Views/PersonalizationPanelView.axaml:18-66` (user-palette row + name box + Save current under the presets gallery; Task 8 moves the whole block into the Saved palettes card)
- Modify: `remex.desktop.tests/Services/PaletteExchangeTests.cs` (new-field round trip, v1 file import)
- Modify: nine `Strings*.resx` (five keys)
- Test: `remex.desktop.tests/ViewModels/SavedPaletteTileViewModelTests.cs`, `remex.desktop.tests/ViewModels/SavedPalettesWiringTests.cs`

**Interfaces:**
- Consumes: `SavedPalette`, `ColorSources` (Task 1), `SchemeVariants.Normalize` (Task 2), `ColorSource`/`AdoptSourceSeed`/`RefreshWallpaperSeedsAsync` (Task 3), `SeedPresetTileViewModel` shape (`:20-75`) as the pattern, `DynamicColorGenerator.Generate`.
- Produces:
  - `PaletteRecipe(string Seed, string Variant, string Mode, double Contrast, double SeedChroma, string ColorSource = "Custom", string? Name = null)`; `PaletteExchange.FormatVersion == 2`; `TryParseJson` accepts a version-1 file (no `colorSource`/`name`) as `Custom`/`null`.
  - `SavedPaletteTileViewModel(SavedPalette palette)`: `string Name` (observable, rename), `SavedPalette Record { get; }` (updated on rename), `IBrush SurfaceBrush/PrimaryBrush/SecondaryBrush/TertiaryBrush/OnSurfaceBrush/OutlineBrush`, `void Refresh(bool liveIsLight)`, `event Action<SavedPaletteTileViewModel>? Renamed`.
  - `CustomizationViewModel`: `ObservableCollection<SavedPaletteTileViewModel> SavedPalettes`, `string NewPaletteName` (observable), `SaveCurrentPaletteCommand`, `ApplySavedPaletteCommand(SavedPaletteTileViewModel)`, `DeleteSavedPaletteCommand(SavedPaletteTileViewModel)`.
  - Resx: `Custom_SavePalette`, `Custom_UserPalettes`, `Custom_BuiltInPresets`, `Custom_DeletePalette`, `Custom_PaletteNamePlaceholder`.

- [ ] **Step 1: Write the failing tests**

Append to `remex.desktop.tests/Services/PaletteExchangeTests.cs`:

```csharp
    [Fact]
    public void ToJson_TryParseJson_RoundTripsTheColourSourceAndName()
    {
        var recipe = new PaletteRecipe("#FF00F3FF", "Neutral", ThemeModes_Dark, 0.1, 30.0, ColorSource: "WindowsAccent", Name: "Office blue");

        PaletteExchange.TryParseJson(PaletteExchange.ToJson(recipe), out var parsed).Should().BeTrue();

        parsed!.ColorSource.Should().Be("WindowsAccent");
        parsed.Name.Should().Be("Office blue");
        parsed.Variant.Should().Be("Neutral");
    }

    [Fact]
    public void AVersionOneFileWithoutTheNewFieldsImportsAsACustomUnnamedPalette()
    {
        // Exactly what RemEx-a7uzb wrote: no colorSource, no name, a variant name that is now retired.
        const string v1 = """
        {
          "formatVersion": 1,
          "seed": "#00F3FF",
          "variant": "Spritz",
          "mode": "Dark",
          "contrast": 0.2,
          "seedChroma": 40
        }
        """;

        PaletteExchange.TryParseJson(v1, out var parsed).Should().BeTrue("an older file imports with the section-4 migration rules");

        parsed!.ColorSource.Should().Be("Custom");
        parsed.Name.Should().BeNull();
        parsed.Variant.Should().Be("Neutral");
    }

    [Fact]
    public void AnUnknownColourSourceImportsAsCustom()
    {
        var json = PaletteExchange.ToJson(new PaletteRecipe("#00F3FF", "Vibrant", ThemeModes_Dark, 0, 40, ColorSource: "Phone"));

        PaletteExchange.TryParseJson(json, out var parsed).Should().BeTrue();
        parsed!.ColorSource.Should().Be("Custom", "the phone source is a follow-up (spec section 12), not a value this build knows");
    }
```

Update `ToJson_EmitsCamelCaseAndFormatVersion` (`:52-56`) if it pins the number `1`: it must now
expect `"formatVersion": 2`.

Create `remex.desktop.tests/ViewModels/SavedPaletteTileViewModelTests.cs`:

```csharp
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class SavedPaletteTileViewModelTests
{
    private static SavedPalette Dusk() => new()
    {
        Name = "Dusk", ColorSource = ColorSources.Custom, Seed = "#FF2D95", Vibrancy = 60, Contrast = 0.2, Strategy = "Expressive",
    };

    [Fact]
    public void TheTileIsPaintedFromItsOwnRecipeInTheLiveMode()
    {
        var tile = new SavedPaletteTileViewModel(Dusk());

        tile.Refresh(liveIsLight: false);
        var dark = ((SolidColorBrush)tile.SurfaceBrush).Color;
        tile.Refresh(liveIsLight: true);
        var light = ((SolidColorBrush)tile.SurfaceBrush).Color;

        dark.Should().NotBe(light, "the tile follows the window's light/dark like the preset tiles do");
        ((SolidColorBrush)tile.PrimaryBrush).Color.Should().NotBe(Colors.Transparent);
    }

    [Fact]
    public void RenamingUpdatesTheRecordAndRaisesRenamed()
    {
        var tile = new SavedPaletteTileViewModel(Dusk());
        SavedPaletteTileViewModel? raised = null;
        tile.Renamed += t => raised = t;

        tile.Name = "Evening";

        tile.Record.Name.Should().Be("Evening");
        tile.Record.Seed.Should().Be("#FF2D95", "a rename changes nothing but the name");
        raised.Should().BeSameAs(tile);
    }

    [Fact]
    public void ABlankRenameIsIgnored()
    {
        var tile = new SavedPaletteTileViewModel(Dusk());

        tile.Name = "   ";

        tile.Record.Name.Should().Be("Dusk");
        tile.Name.Should().Be("Dusk");
    }
}
```

Create `remex.desktop.tests/ViewModels/SavedPalettesWiringTests.cs`:

```csharp
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>The user-palette row on the sheet (spec section 7), pinned over source text like the preset gallery is.</summary>
public class SavedPalettesWiringTests
{
    [Fact]
    public void TheUserRowAppliesAndDeletesThroughTheSavedPaletteCommands()
    {
        var markup = PanelMarkup();
        var row = Regex.Match(markup, @"<ItemsControl ItemsSource=""\{Binding SavedPalettes\}"".*?</ItemsControl>", RegexOptions.Singleline);

        row.Success.Should().BeTrue("the user palettes row binds SavedPalettes");
        row.Value.Should().Contain("ApplySavedPaletteCommand").And.Contain("DeleteSavedPaletteCommand");
        Regex.IsMatch(row.Value, @"#[0-9A-Fa-f]{6}").Should().BeFalse("a tile renders its own palette, never a literal");
    }

    [Fact]
    public void SaveCurrentWritesANameThePersonCanEdit()
    {
        var markup = PanelMarkup();

        markup.Should().MatchRegex(@"Text=""\{Binding NewPaletteName[,}]", "the name box is bound two-way to NewPaletteName");
        markup.Should().Contain("SaveCurrentPaletteCommand");
    }

    [Fact]
    public void PresetsCannotBeDeleted()
    {
        var gallery = Regex.Match(PanelMarkup(), @"<ItemsControl ItemsSource=""\{Binding ThemePresets\}"".*?</ItemsControl>", RegexOptions.Singleline);

        gallery.Success.Should().BeTrue();
        gallery.Value.Should().NotContain("DeleteSavedPaletteCommand");
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo`
Expected: errors — `PaletteRecipe` has no `ColorSource`/`Name`; `SavedPaletteTileViewModel` missing.

- [ ] **Step 3: The exchange format**

`remex.desktop/Services/PaletteExchange.cs`:

- `:15` → `public sealed record PaletteRecipe(string Seed, string Variant, string Mode, double Contrast, double SeedChroma, string ColorSource = ColorSources.Custom, string? Name = null);`
- `:29` → `private const int FormatVersion = 2;` with the remark "2 (RemEx-ddynd): optional
  `colorSource` and `name`; a version-1 reader ignores them, a version-1 file reads as Custom/unnamed."
- `:46-47` → `private sealed record PaletteRecipeDto(int FormatVersion, string Seed, string Variant, string Mode, double Contrast, double SeedChroma, string? ColorSource, string? Name);`
- `ToJson` (`:50-55`) passes `recipe.ColorSource, recipe.Name`.
- `TryParseJson` (`:83`) builds:

```csharp
        var source = dto.ColorSource switch
        {
            ColorSources.WindowsAccent or ColorSources.Wallpaper or ColorSources.Custom => dto.ColorSource,
            _ => ColorSources.Custom,
        };
        var name = string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim();
        recipe = new PaletteRecipe(dto.Seed, SchemeVariants.Normalize(dto.Variant), dto.Mode, contrast, dto.SeedChroma, source, name);
```

`JsonOptions` already ignores nothing and `System.Text.Json` leaves an absent property at its
default (null), which is what makes a version-1 file parse.

- [ ] **Step 4: The tile view model**

Create `remex.desktop/ViewModels/SavedPaletteTileViewModel.cs`:

```csharp
using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// One user-saved palette on the sheet (RemEx-ddynd), painted from its OWN recipe in the window's
/// live light/dark — the same shape as <see cref="SeedPresetTileViewModel"/>, minus localisation:
/// the name is the person's own text.
/// </summary>
public sealed partial class SavedPaletteTileViewModel : ObservableObject
{
    public SavedPaletteTileViewModel(SavedPalette palette)
    {
        Record = palette;
        _name = palette.Name;
        _surfaceBrush = Brushes.Transparent;
        _primaryBrush = Brushes.Transparent;
        _secondaryBrush = Brushes.Transparent;
        _tertiaryBrush = Brushes.Transparent;
        _onSurfaceBrush = Brushes.Transparent;
        _outlineBrush = Brushes.Transparent;
    }

    /// <summary>The persisted recipe. Replaced (records are immutable) on rename.</summary>
    public SavedPalette Record { get; private set; }

    /// <summary>Raised after a rename lands in <see cref="Record"/>, so the owner can persist.</summary>
    public event Action<SavedPaletteTileViewModel>? Renamed;

    [ObservableProperty] private string _name;

    [ObservableProperty] private IBrush _surfaceBrush;
    [ObservableProperty] private IBrush _primaryBrush;
    [ObservableProperty] private IBrush _secondaryBrush;
    [ObservableProperty] private IBrush _tertiaryBrush;
    [ObservableProperty] private IBrush _onSurfaceBrush;
    [ObservableProperty] private IBrush _outlineBrush;

    partial void OnNameChanged(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            // A blank name is not a rename; put the old one back without re-entering.
            if (Name != Record.Name) Name = Record.Name;
            return;
        }
        if (string.Equals(trimmed, Record.Name, StringComparison.Ordinal)) return;

        Record = Record with { Name = trimmed };
        Renamed?.Invoke(this);
    }

    /// <summary>Repaints the tile from its recipe. Cheap: one Generate per tile.</summary>
    public void Refresh(bool liveIsLight)
    {
        var seed = Color.TryParse(Record.Seed, out var parsed) ? parsed : ThemeService.FallbackAccentColor;
        var palette = DynamicColorGenerator.Generate(seed, Record.Strategy, isDark: !liveIsLight, Math.Clamp(Record.Contrast, -1.0, 1.0));

        SurfaceBrush = new SolidColorBrush(palette.Surface);
        PrimaryBrush = new SolidColorBrush(palette.Primary);
        SecondaryBrush = new SolidColorBrush(palette.Secondary);
        TertiaryBrush = new SolidColorBrush(palette.Tertiary);
        OnSurfaceBrush = new SolidColorBrush(palette.OnSurface);
        OutlineBrush = new SolidColorBrush(palette.Outline);
    }
}
```

(`ThemeService.FallbackAccentColor` is the existing `Color` fallback used at
`CustomizationViewModel.cs:589`.)

- [ ] **Step 5: The view model**

`remex.desktop/ViewModels/CustomizationViewModel.cs`, next to `ThemePresets` (`:678`):

```csharp
    // ─── Saved palettes (RemEx-ddynd, spec section 7) ────────────────────────────────────────────

    /// <summary>The person's palettes, in saved order, after the built-in presets on the sheet.</summary>
    public ObservableCollection<SavedPaletteTileViewModel> SavedPalettes { get; } = new();

    /// <summary>The name "Save current" writes. Blank means "Palette N".</summary>
    [ObservableProperty]
    private string _newPaletteName = string.Empty;

    private SavedPalette CurrentAsSavedPalette(string name) => new()
    {
        Name = name,
        ColorSource = ColorSource,
        Seed = AccentColor,
        Vibrancy = SeedChroma,
        Contrast = Math.Clamp(ThemeContrast, -1.0, 1.0),
        Strategy = SchemeVariant,
    };

    private string NextDefaultPaletteName() => SavedPalette.DefaultNamePrefix + (SavedPalettes.Count + 1);

    [RelayCommand]
    private void SaveCurrentPalette()
    {
        var name = string.IsNullOrWhiteSpace(NewPaletteName) ? NextDefaultPaletteName() : NewPaletteName.Trim();
        AddSavedPalette(CurrentAsSavedPalette(name));
        NewPaletteName = string.Empty;
        ApplyAndSave();
    }

    private SavedPaletteTileViewModel AddSavedPalette(SavedPalette palette)
    {
        var tile = new SavedPaletteTileViewModel(palette);
        tile.Renamed += _ => ApplyAndSave();
        tile.Refresh(CurrentIsLightPalette());
        SavedPalettes.Add(tile);
        return tile;
    }

    /// <summary>
    /// Applying a palette sets the same fields a preset sets. A Custom palette becomes the Custom
    /// source with that seed; a Windows-accent or Wallpaper palette re-selects that source, whose
    /// handler adopts the live system colour shaped by the palette's vibrancy (spec section 7).
    /// </summary>
    [RelayCommand]
    private void ApplySavedPalette(SavedPaletteTileViewModel tile)
    {
        var p = tile.Record;
        _isApplyingPreset = true;
        try
        {
            SchemeVariant = SchemeVariants.Normalize(p.Strategy);
            ThemeContrast = Math.Clamp(p.Contrast, -1.0, 1.0);
            AccentColor = p.Seed;      // syncs hue/chroma/tone from the seed
            SeedChroma = p.Vibrancy;   // then the saved vibrancy re-shapes it (PushSeedToAccent)
            ColorSource = p.ColorSource switch
            {
                ColorSources.WindowsAccent when AvailableColorSources.Contains(ColorSources.WindowsAccent) => ColorSources.WindowsAccent,
                ColorSources.Wallpaper when AvailableColorSources.Contains(ColorSources.Wallpaper) => ColorSources.Wallpaper,
                _ => ColorSources.Custom,
            };
        }
        finally
        {
            _isApplyingPreset = false;
        }

        // ColorSource's own handler already adopted the system seed (or, for Wallpaper, started
        // the async extraction, which saves when it lands); this is the one save for everything else.
        ApplyAndSave();
        CommitSeedToRecents();
    }

    [RelayCommand]
    private void DeleteSavedPalette(SavedPaletteTileViewModel tile)
    {
        if (!SavedPalettes.Remove(tile)) return;
        ApplyAndSave();
    }

    private void RefreshSavedPaletteTiles()
    {
        var liveIsLight = CurrentIsLightPalette();
        foreach (var tile in SavedPalettes) tile.Refresh(liveIsLight);
    }
```

Note on `ApplySavedPalette`: `OnColorSourceChanged` is a generated partial that runs even while
`_isApplyingPreset` is set; for Windows accent it calls `AdoptSourceSeed` → `PushSeedToAccent` →
`AccentColor` setter → `ApplyAndSave` (short-circuited by the flag), so the seed lands and the
single `ApplyAndSave()` after the block persists it. If the source is unchanged, the generated
setter does nothing, and the seed written two lines above stands.

Constructor: after the preset gallery is built (`:595-596`), load the tiles:

```csharp
        foreach (var saved in settings.SavedPalettes ?? Array.Empty<SavedPalette>())
            AddSavedPalette(saved);
```

(`AddSavedPalette` calls `CurrentIsLightPalette()`, which reads `_themeModeIndex` — already set by
then.) In `_onCustomizationApplied` and at the tail of `ApplyAndSave` (`:954-955`), add
`RefreshSavedPaletteTiles();` beside `RefreshPresetPreviews`. In `ApplyAndSave`'s initializer
replace `SavedPalettes = carried.SavedPalettes,` with
`SavedPalettes = SavedPalettes.Select(t => t.Record).ToList(),`. In `Dispose` (`:1147-1156`)
add `SavedPalettes.Clear();` (tiles hold no subscriptions to singletons; clearing drops the
`Renamed` handlers that close over this VM).

`SelectTheme` (`:986`): change `if (preset.Seed is { } seed) AccentColor = seed;` to
`if (preset.Seed is { } seed) { ColorSource = ColorSources.Custom; AccentColor = seed; }` — a
preset's seed is a hand-chosen seed, so applying one sets the same fields a Custom saved palette
sets. Dynamic (null seed) leaves the source alone.

`RecipeFromCurrent` (`:376-387`): construct `new PaletteRecipe(AccentColor, SchemeVariant, mode ?? ThemeModes.Dark, ThemeContrast, SeedHct.ChromaOf(…), ColorSource, string.IsNullOrWhiteSpace(NewPaletteName) ? null : NewPaletteName.Trim())`.

`ImportPaletteJsonAsync` (`:505-510`): replace the five apply lines with

```csharp
            var tile = AddSavedPalette(new SavedPalette
            {
                Name = recipe.Name ?? NextDefaultPaletteName(),
                ColorSource = recipe.ColorSource,
                Seed = recipe.Seed,
                Vibrancy = recipe.SeedChroma,
                Contrast = recipe.Contrast,
                Strategy = recipe.Variant,
            });
            SetThemeMode(recipe.Mode);
            ApplySavedPalette(tile);   // saves; the mode rides the same save
```

- [ ] **Step 6: The view (pre-reflow position)**

In `remex.desktop/Views/PersonalizationPanelView.axaml`, directly after the presets
`</ItemsControl>` (`:66`), insert:

```xml
        <!-- The person's own palettes (RemEx-ddynd, spec section 7): whole recipes, painted like the
             presets above from their OWN seed and strategy. Presets have no delete; these do. -->
        <StackPanel Spacing="8">
            <TextBlock Text="{local:Localize Custom_UserPalettes}" Theme="{StaticResource CaptionTextBlock}" Foreground="{DynamicResource TextMutedBrush}"/>
            <ItemsControl ItemsSource="{Binding SavedPalettes}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <UniformGrid Columns="3"/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="vm:SavedPaletteTileViewModel">
                        <Border Margin="4" Background="{Binding SurfaceBrush}" BorderBrush="{Binding OutlineBrush}" BorderThickness="1" CornerRadius="8" Padding="6,8">
                            <StackPanel Spacing="6">
                                <Button Classes="tile" Padding="0" Background="Transparent"
                                        AutomationProperties.Name="{Binding Name}"
                                        Command="{Binding $parent[ItemsControl].((vm:CustomizationViewModel)DataContext).ApplySavedPaletteCommand}"
                                        CommandParameter="{Binding}">
                                    <StackPanel Orientation="Horizontal" Spacing="4" HorizontalAlignment="Center">
                                        <Border Width="14" Height="14" CornerRadius="7" Background="{Binding PrimaryBrush}"/>
                                        <Border Width="11" Height="11" CornerRadius="6" Background="{Binding SecondaryBrush}" VerticalAlignment="Center"/>
                                        <Border Width="8" Height="8" CornerRadius="4" Background="{Binding TertiaryBrush}" VerticalAlignment="Center"/>
                                    </StackPanel>
                                </Button>
                                <TextBox Text="{Binding Name, Mode=TwoWay}" FontSize="10" FontWeight="Bold" TextAlignment="Center"
                                         Foreground="{Binding OnSurfaceBrush}" Background="Transparent" BorderThickness="0"
                                         AutomationProperties.Name="{local:Localize Custom_PaletteNamePlaceholder}"/>
                                <Button Classes="compact" Content="✕" HorizontalAlignment="Center" Padding="6,0"
                                        ToolTip.Tip="{local:Localize Custom_DeletePalette}" AutomationProperties.Name="{local:Localize Custom_DeletePalette}"
                                        Command="{Binding $parent[ItemsControl].((vm:CustomizationViewModel)DataContext).DeleteSavedPaletteCommand}"
                                        CommandParameter="{Binding}"/>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">
                <TextBox Text="{Binding NewPaletteName, Mode=TwoWay}" Watermark="{local:Localize Custom_PaletteNamePlaceholder}" FontSize="12" AutomationProperties.Name="{local:Localize Custom_PaletteNamePlaceholder}"/>
                <Button Grid.Column="1" Classes="compact" Content="{local:Localize Custom_SavePalette}" Command="{Binding SaveCurrentPaletteCommand}" AutomationProperties.Name="{local:Localize Custom_SavePalette}"/>
            </Grid>
        </StackPanel>
```

- [ ] **Step 7: Add the resx keys**

Scratch `task7-keys.json`:

```json
{
  "Custom_SavePalette": {"en": "Save current", "es": "Guardar actual", "fr": "Enregistrer l'actuelle", "hi": "वर्तमान सहेजें", "id": "Simpan yang sekarang", "pl": "Zapisz bieżącą", "pt-BR": "Salvar atual", "tr": "Geçerli olanı kaydet", "uk": "Зберегти поточну"},
  "Custom_UserPalettes": {"en": "Your palettes", "es": "Tus paletas", "fr": "Vos palettes", "hi": "आपके पैलेट", "id": "Palet Anda", "pl": "Twoje palety", "pt-BR": "Suas paletas", "tr": "Paletleriniz", "uk": "Ваші палітри"},
  "Custom_BuiltInPresets": {"en": "Built-in", "es": "Integradas", "fr": "Intégrées", "hi": "अंतर्निर्मित", "id": "Bawaan", "pl": "Wbudowane", "pt-BR": "Integradas", "tr": "Yerleşik", "uk": "Вбудовані"},
  "Custom_DeletePalette": {"en": "Delete palette", "es": "Eliminar paleta", "fr": "Supprimer la palette", "hi": "पैलेट हटाएँ", "id": "Hapus palet", "pl": "Usuń paletę", "pt-BR": "Excluir paleta", "tr": "Paleti sil", "uk": "Видалити палітру"},
  "Custom_PaletteNamePlaceholder": {"en": "Palette name", "es": "Nombre de la paleta", "fr": "Nom de la palette", "hi": "पैलेट का नाम", "id": "Nama palet", "pl": "Nazwa palety", "pt-BR": "Nome da paleta", "tr": "Palet adı", "uk": "Назва палітри"}
}
```

Run: `uv run python scripts/resx_add_keys.py <path>` → nine `+5` lines. (`Custom_BuiltInPresets`
is referenced by Task 8's card header; adding it now keeps Task 8's diff to the view.)

- [ ] **Step 8: Run the tests**

Run:
```
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~PaletteExchangeTests|FullyQualifiedName~SavedPaletteTileViewModelTests|FullyQualifiedName~SavedPalettesWiringTests|FullyQualifiedName~CustomizationSettingsRoundTripTests|FullyQualifiedName~SeedPresetCatalogTests|FullyQualifiedName~LocalizationKeyReferenceTests"
pwsh scripts/check-localization.ps1
```
Expected: 0 warnings; all pass (count +9); `errors=0`, no new warnings. `Custom_BuiltInPresets`
is unreferenced until Task 8; no axis flags an unused key.

- [ ] **Step 9: Eyes pass**

`pwsh scripts/ui-hotreload.ps1 -Start`; drawer; type a name, Save current → a tile appears
painted in the live palette; change the seed, click the tile → the saved palette comes back;
rename in the tile's box; ✕ deletes. Export, then Import the file → a new tile plus the palette
applied. Snapshot; `-Stop`.

- [ ] **Step 10: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add remex.desktop/ViewModels/SavedPaletteTileViewModel.cs remex.desktop/Services/PaletteExchange.cs remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop/Views/PersonalizationPanelView.axaml remex.desktop.tests/Services/PaletteExchangeTests.cs remex.desktop.tests/ViewModels/SavedPaletteTileViewModelTests.cs remex.desktop.tests/ViewModels/SavedPalettesWiringTests.cs remex.desktop/Localization/Strings.resx remex.desktop/Localization/Strings.es.resx remex.desktop/Localization/Strings.fr.resx remex.desktop/Localization/Strings.hi.resx remex.desktop/Localization/Strings.id.resx remex.desktop/Localization/Strings.pl.resx remex.desktop/Localization/Strings.pt-BR.resx remex.desktop/Localization/Strings.tr.resx remex.desktop/Localization/Strings.uk.resx
git commit -m "feat(desktop): saved palettes — save, apply, rename, delete, and a versioned export format (RemEx-<bead>)"
```

---

### Task 8: The sheet — six sections, the source picker, the sample-card preview, splash Preview

**Files:**
- Modify: `remex.desktop/Views/PersonalizationPanelView.axaml` (whole body restructured; element-by-element moves below)
- Modify: `remex.desktop/ViewModels/CustomizationViewModel.cs` (`SampleCardCornerRadius`, `PreviewSplashCommand`, `OnCornerRadiusChanged` notifies)
- Modify: `remex.desktop/ViewModels/ShellViewModel.cs:256-264` (`ReplayWelcomeSplash`, `SplashReplayRequested`), `:481-511`
- Modify: `remex.desktop/Views/ShellView.axaml.cs:191-215` (hook `SplashReplayRequested` to `bootSplash.Restart()`)
- Modify: `remex.desktop/Controls/Splash/SkiaSplashControl.cs:63-88` (`public void Restart()`)
- Modify: `remex.desktop/Services/StartupViewArgument.cs:45-46` (`["Personalize"] = vm => vm.NavigateToCustomization()`)
- Modify: `remex.desktop.tests/Services/StartupViewArgumentTests.cs:72-80` (ten names)
- Modify: `remex.desktop.tests/ViewModels/PaletteStudioWiringTests.cs:24-29` (drop the `SeedTone` case)
- Modify: nine `Strings*.resx` (sixteen keys)
- Test: `remex.desktop.tests/Views/PersonalizationSheetLayoutTests.cs`

**Interfaces:**
- Consumes: everything Tasks 3, 5 and 7 produced on `CustomizationViewModel`; `TonalRampViewModel` brushes (`PrimaryBrush`, `OnPrimaryBrush`, `SurfaceBrush`, `OnSurfaceBrush`, `TonalRampViewModel.cs:20-25`); `ShellViewModel.ShowWelcomeSplash`/`IsWelcomeSplashMounted` (`:256`, `:264`); `SkiaSplashControl` (`:63-88`).
- Produces:
  - `CustomizationViewModel.SampleCardCornerRadius` (`CornerRadius`), `PreviewSplashCommand`.
  - `ShellViewModel.ReplayWelcomeSplash()`; `event Action? SplashReplayRequested`.
  - `SkiaSplashControl.Restart()`.
  - `--view Personalize` opens the sheet.
  - Resx: `Custom_SectionColor`, `Custom_SectionLook`, `Custom_SectionBehaviour`, `Custom_SectionSavedPalettes`, `Custom_ColorSource`, `Custom_Source_WindowsAccent`, `Custom_Source_Wallpaper`, `Custom_Source_Custom`, `Custom_CurrentWindowsAccent`, `Custom_RefreshWallpaperSeeds`, `Custom_Vibrancy`, `Custom_Strategy`, `Custom_Preview`, `Custom_SampleCardTitle`, `Custom_SampleCardBody`, `Custom_SectionMode`.
  - Section header keys, in order: `Custom_SectionColor`, `Custom_SectionMode`, `Custom_SectionLook`, `Custom_AdvancedTuning` (existing, "ADVANCED FINE-TUNING"), `Custom_SectionBehaviour`, `Custom_SectionSavedPalettes`.

- [ ] **Step 1: Write the failing source guards**

Create `remex.desktop.tests/Views/PersonalizationSheetLayoutTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The Personalization sheet's shape (spec section 3): six cards in a pinned order, one path to a
/// colour, no Tone slider, no seed setter reachable from the Saved palettes card. Source-text,
/// because remex.desktop.tests has no headless render.
/// </summary>
public class PersonalizationSheetLayoutTests
{
    private static readonly string[] HeadersInOrder =
    {
        "Custom_SectionColor", "Custom_SectionMode", "Custom_SectionLook",
        "Custom_AdvancedTuning", "Custom_SectionBehaviour", "Custom_SectionSavedPalettes",
    };

    [Fact]
    public void TheSixSectionHeadersAppearInSpecOrder()
    {
        var markup = PanelMarkup();
        var positions = HeadersInOrder.Select(k => markup.IndexOf($"Localize {k}}}", System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every section header is on the sheet");
        positions.Should().BeInAscendingOrder("the order is the spec's: Colour, Mode, Look, Fine-tuning, Behaviour, Saved palettes");
    }

    [Fact]
    public void TheSavedPalettesCardBindsNoSeedSetter()
    {
        var card = SavedPalettesCard();

        card.Should().NotContain("SetAccentCommand", "a saved palette is a whole palette; the accent-swatch shortcut is gone (spec section 1)");
        card.Should().Contain("ThemePresets").And.Contain("SavedPalettes")
            .And.Contain("SaveCurrentPaletteCommand").And.Contain("ImportPaletteJsonCommand").And.Contain("ExportPaletteJsonCommand");
    }

    [Fact]
    public void TheToneSliderIsGoneButTheWheelStillCarriesTone()
    {
        var markup = PanelMarkup();

        Regex.IsMatch(markup, @"<Slider[^>]*Value=""\{Binding SeedTone").Should().BeFalse("Android has no tone slider (spec section 3)");
        markup.Should().Contain("Tone=\"{Binding SeedTone", "the wheel still needs tone to render the disc");
        markup.Should().NotContain("Custom_SeedTone");
    }

    [Fact]
    public void TheRetiredStrategyNamesAreNotOnTheSheet()
    {
        PanelMarkup().Should().NotContain("Custom_Scheme_Content").And.NotContain("Custom_Scheme_Spritz");
    }

    [Fact]
    public void TheColourCardOffersSourceThenVibrancyContrastStrategyThenPreview()
    {
        var card = ColourCard();
        var order = new[] { "AvailableColorSources", "Custom_Vibrancy", "Custom_ContrastLevel", "SchemeVariantStrips", "Custom_Preview" }
            .Select(k => card.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        order.Should().OnlyContain(p => p >= 0);
        order.Should().BeInAscendingOrder("source, then shape, then strategy, then preview — Android's flow");
        card.Should().MatchRegex(@"SelectedItem=""\{Binding ColorSource[,}]", "the source picker writes ColorSource");
        card.Should().MatchRegex(@"Value=""\{Binding SeedChroma[,}]", "Vibrancy keeps its SeedChroma backing field");
    }

    [Fact]
    public void EachSourceShowsOnlyItsOwnControls()
    {
        var card = ColourCard();

        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWindowsAccentSource\}""[^>]*>[\s\S]*?SourceAccentHex", "the Windows-accent swatch shows the current accent");
        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWallpaperSource\}""[\s\S]*?RefreshWallpaperSeedsCommand[\s\S]*?WallpaperSeedCandidates");
        var custom = Regex.Match(card, @"IsVisible=""\{Binding IsCustomSource\}""[\s\S]*?</StackPanel>\s*</StackPanel>");
        custom.Success.Should().BeTrue();
        custom.Value.Should().Contain("<controls:HctColorWheel").And.Contain("CustomAccentHex").And.Contain("CustomAccentColors",
            "the wheel, the hex box and the recents row live under Custom");
    }

    [Fact]
    public void TheSampleCardTakesTheLivePaletteCornersAndWindowOpacity()
    {
        var card = ColourCard();
        var sample = Regex.Match(card, @"<Border[^>]*Name=""SampleCard""[^>]*>");

        sample.Success.Should().BeTrue();
        sample.Value.Should().Contain("Background=\"{Binding TonalRamp.SurfaceBrush}\"")
            .And.Contain("CornerRadius=\"{Binding SampleCardCornerRadius}\"")
            .And.Contain("Opacity=\"{Binding AppWindowOpacity}\"");
    }

    [Fact]
    public void TheLookCardHidesWallpaperControlsUnlessWallpaperIsSelected()
    {
        var card = CardAfterHeader("Custom_SectionLook");

        card.Should().MatchRegex(@"SelectedItem=""\{Binding CanvasBackgroundType[,}]");
        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWallpaperBackgroundSelected\}""[\s\S]*?WallpaperSource[\s\S]*?WallpaperBlur");
        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWindowOpacityRelevant\}""[\s\S]*?AppWindowOpacity");
    }

    [Fact]
    public void FineTuningIsAnExpanderHoldingGeometryAndTypography()
    {
        var card = CardAfterHeader("Custom_AdvancedTuning");

        card.Should().StartWith("<Expander");
        foreach (var binding in new[] { "CornerRadius", "GlassOpacity", "GlowStrength", "SelectedPageTitleFont", "SelectedBodyFont", "UiScale" })
            card.Should().Contain($"{{Binding {binding}", $"{binding} moved into Fine-tuning");
    }

    [Fact]
    public void BehaviourHasSplashWithPreviewHardwareSyncAndReducedMotion()
    {
        var card = CardAfterHeader("Custom_SectionBehaviour");

        card.Should().MatchRegex(@"SelectedItem=""\{Binding SplashStyle[,}]");
        card.Should().Contain("PreviewSplashCommand");
        card.Should().MatchRegex(@"IsChecked=""\{Binding SyncWithHardware[,}]");
        card.Should().MatchRegex(@"IsChecked=""\{Binding IsReducedMotion[,}]");
    }

    [Fact]
    public void ResetSitsBelowTheLastCard()
    {
        var markup = PanelMarkup();
        markup.LastIndexOf("ResetToDefaultCommand", System.StringComparison.Ordinal)
            .Should().BeGreaterThan(markup.LastIndexOf("</material:Card>", System.StringComparison.Ordinal));
    }

    private static string ColourCard() => CardAfterHeader("Custom_SectionColor");

    private static string SavedPalettesCard() => CardAfterHeader("Custom_SectionSavedPalettes");

    /// <summary>The markup from a card's header key to that card's closing tag (or the Expander's).</summary>
    private static string CardAfterHeader(string headerKey)
    {
        var markup = PanelMarkup();
        var start = markup.IndexOf($"Localize {headerKey}}}", System.StringComparison.Ordinal);
        start.Should().BeGreaterOrEqualTo(0, $"{headerKey} must be on the sheet");
        // Back up to the card/expander that owns the header.
        var cardStart = markup.LastIndexOf("<material:Card", start, System.StringComparison.Ordinal);
        var expanderStart = markup.LastIndexOf("<Expander", start, System.StringComparison.Ordinal);
        var isExpander = expanderStart > cardStart;
        var open = isExpander ? expanderStart : cardStart;
        var close = markup.IndexOf(isExpander ? "</Expander>" : "</material:Card>", start, System.StringComparison.Ordinal);
        return markup.Substring(open, close - open);
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

In `PaletteStudioWiringTests.cs:24-29` delete `[InlineData("SeedTone")]`. In
`StartupViewArgumentTests.cs:72-80` add `"Personalize"` to the expected list and rename the test
to `Navigators_CoversExactlyTheTenScriptableViews`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~PersonalizationSheetLayoutTests|FullyQualifiedName~StartupViewArgumentTests"`
Expected: every `PersonalizationSheetLayoutTests` case fails (headers absent); the navigator count test fails.

- [ ] **Step 3: View-model and shell additions**

`CustomizationViewModel.cs`:

```csharp
    /// <summary>The sample card's corners: the live slider value, capped like the preset tiles are.</summary>
    public CornerRadius SampleCardCornerRadius => new(Math.Clamp(CornerRadius, 0, 24));

    /// <summary>Plays the selected splash over the shell (spec section 8).</summary>
    [RelayCommand]
    private void PreviewSplash() => _shell.ReplayWelcomeSplash();
```

In `OnCornerRadiusChanged` (`:777-794`) add `OnPropertyChanged(nameof(SampleCardCornerRadius));`
as its first line (before the snapping logic), and `using Avalonia;` for `CornerRadius`.

`ShellViewModel.cs`, after `_isWelcomeSplashMounted` (`:264`):

```csharp
    /// <summary>The view restarts the mounted SkiaSplashControl when this fires (Preview on the sheet).</summary>
    public event Action? SplashReplayRequested;

    /// <summary>Mounts the splash again and asks the view to restart it. Completion goes through the
    /// same <see cref="OnBootSequenceCompleted"/> path as the first run; the tutorial gate there
    /// stays false for anyone who has completed it.</summary>
    public void ReplayWelcomeSplash()
    {
        IsWelcomeSplashMounted = true;
        ShowWelcomeSplash = true;
        SplashReplayRequested?.Invoke();
    }
```

`ShellView.axaml.cs`, inside the `if (bootSplash != null && !_bootSplashHooked)` block (`:192-215`)
after the `SequenceCompleted` subscription:

```csharp
            if (DataContext is ShellViewModel splashOwner)
                splashOwner.SplashReplayRequested += bootSplash.Restart;
```

`SkiaSplashControl.cs`, after `OnAttachedToVisualTree` (`:76-88`):

```csharp
    /// <summary>Plays the current <see cref="SplashStyle"/> from the start while attached — the sheet's Preview.</summary>
    public void Restart()
    {
        _variant = CreateVariant(SplashStyle);
        _elapsed = 0;
        _completed = false;
        _skipping = false;
        _skipElapsed = 0;
        _stopwatch.Restart();
        _timer.Start();
        InvalidateVisual();
    }
```

`StartupViewArgument.cs:45-46`: add `["Personalize"] = vm => vm.NavigateToCustomization(),` after
`About`, and extend the `:30-34` summary: "Personalize opens the settings side sheet".
`docs/UI-PALETTE-SWEEP.md:63-73` gains the row `| Personalize | The Personalize side sheet over Home |`
(the sweep's `$Script:Views` list stays at nine — the sheet is a drawer, not a page).

- [ ] **Step 4: Restructure the view**

Rewrite the body of `remex.desktop/Views/PersonalizationPanelView.axaml` (everything inside
`<StackPanel Spacing="20">`, `:16-565`) as six cards plus the Reset button. Every existing control
keeps its bindings unless a change is named. Header `TextBlock`s use the existing card-header
style: `FontSize="11" FontWeight="SemiBold" Foreground="{DynamicResource AccentPrimaryBrush}" LetterSpacing="2"`.

**Card 1 — `Custom_SectionColor`** (`material:Card Padding="20"`, `StackPanel Spacing="16"`):

1. Source row — the same ComboBox/PrefixedLabelConverter pattern as the background picker (`:318-331`), prefix `Custom_Source_`:

```xml
                <Grid ColumnDefinitions="*,2*">
                    <TextBlock Text="{local:Localize Custom_ColorSource}" VerticalAlignment="Center"/>
                    <ComboBox Grid.Column="1" SelectedItem="{Binding ColorSource}" ItemsSource="{Binding AvailableColorSources}" HorizontalAlignment="Left" Width="160" AutomationProperties.Name="{local:Localize Custom_ColorSource}">
                        <ComboBox.ItemTemplate>
                            <DataTemplate x:CompileBindings="False">
                                <TextBlock>
                                    <TextBlock.Text>
                                        <MultiBinding Converter="{x:Static local:PrefixedLabelConverter.Instance}" ConverterParameter="Custom_Source_">
                                            <Binding Path="CultureTag" Source="{x:Static svc:LocalizationService.Instance}"/>
                                            <Binding/>
                                        </MultiBinding>
                                    </TextBlock.Text>
                                </TextBlock>
                            </DataTemplate>
                        </ComboBox.ItemTemplate>
                    </ComboBox>
                </Grid>
```

2. Windows accent block: `<StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding IsWindowsAccentSource}">` holding
   `<Border x:CompileBindings="False" Width="28" Height="28" CornerRadius="6" Background="{Binding SourceAccentHex}" BorderBrush="{DynamicResource CardBorderBrush}" BorderThickness="1" ToolTip.Tip="{Binding SourceAccentHex}" AutomationProperties.Name="{local:Localize Custom_CurrentWindowsAccent}"/>`
   and `<TextBlock Text="{Binding SourceAccentHex}" VerticalAlignment="Center" FontFamily="Consolas, monospace" FontSize="12" Foreground="{DynamicResource TextSecondaryBrush}"/>`.
3. Wallpaper block: `<StackPanel Spacing="6" IsVisible="{Binding IsWallpaperSource}">` holding a
   `<Button Classes="compact" Content="{local:Localize Custom_RefreshWallpaperSeeds}" Command="{Binding RefreshWallpaperSeedsCommand}" AutomationProperties.Name="{local:Localize Custom_RefreshWallpaperSeeds}"/>`
   and the existing candidate `ItemsControl` from `:159-170` (`WallpaperSeedCandidates`, swatches bound to `SelectWallpaperSeedCommand`).
4. Custom block: `<StackPanel Spacing="12" IsVisible="{Binding IsCustomSource}">` holding, in
   order, the wheel (`:81-88` verbatim), the Hue slider row (`:91-95` verbatim), the hex row
   (`:114-125` verbatim), and the recents row (`:127-144` verbatim, still `SetAccentCommand`).
   Two nested `StackPanel`s close this block (the recents row's own, then the Custom block's) —
   the guard regex relies on that.
5. Vibrancy row — the old Chroma row (`:97-101`) with its label key changed to `Custom_Vibrancy`
   (both `Text=` and `AutomationProperties.Name=`); the slider still binds `SeedChroma`.
6. Contrast row: `:205-217` verbatim.
7. Strategy: `:346-395` verbatim except the caption key `Custom_SchemeVariant` → `Custom_Strategy`.
8. Preview: a caption `<TextBlock Text="{local:Localize Custom_Preview}" Theme="{StaticResource CaptionTextBlock}" Foreground="{DynamicResource TextSecondaryBrush}"/>`,
   then the tonal-ramp block `:401-498` verbatim (drop its own `Custom_TonalRamp` caption line
   `:402` — the Preview caption replaces it), then the sample card:

```xml
                    <Border Name="SampleCard" Background="{Binding TonalRamp.SurfaceBrush}" CornerRadius="{Binding SampleCardCornerRadius}" Opacity="{Binding AppWindowOpacity}" BorderBrush="{DynamicResource CardBorderBrush}" BorderThickness="1" Padding="12">
                        <StackPanel Spacing="8">
                            <TextBlock Text="{local:Localize Custom_SampleCardTitle}" FontWeight="SemiBold" Foreground="{Binding TonalRamp.OnSurfaceBrush}"/>
                            <TextBlock Text="{local:Localize Custom_SampleCardBody}" FontSize="12" TextWrapping="Wrap" Foreground="{Binding TonalRamp.OnSurfaceBrush}"/>
                            <Border Background="{Binding TonalRamp.PrimaryBrush}" CornerRadius="{Binding SampleCardCornerRadius}" Padding="10,5" HorizontalAlignment="Left">
                                <TextBlock Text="{local:Localize Btn_Apply}" FontSize="12" FontWeight="SemiBold" Foreground="{Binding TonalRamp.OnPrimaryBrush}"/>
                            </Border>
                        </StackPanel>
                    </Border>
```

Deleted from the old markup, not moved: the Tone slider row (`:103-107`); the "Seeds from this
PC" buttons (`:150-155` — the source picker replaces them; `MatchWindowsAccentCommand` and
`SeedFromWallpaperCommand` stay on the VM for the command palette but are unbound here); the
accent swatch gallery row and its "+" flyout (`:270-308`).

**Card 2 — `Custom_SectionMode`**: the base-mode row `:190-203` verbatim (its inner
`Custom_BaseMode`/`Custom_BaseModeDesc` labels stay; the card header is the new key).

**Card 3 — `Custom_SectionLook`**: the background-mode row (`:316-332`), then the Task 5
wallpaper block (`IsWallpaperBackgroundSelected`), then the window-opacity row (`:333-339`, its
`IsVisible` already `IsWindowOpacityRelevant`).

**Section 4 — Fine-tuning**: not a Card but

```xml
        <Expander Header="{local:Localize Custom_AdvancedTuning}" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
            <StackPanel Spacing="20" Margin="0,12,0,0">
                …corners (:226-238), glass opacity (:240-252), glow (:254-266), a Separator,
                title font (:537-540), body font (:541-544), UI scale (:545-551), font warning (:557-559)…
            </StackPanel>
        </Expander>
```

**Card 5 — `Custom_SectionBehaviour`**: the splash row (`:500-509`) with a Preview button added
inside its right column — wrap the ComboBox in `<StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8">`
and add `<Button Classes="compact" Content="{local:Localize Custom_Preview}" Command="{Binding PreviewSplashCommand}" AutomationProperties.Name="{local:Localize Custom_Preview}"/>`
after it; then hardware sync (`:514-520`) and reduced motion (`:523-529`) verbatim.

**Card 6 — `Custom_SectionSavedPalettes`**: caption `Custom_BuiltInPresets` + the presets
`ItemsControl` (`:31-66` verbatim, keeping the three-column comment), then the Task 7 user-palette
block (`Custom_UserPalettes` … Save current row), then the share row (`:177-184`) with its
`Custom_SharePaletteHeader` caption dropped and the three buttons kept (Copy as AXAML is a shipped
RemEx-a7uzb feature the spec does not retire; Import and Export are the spec's).

Then `<Button Content="{local:Localize Custom_Reset}" …>` (`:563`) as the last child.

Remove the now-empty `<UserControl.Styles>` block (`:10-14`) only if it is genuinely empty; it
currently holds a comment and no styles, so delete it with its comment.

- [ ] **Step 5: Add the resx keys**

Scratch `task8-keys.json`:

```json
{
  "Custom_SectionColor": {"en": "COLOUR", "es": "COLOR", "fr": "COULEUR", "hi": "रंग", "id": "WARNA", "pl": "KOLOR", "pt-BR": "COR", "tr": "RENK", "uk": "КОЛІР"},
  "Custom_SectionMode": {"en": "MODE", "es": "MODO", "fr": "MODE CLAIR OU SOMBRE", "hi": "मोड", "id": "MODE TAMPILAN", "pl": "TRYB", "pt-BR": "MODO", "tr": "MOD", "uk": "РЕЖИМ"},
  "Custom_SectionLook": {"en": "LOOK", "es": "ASPECTO", "fr": "APPARENCE", "hi": "रूप", "id": "TAMPILAN", "pl": "WYGLĄD", "pt-BR": "APARÊNCIA", "tr": "GÖRÜNÜM", "uk": "ВИГЛЯД"},
  "Custom_SectionBehaviour": {"en": "BEHAVIOUR", "es": "COMPORTAMIENTO", "fr": "COMPORTEMENT", "hi": "व्यवहार", "id": "PERILAKU", "pl": "ZACHOWANIE", "pt-BR": "COMPORTAMENTO", "tr": "DAVRANIŞ", "uk": "ПОВЕДІНКА"},
  "Custom_SectionSavedPalettes": {"en": "SAVED PALETTES", "es": "PALETAS GUARDADAS", "fr": "PALETTES ENREGISTRÉES", "hi": "सहेजे गए पैलेट", "id": "PALET TERSIMPAN", "pl": "ZAPISANE PALETY", "pt-BR": "PALETAS SALVAS", "tr": "KAYITLI PALETLER", "uk": "ЗБЕРЕЖЕНІ ПАЛІТРИ"},
  "Custom_ColorSource": {"en": "Source", "es": "Origen", "fr": "Origine", "hi": "स्रोत", "id": "Sumber", "pl": "Źródło", "pt-BR": "Origem", "tr": "Kaynak", "uk": "Джерело"},
  "Custom_Source_WindowsAccent": {"en": "Windows accent", "es": "Color de Windows", "fr": "Couleur d'accentuation Windows", "hi": "Windows एक्सेंट", "id": "Aksen Windows", "pl": "Akcent Windows", "pt-BR": "Cor de destaque do Windows", "tr": "Windows vurgu rengi", "uk": "Акцент Windows"},
  "Custom_Source_Wallpaper": {"en": "Wallpaper", "es": "Fondo de pantalla", "fr": "Fond d'écran", "hi": "वॉलपेपर", "id": "Gambar latar", "pl": "Tapeta", "pt-BR": "Papel de parede", "tr": "Duvar kağıdı", "uk": "Шпалери"},
  "Custom_Source_Custom": {"en": "Custom", "es": "Personalizado", "fr": "Personnalisée", "hi": "कस्टम", "id": "Kustom", "pl": "Własny", "pt-BR": "Personalizado", "tr": "Özel", "uk": "Власний"},
  "Custom_CurrentWindowsAccent": {"en": "Current Windows accent colour", "es": "Color de Windows actual", "fr": "Couleur d'accentuation Windows actuelle", "hi": "वर्तमान Windows एक्सेंट रंग", "id": "Warna aksen Windows saat ini", "pl": "Bieżący kolor akcentu Windows", "pt-BR": "Cor de destaque atual do Windows", "tr": "Geçerli Windows vurgu rengi", "uk": "Поточний колір акценту Windows"},
  "Custom_RefreshWallpaperSeeds": {"en": "Refresh", "es": "Actualizar", "fr": "Actualiser", "hi": "रीफ़्रेश करें", "id": "Segarkan", "pl": "Odśwież", "pt-BR": "Atualizar", "tr": "Yenile", "uk": "Оновити"},
  "Custom_Vibrancy": {"en": "Vibrancy", "es": "Viveza", "fr": "Vivacité", "hi": "जीवंतता", "id": "Kecerahan", "pl": "Żywość", "pt-BR": "Vivacidade", "tr": "Canlılık", "uk": "Насиченість"},
  "Custom_Strategy": {"en": "Strategy", "es": "Estrategia", "fr": "Stratégie", "hi": "रणनीति", "id": "Strategi", "pl": "Strategia", "pt-BR": "Estratégia", "tr": "Strateji", "uk": "Стратегія"},
  "Custom_Preview": {"en": "Preview", "es": "Vista previa", "fr": "Aperçu", "hi": "पूर्वावलोकन", "id": "Pratinjau", "pl": "Podgląd", "pt-BR": "Pré-visualização", "tr": "Önizleme", "uk": "Попередній перегляд"},
  "Custom_SampleCardTitle": {"en": "Sample card", "es": "Tarjeta de ejemplo", "fr": "Carte d'exemple", "hi": "नमूना कार्ड", "id": "Kartu contoh", "pl": "Przykładowa karta", "pt-BR": "Cartão de exemplo", "tr": "Örnek kart", "uk": "Зразок картки"},
  "Custom_SampleCardBody": {"en": "Cards and text will look like this.", "es": "Las tarjetas y el texto se verán así.", "fr": "Les cartes et le texte ressembleront à ceci.", "hi": "कार्ड और टेक्स्ट ऐसे दिखेंगे।", "id": "Kartu dan teks akan terlihat seperti ini.", "pl": "Karty i tekst będą wyglądać tak.", "pt-BR": "Os cartões e o texto ficarão assim.", "tr": "Kartlar ve metin böyle görünecek.", "uk": "Картки й текст виглядатимуть так."}
}
```

Run: `uv run python scripts/resx_add_keys.py <path>` → nine `+16` lines.

- [ ] **Step 6: Run the tests**

Run:
```
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~PersonalizationSheetLayoutTests|FullyQualifiedName~PaletteStudioWiringTests|FullyQualifiedName~SeedPresetCatalogTests|FullyQualifiedName~SavedPalettesWiringTests|FullyQualifiedName~StartupViewArgumentTests|FullyQualifiedName~LocalizationKeyReferenceTests"
pwsh scripts/check-localization.ps1
```
Expected: 0 warnings; all pass (count +10); `errors=0`, no new warnings.
`SeedPresetCatalogTests` (`:286-295`) still finds `ItemsSource="{Binding SchemeVariantStrips}"` and
the presets gallery with no hex literal; `PaletteStudioWiringTests` finds a writer for
`ThemeContrast`, `ThemeModeIndex`, `SeedHue`, `SeedChroma`, the wheel, and `SeedCommitted=`.

- [ ] **Step 7: Eyes pass**

`pwsh scripts/ui-hotreload.ps1 -Start -AppArgs '--view Personalize'` → the sheet opens on
launch. `pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree`: six headers in order, the source
picker on Windows accent with the swatch, no Tone slider, the sample card in the live palette.
Switch source to Custom (wheel appears), Wallpaper (candidates + Refresh). Click Preview under
Behaviour: the splash plays over the shell and fades. Drag Corners: the sample card's corners
follow. Check both Light and Dark. `-Stop`.

- [ ] **Step 8: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add remex.desktop/Views/PersonalizationPanelView.axaml remex.desktop/ViewModels/CustomizationViewModel.cs remex.desktop/ViewModels/ShellViewModel.cs remex.desktop/Views/ShellView.axaml.cs remex.desktop/Controls/Splash/SkiaSplashControl.cs remex.desktop/Services/StartupViewArgument.cs docs/UI-PALETTE-SWEEP.md remex.desktop.tests/Views/PersonalizationSheetLayoutTests.cs remex.desktop.tests/ViewModels/PaletteStudioWiringTests.cs remex.desktop.tests/Services/StartupViewArgumentTests.cs remex.desktop/Localization/Strings.resx remex.desktop/Localization/Strings.es.resx remex.desktop/Localization/Strings.fr.resx remex.desktop/Localization/Strings.hi.resx remex.desktop/Localization/Strings.id.resx remex.desktop/Localization/Strings.pl.resx remex.desktop/Localization/Strings.pt-BR.resx remex.desktop/Localization/Strings.tr.resx remex.desktop/Localization/Strings.uk.resx
git commit -m "feat(desktop): Personalization sheet reflowed into Colour, Mode, Look, Fine-tuning, Behaviour and Saved palettes (RemEx-<bead>)"
```

---

### Task 9: Splash default flips to Cosmic Zoom in the three places

**Files:**
- Modify: `remex.core/Models/DashboardProfile.cs:283-285` (`SplashStyle` default `"CosmicZoom"`)
- Modify: `remex.desktop/Models/SeedPreset.cs:88` (BaseDarkGlass `SplashStyle: "CosmicZoom"`)
- Modify: `remex.desktop/Controls/Splash/SkiaSplashControl.cs:25-26` (registered default `"CosmicZoom"`), `:41` (`_variant = new CosmicZoomVariant()`)
- Test: `remex.desktop.tests/Controls/SplashDefaultTests.cs`

**Interfaces:**
- Consumes: `SeedPresetCatalog.Default` (`SeedPreset.cs:132`), `SkiaSplashControl.SplashStyleProperty`.
- Produces: no new members; the default string `"CosmicZoom"` in all three places (the Task 1 migration already flips a stored `RemexCommand`).

- [ ] **Step 1: Write the failing test**

Create `remex.desktop.tests/Controls/SplashDefaultTests.cs`:

```csharp
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>Cosmic Zoom is the splash default in the model, the default preset, and the Skia
/// control's fallback (spec section 8). Three places, one answer.</summary>
public class SplashDefaultTests
{
    [Fact]
    public void TheModelDefaultsToCosmicZoom()
    {
        new CustomizationSettings().SplashStyle.Should().Be("CosmicZoom");
    }

    [Fact]
    public void TheDefaultPresetCarriesCosmicZoom()
    {
        SeedPresetCatalog.Default.SplashStyle.Should().Be("CosmicZoom");
    }

    [Fact]
    public void TheSkiaControlFallsBackToCosmicZoom()
    {
        // The control needs an Avalonia runtime to construct, so its two defaults are pinned as source.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "Splash", "SkiaSplashControl.cs"));

        source.Should().Contain("nameof(SplashStyle), \"CosmicZoom\")", "the registered StyledProperty default");
        source.Should().Contain("ISplashVariant _variant = new CosmicZoomVariant();", "the pre-attach variant");
        source.Should().NotContain("nameof(SplashStyle), \"RemexCommand\")");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~SplashDefaultTests"`
Expected: all three FAIL.

- [ ] **Step 3: Flip the three defaults**

- `DashboardProfile.cs:285`: `public string SplashStyle { get; init; } = "CosmicZoom";`
- `SeedPreset.cs:88`: `SplashStyle: "CosmicZoom"),`
- `SkiaSplashControl.cs:26`: `AvaloniaProperty.Register<SkiaSplashControl, string>(nameof(SplashStyle), "CosmicZoom");`
  and `:41`: `private ISplashVariant _variant = new CosmicZoomVariant();`

`CreateVariant` (`:150-155`) keeps its `_ => new RemexCommandVariant()` arm: a persisted
`RemexCommand` still plays the command sequence when chosen.

- [ ] **Step 4: Run the tests**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~SplashDefaultTests|FullyQualifiedName~SeedPresetCatalogTests|FullyQualifiedName~CustomizationSettingsRoundTripTests|FullyQualifiedName~DashboardLayoutClobberTests"`
Expected: 0 warnings; all pass (count +3).

- [ ] **Step 5: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add remex.core/Models/DashboardProfile.cs remex.desktop/Models/SeedPreset.cs remex.desktop/Controls/Splash/SkiaSplashControl.cs remex.desktop.tests/Controls/SplashDefaultTests.cs
git commit -m "feat(desktop): Cosmic Zoom is the splash default in the model, the default preset and the control (RemEx-<bead>)"
```

---

### Task 10: Localisation — retire the strings the sheet no longer shows, full check

**Files:**
- Create: `scripts/resx_remove_keys.py`
- Modify: nine `remex.desktop/Localization/Strings*.resx` (remove 23 keys)
- Test: `remex.desktop.tests/Localization/RetiredPersonalizationStringsTests.cs`

**Interfaces:**
- Consumes: the resx layout (`<data name="…" xml:space="preserve">` + `<value>` + `</data>`).
- Produces: `scripts/resx_remove_keys.py <key> [<key> …]` — removes each key from all nine files; refuses to run if any key is still referenced in `remex.desktop` code or XAML.

Keys removed (every one unreferenced after Task 8; verify with the grep in Step 3):
the spec's four — `Custom_SeedTone`, `Custom_Scheme_Content`, `Custom_Scheme_Spritz`,
`Custom_BgType_Mica` — plus the nineteen the reflow orphaned: `Custom_BasePresets`,
`Custom_PaletteStudio`, `Custom_Atmosphere`, `Custom_Typography`, `Custom_SeedChroma`,
`Custom_SchemeVariant`, `Custom_TonalRamp`, `Custom_AccentColor`,
`Custom_SelectColorPaletteTooltip`, `Custom_SelectNeonVioletTooltip`,
`Custom_SelectCyberCyanTooltip`, `Custom_SelectHotPinkTooltip`, `Custom_SelectSolarGoldTooltip`,
`Custom_SelectEmeraldGreenTooltip`, `Custom_SelectCrimsonRoseTooltip`,
`Custom_AddCustomColorTooltip`, `Custom_CustomAccentColorTitle`, `Custom_SystemSeedHeader`,
`Custom_SharePaletteHeader`. (`Custom_MatchWindowsAccent` and `Custom_SeedFromWallpaper` stay:
the command palette may still surface those commands — check `grep -rn` before deciding; if
nothing references them, add them to the list.)

- [ ] **Step 1: Write the failing guard**

Create `remex.desktop.tests/Localization/RetiredPersonalizationStringsTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>The strings the redesigned sheet retired (spec section 10) are gone from every language.</summary>
public class RetiredPersonalizationStringsTests
{
    private static readonly string[] Retired =
    {
        "Custom_SeedTone", "Custom_Scheme_Content", "Custom_Scheme_Spritz", "Custom_BgType_Mica",
    };

    private static readonly string[] Files =
    {
        "Strings.resx", "Strings.es.resx", "Strings.fr.resx", "Strings.hi.resx", "Strings.id.resx",
        "Strings.pl.resx", "Strings.pt-BR.resx", "Strings.tr.resx", "Strings.uk.resx",
    };

    [Fact]
    public void TheRetiredKeysAreAbsentFromAllNineFiles()
    {
        foreach (var file in Files)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Localization", file));
            foreach (var key in Retired)
                Regex.IsMatch(text, $@"<data name=""{Regex.Escape(key)}""").Should().BeFalse($"{file} still defines {key}");
        }
    }

    [Fact]
    public void NothingInTheDesktopStillAsksForThem()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs") || p.EndsWith(".axaml")) && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.EndsWith("Strings.Designer.cs"));

        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (var key in Retired)
                text.Should().NotContain(key, $"{path} references a retired string");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~RetiredPersonalizationStringsTests"`
Expected: `TheRetiredKeysAreAbsentFromAllNineFiles` FAILS (the keys still exist).

- [ ] **Step 3: Write the removal script and run it**

Create `scripts/resx_remove_keys.py`:

```python
"""Remove localisation keys from all nine Strings*.resx files.

Usage:  uv run python scripts/resx_remove_keys.py Key_One Key_Two …

Refuses to run when any key is still referenced by a .cs or .axaml file under remex.desktop
(Strings.Designer.cs excluded), because a removed-but-referenced key renders as its raw name on
screen in every language. Preserves BOM and line endings; refuses to write a NUL.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DESKTOP = os.path.join(ROOT, "remex.desktop")
LOC = os.path.join(DESKTOP, "Localization")
FILES = ["Strings.resx", "Strings.es.resx", "Strings.fr.resx", "Strings.hi.resx", "Strings.id.resx",
         "Strings.pl.resx", "Strings.pt-BR.resx", "Strings.tr.resx", "Strings.uk.resx"]


def references(key: str) -> list[str]:
    hits = []
    for dirpath, dirnames, filenames in os.walk(DESKTOP):
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin", "Localization")]
        for name in filenames:
            if not (name.endswith(".cs") or name.endswith(".axaml")):
                continue
            path = os.path.join(dirpath, name)
            with open(path, "rb") as f:
                if key.encode("utf-8") in f.read():
                    hits.append(os.path.relpath(path, ROOT))
    return hits


def main(keys: list[str]) -> None:
    blocked = {k: references(k) for k in keys}
    blocked = {k: v for k, v in blocked.items() if v}
    if blocked:
        for k, v in blocked.items():
            print(f"{k} is still referenced by: {', '.join(v)}")
        sys.exit("refusing to remove referenced keys")
    for name in FILES:
        full = os.path.join(LOC, name)
        with open(full, "rb") as f:
            raw = f.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        removed = 0
        for key in keys:
            pattern = re.compile(r'[ \t]*<data name="' + re.escape(key) + r'"[^>]*>.*?</data>[ \t]*\r?\n', re.DOTALL)
            text, n = pattern.subn("", text)
            removed += n
        if "\x00" in text:
            sys.exit(f"{name}: refusing to write a NUL byte")
        with open(full, "wb") as f:
            f.write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        print(f"{name}: -{removed}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])
```

First confirm nothing references the list (the script also refuses if something does):

```
grep -rn "Custom_SeedTone\|Custom_Scheme_Content\|Custom_Scheme_Spritz\|Custom_BgType_Mica\|Custom_BasePresets\|Custom_PaletteStudio\|Custom_Atmosphere\|Custom_Typography\|Custom_SeedChroma\|Custom_SchemeVariant\|Custom_TonalRamp\"\|Custom_AccentColor\|Custom_SelectColorPaletteTooltip\|Custom_Select.*Tooltip\|Custom_AddCustomColorTooltip\|Custom_CustomAccentColorTitle\|Custom_SystemSeedHeader\|Custom_SharePaletteHeader\|Custom_MatchWindowsAccent\|Custom_SeedFromWallpaper" remex.desktop --include=*.cs --include=*.axaml | grep -v Localization/
```

Expected: no output (or only the two `Custom_MatchWindowsAccent`/`Custom_SeedFromWallpaper`
references, in which case keep those two keys). Then:

```
uv run python scripts/resx_remove_keys.py Custom_SeedTone Custom_Scheme_Content Custom_Scheme_Spritz Custom_BgType_Mica Custom_BasePresets Custom_PaletteStudio Custom_Atmosphere Custom_Typography Custom_SeedChroma Custom_SchemeVariant Custom_TonalRamp Custom_AccentColor Custom_SelectColorPaletteTooltip Custom_SelectNeonVioletTooltip Custom_SelectCyberCyanTooltip Custom_SelectHotPinkTooltip Custom_SelectSolarGoldTooltip Custom_SelectEmeraldGreenTooltip Custom_SelectCrimsonRoseTooltip Custom_AddCustomColorTooltip Custom_CustomAccentColorTitle Custom_SystemSeedHeader Custom_SharePaletteHeader
```

Expected: nine lines `Strings….resx: -23` (a locale that never had one of them prints a smaller
number; that is fine — the point is that none remain).

- [ ] **Step 4: Run the full localisation check and the tests**

Run:
```
pwsh scripts/check-localization.ps1
dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo
dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~RetiredPersonalizationStringsTests|FullyQualifiedName~LocalizationKeyReferenceTests|FullyQualifiedName~LocalizedPropertyRefreshTests"
```
Expected: the summary line reads `errors=0` and its `warnings=` number is **no higher than the
number on `main` before Task 1** (record both in the bead — Tasks 2–8 each checked "no new
warnings", so this is the cumulative confirmation); all listed tests pass (count +2). If the
warning count rose, the offending translation is named in the output: fix that value in the
locale file it names (through a one-key JSON and `resx_remove_keys.py` + `resx_add_keys.py`, or
an `Edit` on the single `<value>` line — a single-line edit is not a bulk edit).

- [ ] **Step 5: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID.

```bash
git add scripts/resx_remove_keys.py remex.desktop.tests/Localization/RetiredPersonalizationStringsTests.cs remex.desktop/Localization/Strings.resx remex.desktop/Localization/Strings.es.resx remex.desktop/Localization/Strings.fr.resx remex.desktop/Localization/Strings.hi.resx remex.desktop/Localization/Strings.id.resx remex.desktop/Localization/Strings.pl.resx remex.desktop/Localization/Strings.pt-BR.resx remex.desktop/Localization/Strings.tr.resx remex.desktop/Localization/Strings.uk.resx
git commit -m "chore(desktop): retire the Personalization strings the redesigned sheet no longer shows (RemEx-<bead>)"
```

---

### Task 11: Sweep cells, ledger rows, docs

**Files:**
- Modify: `scripts/ui-palette-sweep.ps1:90-120` (matrix comment + two cells with `Background`/`WallpaperBlur` fields), `:222-232` (profile writer: schema 3, optional background fields)
- Modify: `docs/UI-PALETTE-SWEEP.md:23-28` (seed table note), `:111-125` (two ledger rows), the matrix prose
- Modify: `remex.desktop.tests/Scripts/PaletteSweepScriptTests.cs:39` (`MatrixHasExactlyThirteenCells` → fifteen)
- Modify: `docs/REGRESSION-GUARDS.md` "Desktop shell" section (one new guard: the Aurora reduced-motion pairing)

**Interfaces:**
- Consumes: the cell schema (`Id`, `ThemeId`, `Seed`, `SchemeVariant`, `Mode`, `Contrast`), the profile writer's `Add-Member` pattern, the ledger's column set.
- Produces: cells `Aurora-Light` and `Wallpaper-Dark-B06`; cell fields `Background` (string) and `WallpaperBlur` (double), both optional; profile keys `canvasBackgroundType`, `wallpaperSource`, `wallpaperBlur`, `colorSource` written only when a cell carries `Background`.

- [ ] **Step 1: Update the script test first**

In `remex.desktop.tests/Scripts/PaletteSweepScriptTests.cs:39` rename
`MatrixHasExactlyThirteenCells` to `MatrixHasExactlyFifteenCells` and change its expected count
to 15. Read the test body: it counts `[ordered]@{ Id = '…'` rows — the two new rows below use
the same shape so the count rises by two. Add:

```csharp
    [Fact]
    public void TheTwoBackgroundCellsCarryTheirModeAndBlur()
    {
        var text = ScriptText();

        text.Should().MatchRegex(@"Id = 'Aurora-Light';[^\n]*Background = 'Aurora'[^\n]*Mode = 'Light'");
        text.Should().MatchRegex(@"Id = 'Wallpaper-Dark-B06';[^\n]*Seed = '#00FF00'[^\n]*Background = 'Wallpaper'[^\n]*WallpaperBlur = 0\.6[^\n]*Mode = 'Dark'");
        text.Should().Contain("'canvasBackgroundType'", "a cell's background reaches the profile the host reads");
        text.Should().Contain("-NotePropertyValue 3 ", "the sweep writes the schema this build writes, or the host re-migrates the file");
    }
```

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~PaletteSweepScriptTests"`
Expected: the renamed count test and the new test FAIL.

- [ ] **Step 2: The cells and the writer**

In `scripts/ui-palette-sweep.ps1`, after the `Chroma-Dark-C1` row (`:119`) add:

```powershell
    # RemEx-ddynd: the two background modes the spec adds. Aurora on the default seed in LIGHT
    # mode (the harder case for a mesh built from tone-90 pastels); Wallpaper on the max-chroma
    # seed in dark mode at the default blur, desktop source, so the veil has a real picture to sit on.
    [ordered]@{ Id = 'Aurora-Light';      ThemeId = 'BaseDarkGlass'; Seed = '#6C4CFF'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 0.0; Background = 'Aurora' }
    [ordered]@{ Id = 'Wallpaper-Dark-B06'; ThemeId = 'Dynamic'; Seed = '#00FF00'; SchemeVariant = 'TonalSpot'; Background = 'Wallpaper'; WallpaperBlur = 0.6; Mode = 'Dark';  Contrast = 0.0 }
```

Update the matrix comment (`:90-102`): "… = 12, plus Default = 13, plus the two background cells
(Aurora-Light, Wallpaper-Dark-B06) = 15 cells."

In the profile writer (`:222-232`): change `-NotePropertyValue 2` to `-NotePropertyValue 3 ` for
`schemaVersion` (schema 3 is what this build writes; a 2 would be re-migrated on read — Spritz
renamed, splash flipped — which is not what a sweep cell asked for) and add after the
`themeMode` line:

```powershell
        # Background cells only; every other cell leaves the profile's own background in place.
        if ($cell.Contains('Background')) {
            $customization | Add-Member -NotePropertyName 'canvasBackgroundType' -NotePropertyValue $cell.Background -Force
            $customization | Add-Member -NotePropertyName 'colorSource'          -NotePropertyValue 'Custom'         -Force
            if ($cell.Background -eq 'Wallpaper') {
                $customization | Add-Member -NotePropertyName 'wallpaperSource' -NotePropertyValue 'Desktop'         -Force
                $customization | Add-Member -NotePropertyName 'wallpaperBlur'   -NotePropertyValue $cell.WallpaperBlur -Force
            }
        }
```

(`colorSource = Custom` so the sweep's seed is not overwritten by the coordinator following the
Windows accent — that is the Task 3 behaviour for `WindowsAccent`, and a sweep cell pins its seed.)

- [ ] **Step 3: The ledger and the docs**

`docs/UI-PALETTE-SWEEP.md`: after the seed table (`:28`) add a short paragraph: "Two cells vary
the background instead of the seed (RemEx-ddynd): **Aurora-Light** (Default seed, Aurora mesh,
light mode) and **Wallpaper-Dark-B06** (Chroma seed, real wallpaper at blur 0.6, dark mode). They
write `canvasBackgroundType` and, for Wallpaper, `wallpaperSource`/`wallpaperBlur`; every other
cell leaves the profile's background alone." Append two ledger rows after `Chroma-Dark-C1`
(`:125`), same columns, every automated view `not run`, RemoteDesktop `manual`:

```
| Aurora-Light | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Wallpaper-Dark-B06 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
```

Change "every cell × view marked **not run**" prose (`:106-110`) to say fifteen cells.

`docs/REGRESSION-GUARDS.md`, at the end of the "Desktop shell" section (after the
palette-transition-suppression guard, before "### Theme switch removes only…"), add:

```markdown
### Aurora's static `Opacity` must equal its first keyframe, and its animation must be class-gated

`remex.desktop/Controls/DashboardBackgroundControl.axaml` — the three `AuroraLayerN` rectangles
(RemEx-ddynd). Reduced motion is implemented by REMOVING the `aurora-animated` class (bound to
`!IsReducedMotion`), which stops the keyframe animation and lets the property fall back to the
rectangle's own `Opacity`. If that static value drifts from the first keyframe, switching reduced
motion on visibly jumps the mesh; if the `Style` selector loses `.aurora-animated`, the mesh keeps
animating with the setting supposedly off — silently, exactly like the chrome-suppression failure
above. `AuroraMeshTests.ReducedMotionFreezesTheMeshAtItsFirstKeyframeInsteadOfHidingIt` pins both
halves; a layer added later must satisfy the same pairing.
```

- [ ] **Step 4: Run the sweep once and the tests**

Run: `dotnet build remex.desktop.tests/remex.desktop.tests.csproj -c Release --nologo && dotnet test remex.desktop.tests/remex.desktop.tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~PaletteSweepScriptTests"`
Expected: 0 warnings; all pass (count +1).

Run: `pwsh scripts/ui-palette-sweep.ps1 -ListCells` → fifteen rows including the two new ids.
Then `pwsh scripts/ui-palette-sweep.ps1 -Cells Aurora-Light,Wallpaper-Dark-B06` → two cells ×
nine views captured under `%TEMP%\remex-ui\`; open the Home screenshots and confirm the Aurora
mesh reads on light, and the wallpaper shows blurred under the veil on dark. Record what was seen
in the bead; the ledger stays `not run` until RemEx-bmuji fills it, exactly as the existing rows
do. If `-Cells` is not the parameter's name on your checkout, `grep -n "param(" -A6 scripts/ui-palette-sweep.ps1`
(`:63-70`) shows it.

- [ ] **Step 5: Full gate and commit**

Run: `pwsh scripts/verify.ps1` then `-Check` → VALID. Confirm every doc is tracked:
`git check-ignore -v docs/UI-PALETTE-SWEEP.md docs/REGRESSION-GUARDS.md` prints nothing.

```bash
git add scripts/ui-palette-sweep.ps1 docs/UI-PALETTE-SWEEP.md docs/REGRESSION-GUARDS.md remex.desktop.tests/Scripts/PaletteSweepScriptTests.cs
git commit -m "docs(desktop): sweep cells and guard for the Aurora and Wallpaper background modes (RemEx-<bead>)"
```

---

## Self-review (applied before saving)

**Spec coverage.** §3 sheet order and every section's controls → Task 8 (guards pin the six
headers, the Colour card order, per-source visibility, Fine-tuning as an Expander, Behaviour's
Preview, Saved palettes with no seed setter); removed Tone slider, swatch gallery, Content/Spritz
names → Tasks 8 and 10. §4 every row of the model table and every migration rule (including the
all-four-at-once file and the reflection round trip) → Task 1; `BackgroundMaterial`/`SplashStyle`
defaults → Tasks 4 and 9 (explicitly transient, see Execution notes). §5 accent watcher (two
seconds, stops while hidden, resume via the Activated hook and the immediate poll on becoming
visible) and the shaped-seed rule → Task 3; wallpaper index → Task 3; vibrancy/contrast bound to
their fields for every source → Task 8 (`SeedChroma`/`ThemeContrast`); strategies incl. the
Monochrome fallback the 0.3.0 package forces → Task 2; normalisation wherever a strategy string
is read (profile at construction and migration, saved palette apply, import) → Tasks 2 and 7;
preview ramp + sample card → Task 8. §6 Aurora (own value, radius ×1.5, opacity up, tone-30/90
sets, `ResolveIsLight` via `isLightTheme`, reduced-motion freeze) → Task 4; Wallpaper (path
resolution, 0–48 px blur, surface veil at window opacity, picker copy downscaled to 2560 under
the per-user root, fallback to Solid + snackbar with the setting untouched, decode-once cache) →
Task 5; Mica removal with Acrylic keeping its hint → Task 6. §7 `SavedPalette`, presets first,
apply rules by source, Save current with an editable name, per-item delete, presets undeletable,
format v2 with v1 import → Tasks 1 and 7. §8 three default flips + Preview → Tasks 9 and 8.
§9 Linux hiding (`AvailableColorSources`/`AvailableWallpaperSources`), failure paths writing
nothing (watcher and coordinator no-op, `RequestSave`'s fallback refusal, copy failure returns
before any setter) → Tasks 3 and 5. §10 additions → Tasks 2, 4, 5, 7, 8; removals + check →
Task 10. §11 unit tests listed → Tasks 1–5, 7; source guards → Tasks 6, 8, 9; sweep + ledger →
Task 11; live checks → each task's eyes pass. Gap: none found. One deviation from the request's
task list is recorded in Execution notes (SavedPalette/migration in Task 1, localisation additions
per task).

**Placeholder scan.** Searched the plan for "TBD", "TODO", "implement later", "fill in",
"appropriate", "handle edge cases", "similar to Task": none. Every code step carries the code;
every run step carries the command and the expected result.

**Type consistency.** `ColorSources`/`WallpaperSources`/`SavedPalette` (Task 1) are the names used
in Tasks 3, 5, 7, 8, 11. `SchemeVariants.All/Normalize` (Task 2) is what Tasks 1 (after Task 2's
swap), 7 and the migration call. `AdoptSourceSeed`, `RefreshWallpaperSeedsAsync`,
`SelectWallpaperSeedCommand`, `RefreshWallpaperSeedsCommand`, `SourceAccentHex`,
`IsWindowsAccentSource/IsWallpaperSource/IsCustomSource` (Task 3) match the Task 8 markup and
guards. `IsWallpaperBackgroundSelected`, `IsWindowOpacityRelevant`, `IsImageWallpaperSource`,
`PickWallpaperImageCommand`, `WallpaperBitmap`, `WallpaperBlurRadius`, `EffectiveBackgroundType`
(Task 5) match the Task 5 control and the Task 8 guards. `SavedPalettes`, `NewPaletteName`,
`SaveCurrentPaletteCommand`, `ApplySavedPaletteCommand`, `DeleteSavedPaletteCommand`,
`SavedPaletteTileViewModel.Record/Renamed` (Task 7) match Tasks 7 and 8. `SampleCardCornerRadius`,
`PreviewSplashCommand`, `ReplayWelcomeSplash`, `SplashReplayRequested`, `SkiaSplashControl.Restart`
(Task 8) are defined and consumed in the same task. `AuroraPrimary/Secondary/Tertiary`,
`StringMatchConverter.IsAurora`, `DynamicColorGenerator.AuroraColors/AuroraSet` (Task 4) match
the Task 4 markup, tests and the Task 11 guard text.
