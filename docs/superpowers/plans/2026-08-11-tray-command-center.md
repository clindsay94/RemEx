# Tray Command Center Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the RemEx PC tray flyout with a resizable, movable command center — an action grid of PC-side controls, a status-aware tray menu, and no grey rectangle around its rounded corners.

**Architecture:** A new `TrayFlyoutViewModel` composes existing services (`PhonePresenceMonitor`, `ConnectionViewModel`, `HomeViewModel`, `ShellViewModel`) rather than growing any of them. All decision logic — geometry validation, column breakpoints, tile enablement, confirmation policy — lives in small pure classes so it is testable without an Avalonia app, matching this repo's existing test style (`LauncherLayoutMathTests`, `TrayTooltipThrottleTests`). Window geometry persists through a JSON store following the `FileTransferRootSettingsService` pattern.

**Tech Stack:** C# / .NET, Avalonia 11, CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), xunit.

**Spec:** `docs/superpowers/specs/2026-08-11-tray-command-center-design.md`
**Epic:** `RemEx-exfu7`

## Global Constraints

- **No PC→phone commands.** No message type exists in `remex.core/Messages` to carry one. Every control in this work targets **this PC** or navigates the local UI. Do not add a tile that implies otherwise, and do not reuse the name "Send to phone" (see `RemEx-74kfg`).
- **`ConfigureAwait` is banned** in this repo. Do not add it.
- **Four PC themes** — `BaseDarkGlass`, `CyberNOC`, `Monolith`, `SolarFlare`. **SolarFlare is the light one** (`App.axaml.cs:592`). Tokens only in XAML; no hardcoded `#RRGGBB`. Verified present in all four: `CardBackgroundBrush`, `CardBackgroundHoverBrush`, `CardBorderBrush`, `AccentPrimaryBrush`, `GlassBaseDarkBrush`.
- **9 locale files** — `remex.desktop/Localization/Strings.resx` plus `.es`, `.fr`, `.hi`, `.id`, `.pl`, `.pt-BR`, `.tr`, `.uk`. Every new string goes in all nine. Gate: `scripts/check-localization.ps1`.
- **Data paths go through `RemexDataPaths`**, never `Environment.SpecialFolder` directly (see the RemEx-mz9f note at `RemexSavefileService.cs:79`). Writes use `RemexDataPaths.WriteAllTextAtomicAsync`.
- **Commit gate:** `scripts/verify.ps1 -Check` must pass before every commit. Read the test **count**, not just the colour — a green run whose total did not rise did not run your new test.
- **Test namespace:** `Remex.Desktop.Tests`. `using Avalonia;` is available in the test project.
- Desktop-only. No Android changes.

## File Structure

| File | Responsibility |
|---|---|
| `remex.desktop/Services/TrayFlyoutGeometry.cs` (create) | The persisted record + the pure validator. No file I/O. |
| `remex.desktop/Services/TrayFlyoutLayoutStore.cs` (create) | JSON load/save under `RemexDataPaths`. No validation logic of its own — delegates to the validator. |
| `remex.desktop/Converters/TrayTileColumnsConverter.cs` (create) | Window width → grid column count. Pure. |
| `remex.desktop/ViewModels/TrayTileRules.cs` (create) | Tile enablement + confirmation policy. Pure, no Avalonia. |
| `remex.desktop/ViewModels/TrayFlyoutViewModel.cs` (create) | Composes presence, power commands, navigation, pinned sensors. Owns `IsPinned` and `Tiles`. |
| `remex.desktop/Views/TrayFlyoutWindow.axaml` (rewrite) | Status strip + action grid + sensor strip. |
| `remex.desktop/Views/TrayFlyoutWindow.axaml.cs` (rewrite) | Chrome, transient/pinned modes, drag, geometry persistence. |
| `remex.desktop/Views/TrayWindowCorners.cs` (create) | The DWM round-corner P/Invoke, Windows-guarded. |
| `remex.desktop/App.axaml` (modify, `:329-343`) | Strip the declared `NativeMenu` down to the `TrayIcon` element. |
| `remex.desktop/App.axaml.cs` (modify, `:403-431`, `:598-630`) | Build the tray menu in code; own the status header. |
| `remex.desktop/Localization/Strings*.resx` (modify, ×9) | New strings; remove `Tray_LiveGlance`, `Tray_SwitchLightMode`, `Tray_SwitchDarkMode`. |
| `remex.desktop.tests/TrayFlyoutGeometryTests.cs` (create) | Validator: clamping, offscreen rejection, multi-monitor. |
| `remex.desktop.tests/TrayFlyoutLayoutStoreTests.cs` (create) | Roundtrip, missing file, corrupt JSON. |
| `remex.desktop.tests/TrayTileColumnsTests.cs` (create) | Breakpoints and degenerate widths. |
| `remex.desktop.tests/TrayTileRulesTests.cs` (create) | Enablement and confirmation policy. |

---

### Task 1: Geometry record and pure validator

The failure this exists to prevent: a window whose saved position is on a monitor that is no longer connected. It restores offscreen, has no visible chrome to drag, and the only recovery is deleting a JSON file the user does not know about.

**Files:**
- Create: `remex.desktop/Services/TrayFlyoutGeometry.cs`
- Test: `remex.desktop.tests/TrayFlyoutGeometryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Remex.Desktop.Services.TrayFlyoutGeometry` (record: `bool IsPinned`, `double X`, `double Y`, `double Width`, `double Height`); `Remex.Desktop.Services.TrayFlyoutGeometryValidator.Validate(TrayFlyoutGeometry?, IReadOnlyList<PixelRect>) -> TrayFlyoutGeometry?`; constants `MinWidth = 320`, `MaxWidth = 900`, `MinHeight = 240`, `MaxHeight = 800`, `MinVisible = 100`.

- [ ] **Step 1: Write the failing tests**

Create `remex.desktop.tests/TrayFlyoutGeometryTests.cs`:

```csharp
using Avalonia;
using Remex.Desktop.Services;

namespace Remex.Desktop.Tests;

public class TrayFlyoutGeometryTests
{
    // A single 1920x1080 monitor at the origin.
    private static readonly IReadOnlyList<PixelRect> SingleScreen =
        [new PixelRect(0, 0, 1920, 1080)];

    // A second monitor to the LEFT of the primary, which is where negative
    // coordinates legitimately come from — the case a naive "x >= 0" check breaks.
    private static readonly IReadOnlyList<PixelRect> DualScreen =
        [new PixelRect(0, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1080)];

    private static TrayFlyoutGeometry Geometry(double x, double y, double w = 460, double h = 380) =>
        new() { IsPinned = true, X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void Null_candidate_returns_null()
    {
        Assert.Null(TrayFlyoutGeometryValidator.Validate(null, SingleScreen));
    }

    [Fact]
    public void Fully_visible_rect_is_returned_unchanged()
    {
        var input = Geometry(400, 300);
        var result = TrayFlyoutGeometryValidator.Validate(input, SingleScreen);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Oversize_is_clamped_to_the_maximum()
    {
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(0, 0, 5000, 4000), SingleScreen);
        Assert.NotNull(result);
        Assert.Equal(TrayFlyoutGeometryValidator.MaxWidth, result!.Width);
        Assert.Equal(TrayFlyoutGeometryValidator.MaxHeight, result.Height);
    }

    [Fact]
    public void Undersize_is_clamped_to_the_minimum()
    {
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(0, 0, 10, 10), SingleScreen);
        Assert.NotNull(result);
        Assert.Equal(TrayFlyoutGeometryValidator.MinWidth, result!.Width);
        Assert.Equal(TrayFlyoutGeometryValidator.MinHeight, result.Height);
    }

    [Fact]
    public void Rect_entirely_off_the_only_screen_is_rejected()
    {
        // The disconnected-monitor case: saved on a screen that no longer exists.
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(-4000, 300), SingleScreen));
    }

    [Fact]
    public void Rect_overlapping_by_less_than_the_minimum_is_rejected()
    {
        // 460 wide at x = 1920 - 50 leaves only 50px on screen; below MinVisible of 100.
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(1870, 300), SingleScreen));
    }

    [Fact]
    public void Rect_overlapping_by_exactly_the_minimum_is_accepted()
    {
        // 100px of the window remains on screen — the boundary, and it must pass.
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(1820, 300), SingleScreen);
        Assert.NotNull(result);
    }

    [Fact]
    public void Rect_on_a_secondary_screen_at_negative_coordinates_is_preserved()
    {
        var input = Geometry(-1500, 200);
        var result = TrayFlyoutGeometryValidator.Validate(input, DualScreen);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Rect_is_rejected_when_no_screens_are_reported()
    {
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(400, 300), []));
    }

    [Fact]
    public void Non_finite_coordinates_are_rejected()
    {
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(double.NaN, 300), SingleScreen));
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(400, double.PositiveInfinity), SingleScreen));
    }

    [Fact]
    public void Clamping_happens_before_the_visibility_check()
    {
        // A 10x10 rect near the right edge is offscreen-ish before clamping and comfortably
        // visible after. Clamp first, then judge — otherwise a tiny saved size is rejected
        // for a reason that no longer applies once it has been grown to the minimum.
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(1700, 300, 10, 10), SingleScreen);
        Assert.NotNull(result);
        Assert.Equal(TrayFlyoutGeometryValidator.MinWidth, result!.Width);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~TrayFlyoutGeometryTests"
```

Expected: FAIL to compile — `TrayFlyoutGeometry` and `TrayFlyoutGeometryValidator` do not exist.

- [ ] **Step 3: Write the implementation**

Create `remex.desktop/Services/TrayFlyoutGeometry.cs`:

```csharp
using Avalonia;

namespace Remex.Desktop.Services;

/// <summary>The tray flyout's persisted window state.</summary>
public sealed record TrayFlyoutGeometry
{
    public bool IsPinned { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>
/// Decides whether a saved <see cref="TrayFlyoutGeometry"/> is still usable on the screens that
/// exist right now, and clamps it to a sane size.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE STORE, AND WITH NO FILE I/O, so the rule that matters most here can be tested
/// against a fabricated monitor layout rather than the tester's actual desktop. The rule that
/// matters most is the visibility check: a window restored onto a monitor that has since been
/// unplugged has no visible chrome to drag and no in-app recovery — the user's only option is to
/// find and delete a JSON file they do not know exists.
/// <para>
/// The check is an INTERSECTION against every screen, not a bounds test against the primary. A
/// second monitor to the left of the primary has negative coordinates, so "x is negative" is not
/// evidence of anything.
/// </para>
/// </remarks>
public static class TrayFlyoutGeometryValidator
{
    public const double MinWidth = 320;
    public const double MaxWidth = 900;
    public const double MinHeight = 240;
    public const double MaxHeight = 800;

    /// <summary>How much of the window must remain on some screen, in logical pixels, each axis.</summary>
    public const double MinVisible = 100;

    public static TrayFlyoutGeometry? Validate(
        TrayFlyoutGeometry? candidate,
        IReadOnlyList<PixelRect> workingAreas)
    {
        if (candidate is null)
            return null;

        if (!IsFinite(candidate.X) || !IsFinite(candidate.Y) ||
            !IsFinite(candidate.Width) || !IsFinite(candidate.Height))
            return null;

        // Clamp BEFORE judging visibility: a saved size below the minimum is grown here, and it
        // would be wrong to reject it for an overlap it only failed at its pre-clamp size.
        var clamped = candidate with
        {
            Width = Math.Clamp(candidate.Width, MinWidth, MaxWidth),
            Height = Math.Clamp(candidate.Height, MinHeight, MaxHeight),
        };

        foreach (var area in workingAreas)
        {
            var overlapX = Math.Min(clamped.X + clamped.Width, area.X + area.Width) - Math.Max(clamped.X, area.X);
            var overlapY = Math.Min(clamped.Y + clamped.Height, area.Y + area.Height) - Math.Max(clamped.Y, area.Y);

            if (overlapX >= MinVisible && overlapY >= MinVisible)
                return clamped;
        }

        return null;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~TrayFlyoutGeometryTests"
```

Expected: PASS, 11 tests. Confirm the total is 11 — a green run with a smaller total means the build did not pick up your new file.

- [ ] **Step 5: Mutation-check the visibility rule**

Temporarily change `overlapX >= MinVisible && overlapY >= MinVisible` to `overlapX >= MinVisible`. Re-run. `Rect_overlapping_by_less_than_the_minimum_is_rejected` must still pass on the X axis, so **also** add this mutation: change `MinVisible` to `0`. At least `Rect_overlapping_by_less_than_the_minimum_is_rejected` must fail. Revert both.

If no test fails for a mutation, the assertion is vacuous — fix the test, not the mutant.

- [ ] **Step 6: Commit**

```powershell
git add remex.desktop/Services/TrayFlyoutGeometry.cs remex.desktop.tests/TrayFlyoutGeometryTests.cs
git commit -m "feat(desktop): tray flyout geometry record and offscreen validator (RemEx-3m4ho)"
```

---

### Task 2: Layout store

**Files:**
- Create: `remex.desktop/Services/TrayFlyoutLayoutStore.cs`
- Test: `remex.desktop.tests/TrayFlyoutLayoutStoreTests.cs`

**Interfaces:**
- Consumes: `TrayFlyoutGeometry` from Task 1.
- Produces: `TrayFlyoutLayoutStore` with `Task<TrayFlyoutGeometry?> LoadRawAsync()` and `Task SaveAsync(TrayFlyoutGeometry geometry)`. An `internal` constructor taking an explicit config path exists **for tests only** — the public parameterless constructor resolves through `RemexDataPaths`.

Note the store deliberately does **not** validate. It returns what was on disk; the window validates against live screens at the moment it uses it. Screens can change between load and use.

- [ ] **Step 1: Write the failing tests**

Create `remex.desktop.tests/TrayFlyoutLayoutStoreTests.cs`:

```csharp
using Remex.Desktop.Services;

namespace Remex.Desktop.Tests;

public class TrayFlyoutLayoutStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TrayFlyoutLayoutStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remex-tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "tray_flyout_layout.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TrayFlyoutLayoutStore Store() => new(_path);

    [Fact]
    public async Task Missing_file_loads_as_null()
    {
        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Saved_geometry_round_trips()
    {
        var saved = new TrayFlyoutGeometry { IsPinned = true, X = 120, Y = 340, Width = 460, Height = 380 };
        await Store().SaveAsync(saved);

        var loaded = await Store().LoadRawAsync();

        Assert.Equal(saved, loaded);
    }

    [Fact]
    public async Task Corrupt_json_loads_as_null_without_throwing()
    {
        await File.WriteAllTextAsync(_path, "{ this is not json");

        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Empty_file_loads_as_null_without_throwing()
    {
        await File.WriteAllTextAsync(_path, string.Empty);

        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Json_null_literal_loads_as_null_without_throwing()
    {
        await File.WriteAllTextAsync(_path, "null");

        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Save_overwrites_a_previous_value()
    {
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = true, X = 1, Y = 2, Width = 400, Height = 300 });
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = false, X = 9, Y = 8, Width = 500, Height = 350 });

        var loaded = await Store().LoadRawAsync();

        Assert.NotNull(loaded);
        Assert.False(loaded!.IsPinned);
        Assert.Equal(9, loaded.X);
    }

    [Fact]
    public async Task Unpinned_state_survives_a_round_trip()
    {
        // IsPinned = false is the DEFAULT for a bool, so a serializer misconfiguration that drops
        // the property would still pass a pinned-only test. This is the one that catches it.
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = false, X = 5, Y = 5, Width = 400, Height = 300 });
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = true, X = 5, Y = 5, Width = 400, Height = 300 });

        var loaded = await Store().LoadRawAsync();

        Assert.True(loaded!.IsPinned);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~TrayFlyoutLayoutStoreTests"
```

Expected: FAIL to compile — `TrayFlyoutLayoutStore` does not exist.

- [ ] **Step 3: Write the implementation**

Create `remex.desktop/Services/TrayFlyoutLayoutStore.cs`:

```csharp
using System.Text.Json;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>
/// Persists the tray flyout's pinned window state as a small JSON file.
/// </summary>
/// <remarks>
/// Follows <c>FileTransferRootSettingsService</c>: the path resolves through
/// <see cref="RemexDataPaths"/> rather than <c>SpecialFolder</c> directly, and writes are staged
/// rather than written over the live file.
/// <para>
/// DELIBERATELY DOES NOT VALIDATE. It returns whatever was on disk and lets the window judge it
/// against the screens that exist at the moment it is used — screens can be connected or
/// disconnected between this load and that use, so a verdict reached here would already be stale.
/// The rule lives in <see cref="TrayFlyoutGeometryValidator"/>.
/// </para>
/// </remarks>
public sealed class TrayFlyoutLayoutStore
{
    private const string FileName = "tray_flyout_layout.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;

    public TrayFlyoutLayoutStore()
    {
        var legacyFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
        RemexDataPaths.TryMigrateWindowsFile(FileName);
        _configPath = Path.Combine(baseFolder, FileName);
        RemexDataPaths.SweepStagingOrphans(_configPath);
    }

    /// <summary>Test seam: point the store at an explicit file.</summary>
    internal TrayFlyoutLayoutStore(string configPath) => _configPath = configPath;

    /// <summary>Reads the saved state, or <c>null</c> if there is none or it is unreadable.</summary>
    public async Task<TrayFlyoutGeometry?> LoadRawAsync()
    {
        if (!File.Exists(_configPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<TrayFlyoutGeometry>(json, JsonOptions);
        }
        catch
        {
            // A corrupt layout file must never be worse than no layout file. The caller falls back
            // to tray placement, which is always valid.
            return null;
        }
    }

    public async Task SaveAsync(TrayFlyoutGeometry geometry)
    {
        var json = JsonSerializer.Serialize(geometry, JsonOptions);
        await RemexDataPaths.WriteAllTextAtomicAsync(_configPath, json);
    }
}
```

If `remex.desktop.tests` cannot see the `internal` constructor, add `remex.desktop.tests` to `InternalsVisibleTo` in `remex.desktop`'s csproj or `AssemblyInfo` — check whether that attribute already exists there before adding a second one.

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~TrayFlyoutLayoutStoreTests"
```

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```powershell
git add remex.desktop/Services/TrayFlyoutLayoutStore.cs remex.desktop.tests/TrayFlyoutLayoutStoreTests.cs
git commit -m "feat(desktop): persist tray flyout geometry through RemexDataPaths (RemEx-3m4ho)"
```

---

### Task 3: Column breakpoints and tile rules

**Files:**
- Create: `remex.desktop/Converters/TrayTileColumnsConverter.cs`
- Create: `remex.desktop/ViewModels/TrayTileRules.cs`
- Test: `remex.desktop.tests/TrayTileColumnsTests.cs`
- Test: `remex.desktop.tests/TrayTileRulesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `TrayTileColumnsConverter.Instance` (an `IValueConverter`) and `TrayTileColumnsConverter.ColumnsFor(double) -> int`; `TrayPowerAction` enum (`Restart`, `Shutdown`, `SignOut`, `Hibernate`); `TrayTileRules.IsRemoteDesktopEnabled(bool isPhoneAttached) -> bool`; `TrayTileRules.RequiresConfirmation(TrayPowerAction) -> bool`; `TrayPowerInvoker.InvokeAsync(TrayPowerAction, Func<TrayPowerAction, Task<bool>>?, Func<TrayPowerAction, Task>) -> Task<bool>`.

- [ ] **Step 1: Write the failing tests**

Create `remex.desktop.tests/TrayTileColumnsTests.cs`:

```csharp
using Remex.Desktop.Converters;

namespace Remex.Desktop.Tests;

public class TrayTileColumnsTests
{
    [Theory]
    [InlineData(320, 2)]
    [InlineData(379, 2)]   // just below the first breakpoint
    [InlineData(380, 3)]   // exactly on it
    [InlineData(519, 3)]   // just below the second
    [InlineData(520, 4)]   // exactly on it
    [InlineData(1200, 4)]  // no fifth column, however wide
    public void Column_count_follows_the_breakpoints(double width, int expected)
    {
        Assert.Equal(expected, TrayTileColumnsConverter.ColumnsFor(width));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-50)]
    public void Degenerate_widths_fall_back_to_two_columns(double width)
    {
        // A UniformGrid with Columns = 0 lays every tile in a single row off the edge of the
        // window, so the fallback must never be zero. NaN is reachable: Bounds.Width is NaN
        // before the first layout pass.
        Assert.Equal(2, TrayTileColumnsConverter.ColumnsFor(width));
    }

    [Fact]
    public void Converter_delegates_to_ColumnsFor()
    {
        var result = TrayTileColumnsConverter.Instance.Convert(
            520d, typeof(int), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(4, result);
    }

    [Fact]
    public void Converter_given_a_non_double_falls_back_to_two_columns()
    {
        var result = TrayTileColumnsConverter.Instance.Convert(
            "not a width", typeof(int), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(2, result);
    }
}
```

Create `remex.desktop.tests/TrayTileRulesTests.cs`:

```csharp
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Tests;

public class TrayTileRulesTests
{
    [Fact]
    public void Remote_desktop_is_enabled_only_when_a_phone_is_attached()
    {
        Assert.True(TrayTileRules.IsRemoteDesktopEnabled(isPhoneAttached: true));
        Assert.False(TrayTileRules.IsRemoteDesktopEnabled(isPhoneAttached: false));
    }

    [Theory]
    [InlineData(TrayPowerAction.Restart)]
    [InlineData(TrayPowerAction.Shutdown)]
    [InlineData(TrayPowerAction.SignOut)]
    public void Session_ending_actions_require_confirmation(TrayPowerAction action)
    {
        Assert.True(TrayTileRules.RequiresConfirmation(action));
    }

    [Fact]
    public void Hibernate_does_not_require_confirmation()
    {
        // Recoverable: the session comes back exactly as it was. Confirming it would train the
        // user to dismiss the dialog that guards Shutdown.
        Assert.False(TrayTileRules.RequiresConfirmation(TrayPowerAction.Hibernate));
    }

    [Fact]
    public void Every_power_action_has_an_explicit_confirmation_verdict()
    {
        // Guards the default arm: a new enum member must be classified deliberately, not inherit
        // "no confirmation needed" by falling through.
        foreach (TrayPowerAction action in Enum.GetValues<TrayPowerAction>())
        {
            var exception = Record.Exception(() => TrayTileRules.RequiresConfirmation(action));
            Assert.Null(exception);
        }
    }

    // ---- TrayPowerInvoker: the routing itself, not just the policy ----------------------------
    //
    // These matter more than the policy tests above. "Shutdown requires confirmation" being true
    // is worth nothing if the code path that runs Shutdown never consults it. That is the bug
    // these four catch, and it is silent — the PC just turns off.

    [Fact]
    public async Task Confirmed_destructive_action_executes()
    {
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Shutdown,
            confirm: _ => Task.FromResult(true),
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.True(result);
        Assert.True(executed);
    }

    [Fact]
    public async Task Declined_destructive_action_does_not_execute()
    {
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Shutdown,
            confirm: _ => Task.FromResult(false),
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.False(result);
        Assert.False(executed);
    }

    [Fact]
    public async Task Destructive_action_with_no_confirm_delegate_does_not_execute()
    {
        // An unwired view model must DECLINE, not proceed unconfirmed. Same contract as every
        // other destructive command in this app (RemEx-07jx).
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Shutdown,
            confirm: null,
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.False(result);
        Assert.False(executed);
    }

    [Fact]
    public async Task Non_destructive_action_executes_without_asking()
    {
        var asked = false;
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Hibernate,
            confirm: _ => { asked = true; return Task.FromResult(true); },
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.True(result);
        Assert.True(executed);
        Assert.False(asked);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~TrayTile"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the implementations**

Create `remex.desktop/Converters/TrayTileColumnsConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Turns the tray flyout's available width into the number of columns its action grid should use.
/// </summary>
/// <remarks>
/// This is what makes the flyout worth resizing. Without it a wider window only stretches the same
/// tiles; with it, widening the window reveals more of the grid per row.
/// <para>
/// NEVER RETURNS ZERO. <c>UniformGrid.Columns = 0</c> puts every tile in one row running off the
/// edge of the window, and <c>Bounds.Width</c> is <c>NaN</c> until the first layout pass — so the
/// degenerate case is reached on every single show, not just in theory.
/// </para>
/// </remarks>
public sealed class TrayTileColumnsConverter : IValueConverter
{
    public static readonly TrayTileColumnsConverter Instance = new();

    private const double ThreeColumnWidth = 380;
    private const double FourColumnWidth = 520;
    private const int FallbackColumns = 2;

    public static int ColumnsFor(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return FallbackColumns;

        if (width < ThreeColumnWidth) return 2;
        if (width < FourColumnWidth) return 3;
        return 4;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width ? ColumnsFor(width) : FallbackColumns;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Create `remex.desktop/ViewModels/TrayTileRules.cs`:

```csharp
namespace Remex.Desktop.ViewModels;

/// <summary>The power actions the tray flyout's Power submenu offers.</summary>
/// <remarks>
/// The Force variants (<c>ForceShutdown</c>, <c>ForceRestart</c>, <c>RestartToUefi</c>) are
/// deliberately absent. They exist on <c>ConnectionViewModel</c> and stay reachable from the main
/// window; a tray popup you open by mis-clicking an icon is the wrong place to offer an action
/// that discards unsaved work without asking the OS first.
/// </remarks>
public enum TrayPowerAction
{
    Restart,
    Shutdown,
    SignOut,
    Hibernate,
}

/// <summary>
/// The tray flyout's enablement and confirmation policy, kept free of Avalonia so it can be tested
/// without a running application.
/// </summary>
public static class TrayTileRules
{
    /// <summary>
    /// Remote desktop needs a phone on the other end. Note this is PHONE presence, not the
    /// desktop's own loopback link — see <c>PhonePresence.IsPhone</c> for why those differ.
    /// </summary>
    public static bool IsRemoteDesktopEnabled(bool isPhoneAttached) => isPhoneAttached;

    /// <summary>
    /// Whether an action ends the session and so must be confirmed first.
    /// </summary>
    /// <remarks>
    /// Hibernate is excluded on purpose: it restores the session exactly as it was, so confirming
    /// it would only teach the habit of dismissing the dialog that guards Shutdown.
    /// </remarks>
    public static bool RequiresConfirmation(TrayPowerAction action) => action switch
    {
        TrayPowerAction.Restart => true,
        TrayPowerAction.Shutdown => true,
        TrayPowerAction.SignOut => true,
        TrayPowerAction.Hibernate => false,
    };
}
```

The `switch` expression has **no default arm** on purpose — adding a member to `TrayPowerAction` then produces a compiler warning rather than silently classifying it as "no confirmation needed".

Add `TrayPowerInvoker` to the same file:

```csharp
/// <summary>
/// Runs a power action, asking for confirmation first when the policy demands it.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE VIEW MODEL SO IT CAN BE TESTED. <c>TrayFlyoutViewModel</c> needs
/// <c>ShellViewModel</c> and <c>HomeViewModel</c> to construct, neither of which stands up in a
/// unit test without the whole container — which would have left the single most safety-critical
/// path in this feature covered by nothing but a manual click. The policy being correct is not
/// the same property as the policy being consulted, and it is the second one that turns a PC off
/// without asking.
/// </remarks>
public static class TrayPowerInvoker
{
    /// <param name="confirm">
    /// Asks the user. <c>null</c> means there is no way to ask, which must be read as "do not
    /// proceed" — never as "no confirmation needed".
    /// </param>
    /// <returns><c>true</c> if the action ran.</returns>
    public static async Task<bool> InvokeAsync(
        TrayPowerAction action,
        Func<TrayPowerAction, Task<bool>>? confirm,
        Func<TrayPowerAction, Task> execute)
    {
        if (TrayTileRules.RequiresConfirmation(action))
        {
            if (confirm is null)
                return false;

            if (!await confirm(action))
                return false;
        }

        await execute(action);
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~TrayTile"
```

Expected: PASS, 15 tests (11 columns + 4 rules... count them; the theory rows count individually).

- [ ] **Step 5: Mutation-check the breakpoints**

Change `width < ThreeColumnWidth` to `width <= ThreeColumnWidth`. Re-run. `Column_count_follows_the_breakpoints(380, 3)` must fail. Revert. If it passes, the boundary rows are not doing their job.

- [ ] **Step 6: Commit**

```powershell
git add remex.desktop/Converters/TrayTileColumnsConverter.cs remex.desktop/ViewModels/TrayTileRules.cs remex.desktop.tests/TrayTileColumnsTests.cs remex.desktop.tests/TrayTileRulesTests.cs
git commit -m "feat(desktop): tray grid breakpoints and tile policy (RemEx-el6gh, RemEx-7n5gr)"
```

---

### Task 4: Localization strings

Done before the view model so the VM can reference real keys rather than placeholders.

**Files:**
- Modify: `remex.desktop/Localization/Strings.resx` and the eight locale variants (`.es`, `.fr`, `.hi`, `.id`, `.pl`, `.pt-BR`, `.tr`, `.uk`)

**Interfaces:**
- Produces: the resource keys below, resolved at runtime through `LocalizationService.Instance["Key"]` and in XAML through `{local:Localize Key}`.

- [ ] **Step 1: Add the new keys to `Strings.resx`**

| Key | English value |
|---|---|
| `Tray_Tile_Lock` | `Lock` |
| `Tray_Tile_Sleep` | `Sleep` |
| `Tray_Tile_RemoteDesktop` | `Remote` |
| `Tray_Tile_SendFile` | `Send` |
| `Tray_Tile_Pair` | `Pair` |
| `Tray_Tile_Power` | `Power` |
| `Tray_Power_Restart` | `Restart` |
| `Tray_Power_Shutdown` | `Shut down` |
| `Tray_Power_SignOut` | `Sign out` |
| `Tray_Power_Hibernate` | `Hibernate` |
| `Tray_Disabled_NeedsPhone` | `Connect a phone to use this` |
| `Tray_Pin` | `Pin this window` |
| `Tray_Unpin` | `Unpin this window` |
| `Tray_Menu_LockPc` | `Lock PC` |
| `Tray_Menu_RemoteDesktop` | `Remote desktop` |
| `Tray_Menu_OpenTransfers` | `Open transfers` |
| `Tray_Menu_PairDevice` | `Pair a device` |
| `Tray_Menu_Settings` | `Settings` |
| `A11y_TrayFlyout_Header` | `RemEx status` |
| `A11y_TrayFlyout_Pin` | `Pin or unpin the RemEx window` |

Reuse, do **not** duplicate: `Tray_ShowMainWindow`, `Tray_Exit`, `A11y_Close`, and the presence strings already backing `PhonePresenceMonitor`. If any English value above is byte-identical to an existing string's value, use the existing key instead and drop the new one — a second key with the same text is a translation asked for twice and a chance for the two to drift.

- [ ] **Step 2: Remove the retired keys from `Strings.resx`**

Delete `Tray_LiveGlance`, `Tray_SwitchLightMode`, `Tray_SwitchDarkMode`. Theme switching moves to Settings, where it already exists.

- [ ] **Step 3: Mirror both changes across the eight locale files**

Same keys, translated values, same removals. Match the tone of the neighbouring entries in each file — these are terse UI labels, not sentences.

- [ ] **Step 4: Run the localization gate**

```powershell
pwsh -File scripts/check-localization.ps1
```

Expected: PASS on all three axes (parity, staleness, references). A missing key in one locale fails here, which is the point of doing this task before the code that references the keys.

- [ ] **Step 5: Commit**

```powershell
git add remex.desktop/Localization/
git commit -m "i18n(desktop): tray command center strings across 9 locales (RemEx-sr5uq)"
```

---

### Task 5: `TrayFlyoutViewModel`

**Files:**
- Create: `remex.desktop/ViewModels/TrayFlyoutViewModel.cs`

**Interfaces:**
- Consumes: `TrayTileRules`, `TrayPowerAction` (Task 3); the resource keys from Task 4; `PhonePresenceMonitor.Instance`; `ShellViewModel` (`Connection` at `:67`, `NavigateToRemoteDesktop()` at `:560`, `NavigateToFileTransfer()` at `:581`, `NavigateToHome()` at `:515`, `NavigateToSettings()` at `:664`); `HomeViewModel.PinnedSensors`; `ConnectionViewModel.LockCommand`, `SleepCommand`, `RestartCommand`, `ShutdownCommand`, `SignOutCommand`, `HibernateCommand` (generated from the `[RelayCommand]` methods at `ConnectionViewModel.cs:686-714`).
- Produces: `TrayFlyoutViewModel` with `PhonePresenceMonitor Presence`, `ConnectionViewModel Connection`, `ObservableCollection<SensorViewModel> PinnedSensors`, `IReadOnlyList<TrayTile> Tiles`, `bool IsPinned`, `Func<string, string, string, Task<bool>>? OnConfirmationRequested`, `void Refresh()`, and `InvokePowerCommand` (parameterised by `TrayPowerAction`). Plus the `TrayTile` record: `string Label`, `Geometry? Icon`, `ICommand Command`, `bool IsEnabled`, `string? DisabledTooltip`, `bool HasSubmenu`.

`Connection` is exposed because the status strip binds `Connection.StatusText`. `Icon` is a resolved `Geometry`, **not** a resource-key string — resolving it once here removes a converter and a per-render resource lookup from the item template.

- [ ] **Step 1: Write the view model**

Create `remex.desktop/ViewModels/TrayFlyoutViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>One button in the tray flyout's action grid.</summary>
public sealed record TrayTile
{
    public required string Label { get; init; }

    /// <summary>Resolved once at build time, not looked up per render.</summary>
    public Geometry? Icon { get; init; }

    public required ICommand Command { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? DisabledTooltip { get; init; }
    public bool HasSubmenu { get; init; }
}

/// <summary>
/// The tray flyout's own view model.
/// </summary>
/// <remarks>
/// COMPOSES, DOES NOT REIMPLEMENT. Presence comes from the one polling singleton every other
/// indicator reads; power commands are <c>ConnectionViewModel</c>'s existing
/// <c>[RelayCommand]</c>s; the sensors are <c>HomeViewModel</c>'s list, not a second copy.
/// <para>
/// IT IS A SEPARATE CLASS FROM <see cref="HomeViewModel"/> for a reason. The flyout previously used
/// <c>HomeViewModel</c> as its data context, which is why it had telemetry and no actions — that
/// view model has no commands to offer. Adding power commands there would couple the Home page to
/// <c>ConnectionViewModel</c> for a different surface's benefit, and would make the tile set
/// impossible to test without standing up the whole dashboard.
/// </para>
/// <para>
/// EVERY ACTION HERE TARGETS THIS PC. RemEx has no PC-to-phone command channel — there is no
/// message type in <c>remex.core</c> that could carry one (see RemEx-uov9y). Do not add a tile
/// whose label implies the phone is being controlled.
/// </para>
/// </remarks>
public sealed partial class TrayFlyoutViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly HomeViewModel _home;

    /// <summary>Supplied by the view; see <c>ConfirmationDialogHost</c>.</summary>
    /// <remarks>
    /// Null means "cannot confirm", and every caller must read that as "do not proceed" — the same
    /// contract every other destructive command in this app follows (RemEx-07jx).
    /// </remarks>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    public PhonePresenceMonitor Presence => PhonePresenceMonitor.Instance;

    /// <summary>Exposed because the status strip binds <c>Connection.StatusText</c>.</summary>
    public ConnectionViewModel Connection => _shell.Connection;

    public ObservableCollection<SensorViewModel> PinnedSensors => _home.PinnedSensors;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private IReadOnlyList<TrayTile> _tiles = [];

    public TrayFlyoutViewModel(ShellViewModel shell, HomeViewModel home)
    {
        _shell = shell;
        _home = home;

        // Rebuild when phone presence changes, so the Remote tile enables and disables in place
        // rather than at the next time the flyout happens to be reopened.
        Presence.PropertyChanged += OnPresenceChanged;

        RebuildTiles();
    }

    /// <summary>Refreshes everything the flyout shows. Called each time it is about to be shown.</summary>
    public void Refresh()
    {
        _home.RefreshPinnedSensors();
        Presence.Refresh();
        RebuildTiles();
    }

    private void OnPresenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhonePresenceMonitor.IsPhoneAttached))
            RebuildTiles();
    }

    private void RebuildTiles()
    {
        var remoteEnabled = TrayTileRules.IsRemoteDesktopEnabled(Presence.IsPhoneAttached);

        Tiles =
        [
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Lock"],
                Icon = Icon("IconLock"),
                Command = _shell.Connection.LockCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Sleep"],
                Icon = Icon("IconMoon"),
                Command = _shell.Connection.SleepCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_RemoteDesktop"],
                Icon = Icon("IconMonitor"),
                Command = OpenRemoteDesktopCommand,
                IsEnabled = remoteEnabled,
                DisabledTooltip = remoteEnabled
                    ? null
                    : LocalizationService.Instance["Tray_Disabled_NeedsPhone"],
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_SendFile"],
                Icon = Icon("IconUpload"),
                Command = OpenTransfersCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Pair"],
                Icon = Icon("IconLink"),
                // Always enabled: RemEx supports several paired devices, so gating this on
                // "already paired" would block adding a second phone.
                Command = OpenPairingCommand,
            },
            new TrayTile
            {
                Label = LocalizationService.Instance["Tray_Tile_Power"],
                Icon = Icon("IconPower"),
                Command = NoOpCommand,
                HasSubmenu = true,
            },
        ];
    }

    /// <summary>
    /// Resolves an icon geometry from the application's resources, tolerating a missing key.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing. A typo'd or removed icon key should cost a tile its
    /// glyph, not take down the tray flyout — the label alone still says what the tile does.
    /// </remarks>
    private static Geometry? Icon(string key)
        => Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    [RelayCommand]
    private void OpenRemoteDesktop() => _shell.NavigateToRemoteDesktop();

    [RelayCommand]
    private void OpenTransfers() => _shell.NavigateToFileTransfer();

    [RelayCommand]
    private void OpenPairing() => _shell.NavigateToSettings();

    /// <summary>The Power tile opens a submenu from the view; the tile's own command does nothing.</summary>
    [RelayCommand]
    private void NoOp() { }

    [RelayCommand]
    private async Task InvokePowerAsync(TrayPowerAction action)
    {
        // The confirm-then-execute decision lives in TrayPowerInvoker so it can be unit tested;
        // this method only supplies the two delegates.
        await TrayPowerInvoker.InvokeAsync(
            action,
            confirm: OnConfirmationRequested is null
                ? null
                : a => OnConfirmationRequested(
                    LocalizationService.Instance[LabelKey(a)],
                    LocalizationService.Instance[ConfirmMessageKey(a)],
                    LocalizationService.Instance[LabelKey(a)]),
            execute: a => a switch
            {
                TrayPowerAction.Restart => _shell.Connection.RestartAsync(),
                TrayPowerAction.Shutdown => _shell.Connection.ShutdownAsync(),
                TrayPowerAction.SignOut => _shell.Connection.SignOutAsync(),
                TrayPowerAction.Hibernate => _shell.Connection.HibernateAsync(),
                _ => Task.CompletedTask,
            });
    }

    private static string LabelKey(TrayPowerAction action) => action switch
    {
        TrayPowerAction.Restart => "Tray_Power_Restart",
        TrayPowerAction.Shutdown => "Tray_Power_Shutdown",
        TrayPowerAction.SignOut => "Tray_Power_SignOut",
        TrayPowerAction.Hibernate => "Tray_Power_Hibernate",
    };

    private static string ConfirmMessageKey(TrayPowerAction action) => action switch
    {
        TrayPowerAction.Restart => "Confirm_Restart_Message",
        TrayPowerAction.Shutdown => "Confirm_Shutdown_Message",
        TrayPowerAction.SignOut => "Confirm_SignOut_Message",
        TrayPowerAction.Hibernate => "Confirm_Hibernate_Message",
    };
}
```

**Before writing this file**, grep for the confirmation-message keys that already exist:

```powershell
Select-String -Path remex.desktop/Localization/Strings.resx -Pattern 'Confirm_(Restart|Shutdown|SignOut)' -Context 0,1
```

If `ConnectionViewModel` or `RemoteViewModel` already confirms these actions with existing keys, use **those** key names in `ConfirmMessageKey` and delete the corresponding rows you would otherwise add in Task 4. If they do not exist, add `Confirm_Restart_Message`, `Confirm_Shutdown_Message`, `Confirm_SignOut_Message` to all nine locale files as part of this task's commit. Hibernate never reaches `ConfirmMessageKey` (it returns `false` from `RequiresConfirmation`), but the arm is present so the `switch` stays exhaustive.

The file needs `using Avalonia;` and `using Avalonia.Media;` for `Application` and `Geometry`.

Likewise, verify the six icon keys against the icon resources actually defined — `TrayFlyoutWindow.axaml:92` currently uses `{StaticResource IconFlash}`, so the naming convention is `Icon<Name>`:

```powershell
Select-String -Path remex.desktop -Include *.axaml -Recurse -Pattern 'x:Key="Icon\w+"' | ForEach-Object { $_.Line -replace '.*x:Key="(Icon\w+)".*','$1' } | Sort-Object -Unique
```

Use the nearest existing icon for each tile rather than adding six new geometries. If a tile has no reasonable existing icon, add one `StreamGeometry` to the same dictionary the others live in.

- [ ] **Step 2: Build to verify it compiles**

```powershell
dotnet build remex.desktop -c Release
```

Expected: succeeds. **Check the exit code** — `dotnet build` takes one project per invocation, and a malformed multi-project invocation reports an `MSB1008` error while a following `--no-build` test run happily executes a stale assembly.

- [ ] **Step 3: Register the view model**

In `remex.desktop/App.axaml.cs`, wherever `HomeViewModel` is registered in the service collection, register `TrayFlyoutViewModel` the same way (find it with `Select-String -Path remex.desktop/App.axaml.cs -Pattern 'HomeViewModel'`). It is a singleton — the flyout is created once and reused.

- [ ] **Step 4: Commit**

```powershell
git add remex.desktop/ViewModels/TrayFlyoutViewModel.cs remex.desktop/App.axaml.cs
git commit -m "feat(desktop): TrayFlyoutViewModel composing presence, power and navigation (RemEx-7n5gr)"
```

---

### Task 6: Window chrome — kill the grey rectangle

Do this before the layout rewrite so the chrome fix can be verified on the existing content, in isolation. If the two land together and the rectangle survives, you will not know which change is responsible.

**Files:**
- Create: `remex.desktop/Views/TrayWindowCorners.cs`
- Modify: `remex.desktop/Views/TrayFlyoutWindow.axaml:6` (the `Window` attributes)
- Modify: `remex.desktop/Views/TrayFlyoutWindow.axaml.cs:9-22` (the constructor)

**Interfaces:**
- Produces: `TrayWindowCorners.ApplyRounded(Window window)` — a no-op off Windows and on Windows 10.

- [ ] **Step 1: Write the corner helper**

Create `remex.desktop/Views/TrayWindowCorners.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Remex.Desktop.Views;

/// <summary>
/// Asks DWM to round an undecorated window's corners on Windows 11.
/// </summary>
/// <remarks>
/// Belt-and-braces for the OS-drawn edge. The visible rounding comes from the inner
/// <c>Border.CornerRadius</c>; this stops Windows compositing a square edge behind it.
/// <para>
/// Fails silently and deliberately. The attribute does not exist before Windows 11 build 22000, so
/// <c>DwmSetWindowAttribute</c> returns a non-zero HRESULT there — which is the expected outcome on
/// Windows 10, not an error worth logging on every window creation.
/// </para>
/// </remarks>
internal static class TrayWindowCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [SupportedOSPlatform("windows")]
    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    internal static void ApplyRounded(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == nint.Zero)
            return;

        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
```

- [ ] **Step 2: Fix the window attributes in XAML**

In `remex.desktop/Views/TrayFlyoutWindow.axaml`, on the `Window` element at line 6, **remove** these three attributes entirely — the code-behind is the single place that sets them, and today the two disagree:

```
Background="Transparent"
TransparencyLevelHint="AcrylicBlur, Mica, None"
SystemDecorations="None"
```

Leave the rest of line 6 as it is for now (`Width`, `SizeToContent`, `MaxHeight`, `Title`, `CanResize`, `Focusable`, `ShowInTaskbar`) — Task 7 rewrites them.

- [ ] **Step 3: Fix the constructor**

Replace the body of the `TrayFlyoutWindow` constructor in `remex.desktop/Views/TrayFlyoutWindow.axaml.cs`:

```csharp
    public TrayFlyoutWindow()
    {
        InitializeComponent();

        // TRANSPARENT ONLY — NOT MICA, NOT BLUR (RemEx-zu09j). DWM composites a Mica or acrylic
        // backdrop across the WHOLE window rect, including the margin this window leaves around its
        // rounded card for the drop shadow. The result was an opaque square sitting behind rounded
        // corners: the sharp grey rectangle. The frosted look now comes from GlassBaseDarkBrush on
        // the inner Border, which clips to its own CornerRadius and is a per-theme token, so all
        // four themes keep their own surface colour.
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;

        Opened += (_, _) => TrayWindowCorners.ApplyRounded(this);
    }
```

`ApplyRounded` is called from `Opened` rather than the constructor because the platform handle does not exist until the window is opened.

- [ ] **Step 4: Point the outer Border at the per-theme surface token**

The outer `Border` at `TrayFlyoutWindow.axaml:38` already uses `{DynamicResource GlassBaseDarkBrush}`. Verify it is unchanged and that `Margin="12"` is retained — the margin is what gives `BoxShadow` room to render, and it is only a problem when a backdrop paints behind it, which Step 3 has now stopped.

- [ ] **Step 5: Build and deploy to the installed location**

```powershell
dotnet build remex.desktop -c Release
pwsh -File scripts/update-local-install.ps1
```

Then run `C:\Program Files\RemEx\Remex.Agent.exe`. Do **not** verify with `dotnet <dll>` — that trips a .NET Host firewall prompt and the host refuses inbound connections, so presence will be wrong.

- [ ] **Step 6: Verify visually at four display scalings**

Open the tray flyout at 100%, 125%, 150% and 200% display scaling (Settings → System → Display → Scale; sign-out is not required for the running app to re-render, but re-open the flyout after each change).

Expected at every scaling: rounded corners with the desktop visible behind them, and **no** grey or opaque square outside the rounded card.

Then switch through all four themes (Settings → Customization) and confirm the same, paying particular attention to **SolarFlare**, the light theme — the other three are dark and will hide a contrast mistake.

- [ ] **Step 7: Commit**

```powershell
git add remex.desktop/Views/TrayWindowCorners.cs remex.desktop/Views/TrayFlyoutWindow.axaml remex.desktop/Views/TrayFlyoutWindow.axaml.cs
git commit -m "fix(desktop): stop Mica painting a square behind the tray flyout's rounded card (RemEx-zu09j)"
```

---

### Task 7: Transient and pinned modes

**Files:**
- Modify: `remex.desktop/Views/TrayFlyoutWindow.axaml.cs` (whole file)
- Modify: `remex.desktop/App.axaml.cs:403-431` (`ToggleLiveGlance`)

**Interfaces:**
- Consumes: `TrayFlyoutLayoutStore`, `TrayFlyoutGeometry`, `TrayFlyoutGeometryValidator` (Tasks 1–2); `TrayFlyoutViewModel` (Task 5); the existing `TrayPlacement.BottomRight(...)`.
- Produces: `TrayFlyoutWindow.ShowAtTray()` (unchanged name), `TrayFlyoutWindow.SuppressNextDeactivate()` — called by `App` before a native menu opens.

- [ ] **Step 1: Rewrite the code-behind**

Replace `remex.desktop/Views/TrayFlyoutWindow.axaml.cs` in full:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class TrayFlyoutWindow : Window
{
    /// <summary>
    /// How long after being shown the window ignores deactivation.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT A COSMETIC DELAY. On Windows, opening the tray icon's own context menu
    /// deactivates this window — so a naive hide-on-deactivate makes the flyout vanish the instant
    /// you right-click the icon that owns it. The previous code-behind carried a comment refusing
    /// to implement deactivate-hide for exactly this reason; the grace window plus
    /// <see cref="SuppressNextDeactivate"/> is what makes it safe to implement.
    /// </remarks>
    private static readonly TimeSpan DeactivateGrace = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private readonly TrayFlyoutLayoutStore _layoutStore = new();
    private readonly DispatcherTimer _saveTimer;

    private DateTime _shownAtUtc = DateTime.MinValue;
    private bool _suppressDeactivate;

    public TrayFlyoutWindow()
    {
        InitializeComponent();

        // TRANSPARENT ONLY — NOT MICA, NOT BLUR (RemEx-zu09j). See the note in Task 6's commit.
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;

        _saveTimer = new DispatcherTimer { Interval = SaveDebounce };
        _saveTimer.Tick += OnSaveTimerTick;

        Opened += (_, _) => TrayWindowCorners.ApplyRounded(this);
        Deactivated += OnDeactivated;
        PositionChanged += (_, _) => ScheduleSave();

        ApplyMode(isPinned: false);
    }

    private TrayFlyoutViewModel? ViewModel => DataContext as TrayFlyoutViewModel;

    /// <summary>Tells the window to ignore the next deactivation, because we caused it.</summary>
    public void SuppressNextDeactivate() => _suppressDeactivate = true;

    public async void ShowAtTray()
    {
        var saved = await _layoutStore.LoadRawAsync();
        var screens = Screens.All.Select(screen => screen.WorkingArea).ToList();
        var valid = TrayFlyoutGeometryValidator.Validate(saved, screens);

        if (valid is { IsPinned: true })
        {
            ApplyMode(isPinned: true);
            Width = valid.Width;
            Height = valid.Height;
            Position = new PixelPoint((int)valid.X, (int)valid.Y);
        }
        else
        {
            // Either nothing saved, or the saved rect is on a screen that no longer exists. The
            // tray corner is always valid, so it is the fallback in both cases.
            ApplyMode(isPinned: false);

            if (Screens.Primary is { } primary)
            {
                Position = TrayPlacement.BottomRight(
                    primary.WorkingArea, Width, Height, primary.Scaling, marginLogical: 12);
            }
        }

        _shownAtUtc = DateTime.UtcNow;
        ViewModel?.Refresh();
        Show();
    }

    /// <summary>Switches between the transient popup and the pinned, movable window.</summary>
    private void ApplyMode(bool isPinned)
    {
        // Focusable is the hinge. False gives a popup that never steals focus from what you are
        // doing; true is required for BeginMoveDrag and for resize grips to respond.
        Focusable = isPinned;
        CanResize = isPinned;
        SizeToContent = isPinned ? SizeToContent.Manual : SizeToContent.Height;

        if (ViewModel is { } vm)
            vm.IsPinned = isPinned;
    }

    private void OnTogglePin(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var pinning = ViewModel?.IsPinned != true;
        ApplyMode(pinning);
        ScheduleSave();
    }

    /// <summary>Drags the whole window by its header, since it has no system title bar.</summary>
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.IsPinned != true)
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (ViewModel?.IsPinned == true)
            return;

        if (_suppressDeactivate)
        {
            _suppressDeactivate = false;
            return;
        }

        if (DateTime.UtcNow - _shownAtUtc < DeactivateGrace)
            return;

        Hide();
    }

    /// <summary>
    /// Coalesces the writes a drag or resize would otherwise produce.
    /// </summary>
    /// <remarks>
    /// PositionChanged fires per frame while dragging. Without this, one drag across a monitor is
    /// hundreds of atomic file writes, each of which stages and renames.
    /// </remarks>
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();

        await _layoutStore.SaveAsync(new TrayFlyoutGeometry
        {
            IsPinned = ViewModel?.IsPinned ?? false,
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
        });
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (ViewModel?.IsPinned == true)
            ScheduleSave();
    }

    private void OnOpenMainApp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
        App.BringMainWindowToFront();
    }

    private void OnCloseFlyout(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Hide();
}
```

`async void` appears twice here. Both are event handlers, which is the one place it is correct — but each must not be able to throw: `LoadRawAsync` and `SaveAsync` both swallow their own I/O failures (Task 2), so neither can escape to the dispatcher.

- [ ] **Step 2: Update `App.ToggleLiveGlance` to use the new view model**

In `remex.desktop/App.axaml.cs`, replace `ToggleLiveGlance` (currently at `:410-431`) so it resolves `TrayFlyoutViewModel` instead of `HomeViewModel`, and wires the confirmation delegate:

```csharp
    private void ToggleLiveGlance()
    {
        if (_flyout == null)
        {
            var vm = Services.GetRequiredService<TrayFlyoutViewModel>();
            _flyout = new TrayFlyoutWindow { DataContext = vm };
            // The flyout is a visible Window, so it can host a modal dialog. Wired here rather than
            // in the view model because a view model owns no UI (RemEx-07jx).
            vm.OnConfirmationRequested = ConfirmationDialogHost.For(_flyout);
        }

        if (_flyout.IsVisible)
            _flyout.Hide();
        else
            _flyout.ShowAtTray();
    }
```

Note `ConfirmationDialogHost.For` returns `false` when the owner has no **visible** window (`ConfirmationDialogHost.cs:41`). The flyout is visible whenever a power tile can be clicked, so this holds — but it means a destructive action declines rather than proceeding if that ever stops being true, which is the safe direction.

- [ ] **Step 3: Suppress deactivation while the tray menu is open**

In `remex.desktop/App.axaml.cs`, in the handler that opens the tray context menu, call `_flyout?.SuppressNextDeactivate()` before the menu appears. Avalonia's `TrayIcon` does not expose a menu-opening event on all platforms, so if there is no such hook, call it from `OnTrayIconClicked` instead — a left-click toggle and a right-click menu both route through the tray icon, and suppressing one extra deactivation is harmless.

- [ ] **Step 4: Build and deploy**

```powershell
dotnet build remex.desktop -c Release
pwsh -File scripts/update-local-install.ps1
```

- [ ] **Step 5: Verify the five behaviours on the installed exe**

Run `C:\Program Files\RemEx\Remex.Agent.exe` and check each:

1. Click the tray icon → flyout appears at the bottom-right corner. Click elsewhere on the desktop → it hides.
2. **Right-click the tray icon while the flyout is open → the flyout does not vanish.** This is the regression the previous code-behind refused to risk; if it fails, the suppression hook in Step 3 is on the wrong event.
3. Click pin → drag the header to a second monitor → resize from a corner → close and restart the app → the window returns to that size and position.
4. Disconnect that second monitor → restart the app → the window appears at the tray corner rather than vanishing.
5. Unpin → the window reverts to transient and hides on click-away again.

- [ ] **Step 6: Commit**

```powershell
git add remex.desktop/Views/TrayFlyoutWindow.axaml.cs remex.desktop/App.axaml.cs
git commit -m "feat(desktop): transient and pinned tray flyout modes with persisted geometry (RemEx-5g5b7)"
```

---

### Task 8: The action-grid layout

**Files:**
- Modify: `remex.desktop/Views/TrayFlyoutWindow.axaml` (the content, lines 38–126)

**Interfaces:**
- Consumes: `TrayFlyoutViewModel.Tiles`, `.Presence`, `.PinnedSensors`, `.IsPinned` (Task 5); `TrayTileColumnsConverter` (Task 3); handlers `OnHeaderPressed`, `OnTogglePin`, `OnCloseFlyout`, `OnOpenMainApp` (Task 7).

- [ ] **Step 1: Change the window's data type and sizing**

On the `Window` element, set `x:DataType="vm:TrayFlyoutViewModel"` (it is `vm:HomeViewModel` today), `Width="420"`, `Height="400"`, `MinWidth="320"`, `MinHeight="240"`, `MaxWidth="900"`, `MaxHeight="800"`. Remove `SizeToContent` and `CanResize` from the markup — Task 7's `ApplyMode` owns both, and a value in the markup would fight it.

The min and max here must match `TrayFlyoutGeometryValidator`'s constants. They are stated in two places because Avalonia needs literals in XAML; if you change one, change the other.

- [ ] **Step 2: Replace the content**

Replace everything inside the outer `Border` (currently the `Grid` at line 39) with:

```xml
<Grid RowDefinitions="Auto,*,Auto" Margin="16">

    <!-- Status strip. Doubles as the drag handle in pinned mode: this window has no system
         title bar, so without PointerPressed here a pinned window could not be moved. -->
    <Grid Grid.Row="0" ColumnDefinitions="Auto,*,Auto,Auto,Auto"
          PointerPressed="OnHeaderPressed"
          Background="Transparent"
          AutomationProperties.Name="{local:Localize A11y_TrayFlyout_Header}">

        <Ellipse Grid.Column="0" Width="10" Height="10" Margin="0,0,10,0"
                 VerticalAlignment="Center"
                 Classes="status-dot"
                 Classes.connected="{Binding Presence.IsPhoneAttached}"
                 AutomationProperties.Name="{Binding Presence.PresenceAccessibleName}"/>

        <StackPanel Grid.Column="1" Spacing="1" VerticalAlignment="Center">
            <TextBlock Text="{Binding Presence.PresenceText}"
                       FontSize="13" FontWeight="SemiBold"
                       TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource TextPrimaryBrush}"/>
            <TextBlock Text="{Binding Connection.StatusText}"
                       FontSize="10"
                       TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource TextMutedBrush}"/>
        </StackPanel>

        <Button Grid.Column="2" Classes="tray-chip" Click="OnOpenMainApp"
                Content="&#xE115;"
                AutomationProperties.Name="{local:Localize Tray_Menu_Settings}"/>

        <!-- ONE-WAY ON PURPOSE. A two-way IsChecked binding writes IsPinned before the Click
             handler runs, so OnTogglePin would read the already-flipped value and flip it
             straight back — the pin button would appear dead. The handler is the only writer;
             this binding just reflects the result. -->
        <ToggleButton Grid.Column="3" Classes="tray-chip"
                      IsChecked="{Binding IsPinned, Mode=OneWay}"
                      Click="OnTogglePin"
                      Content="&#xE718;"
                      AutomationProperties.Name="{local:Localize A11y_TrayFlyout_Pin}"/>

        <Button Grid.Column="4" Classes="tray-chip" Click="OnCloseFlyout"
                Content="&#x2715;"
                AutomationProperties.Name="{local:Localize A11y_Close}"/>
    </Grid>

    <!-- Action grid. Columns are driven by the window's own width so that resizing reveals
         more of the grid per row instead of only stretching the tiles. -->
    <ItemsControl Grid.Row="1" ItemsSource="{Binding Tiles}" Margin="0,16">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
                <UniformGrid Columns="{Binding $parent[Window].Bounds.Width,
                                       Converter={x:Static conv:TrayTileColumnsConverter.Instance}}"/>
            </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:TrayTile">
                <Button Classes="tray-tile"
                        Command="{Binding Command}"
                        IsEnabled="{Binding IsEnabled}"
                        ToolTip.Tip="{Binding DisabledTooltip}"
                        AutomationProperties.Name="{Binding Label}">
                    <StackPanel Spacing="6" HorizontalAlignment="Center" VerticalAlignment="Center">
                        <Path Data="{Binding Icon}"
                              Fill="{DynamicResource AccentPrimaryBrush}"
                              Width="18" Height="18" Stretch="Uniform"
                              HorizontalAlignment="Center"/>
                        <TextBlock Text="{Binding Label}"
                                   FontSize="10" FontWeight="Bold"
                                   HorizontalAlignment="Center"
                                   TextTrimming="CharacterEllipsis"
                                   Foreground="{DynamicResource TextPrimaryBrush}"/>
                    </StackPanel>
                </Button>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>

    <!-- Sensor strip. Collapses entirely when nothing is pinned: a command center with no
         telemetry should just be shorter, not show a paragraph explaining its own emptiness. -->
    <ScrollViewer Grid.Row="2"
                  HorizontalScrollBarVisibility="Auto"
                  VerticalScrollBarVisibility="Disabled"
                  IsVisible="{Binding PinnedSensors.Count}">
        <ItemsControl ItemsSource="{Binding PinnedSensors}">
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                    <StackPanel Orientation="Horizontal" Spacing="16"/>
                </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:SensorViewModel">
                    <StackPanel Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
                        <TextBlock Text="{Binding Name}"
                                   FontSize="9" FontWeight="Black" LetterSpacing="1"
                                   VerticalAlignment="Center"
                                   Foreground="{DynamicResource TextMutedBrush}"/>
                        <TextBlock FontSize="12" FontWeight="Bold"
                                   VerticalAlignment="Center"
                                   FontFamily="{StaticResource JetBrainsMono}"
                                   Foreground="{DynamicResource TextPrimaryBrush}">
                            <TextBlock.Text>
                                <MultiBinding Converter="{x:Static conv:SensorValueConverter.Instance}">
                                    <Binding Path="Value"/>
                                    <Binding Path="Unit"/>
                                </MultiBinding>
                            </TextBlock.Text>
                        </TextBlock>
                        <ctrl:SparklineControl History="{Binding History}"
                                               GraphType="{Binding ResolvedGraphType}"
                                               AccentColor="{Binding Theme.AccentColor,
                                                             Converter={x:Static conv:HexToColorConverter.Instance}}"
                                               Width="44" Height="16"
                                               Opacity="0.7" VerticalAlignment="Center"/>
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </ScrollViewer>
</Grid>
```

One note on that markup: `IsVisible="{Binding PinnedSensors.Count}"` relies on an int-to-bool coercion. The existing markup at `TrayFlyoutWindow.axaml:108` uses `{Binding !PinnedSensors.Count}`, so the coercion is known to work in this codebase — but that is the *negated* form. If the strip shows while empty, the positive form is not supported; add a `HasPinnedSensors` bool to the view model rather than inventing a converter.

Note also that `PinnedSensors` is an `ObservableCollection`, so `.Count` raises change notification and the strip appears and disappears live as sensors are pinned.

- [ ] **Step 3: Add the tile and chip styles**

In the `Window.Styles` block, replace the `Button.tray-btn` style with:

```xml
<Style Selector="Button.tray-tile">
    <Setter Property="Background" Value="{DynamicResource CardBackgroundBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource CardBorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="{DynamicResource CornerRadiusMedium}"/>
    <Setter Property="Margin" Value="4"/>
    <Setter Property="Height" Value="66"/>
    <Setter Property="HorizontalAlignment" Value="Stretch"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Transitions">
        <Transitions>
            <BrushTransition Property="Background" Duration="0:0:0.15" Easing="CubicEaseOut"/>
        </Transitions>
    </Setter>
</Style>

<Style Selector="Button.tray-tile:pointerover">
    <Setter Property="Background" Value="{DynamicResource CardBackgroundHoverBrush}"/>
</Style>

<Style Selector="Button.tray-tile:disabled">
    <Setter Property="Opacity" Value="0.4"/>
    <Setter Property="Cursor" Value="Arrow"/>
</Style>

<Style Selector="Button.tray-chip, ToggleButton.tray-chip">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Width" Value="28"/>
    <Setter Property="Height" Value="28"/>
    <Setter Property="Padding" Value="0"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/>
    <Setter Property="Cursor" Value="Hand"/>
</Style>

<Style Selector="Button.tray-chip:pointerover, ToggleButton.tray-chip:pointerover">
    <Setter Property="Foreground" Value="{DynamicResource AccentPrimaryBrush}"/>
</Style>
```

The `Border.widget-card` style is now unused — delete it rather than leaving it to rot.

- [ ] **Step 4: Wire the Power submenu**

The Power tile carries `HasSubmenu = true` but nothing opens yet. A `Button.Flyout` cannot be set conditionally from inside a single `DataTemplate`, so the template holds **two** buttons in a `Panel` and swaps their visibility. Replace the `DataTemplate` body from Step 2 with:

```xml
<DataTemplate x:DataType="vm:TrayTile">
    <Panel>
        <!-- Ordinary tile -->
        <Button Classes="tray-tile"
                IsVisible="{Binding !HasSubmenu}"
                Command="{Binding Command}"
                IsEnabled="{Binding IsEnabled}"
                ToolTip.Tip="{Binding DisabledTooltip}"
                AutomationProperties.Name="{Binding Label}"
                ContentTemplate="{StaticResource TrayTileFace}"
                Content="{Binding}"/>

        <!-- Submenu tile. A second Button rather than a conditional Flyout, because
             Button.Flyout cannot be set per-item from one DataTemplate. The face is shared
             through TrayTileFace so the two cannot drift apart visually. -->
        <Button Classes="tray-tile"
                IsVisible="{Binding HasSubmenu}"
                AutomationProperties.Name="{Binding Label}"
                ContentTemplate="{StaticResource TrayTileFace}"
                Content="{Binding}">
            <Button.Flyout>
                <MenuFlyout Placement="Top">
                    <MenuItem Header="{local:Localize Tray_Power_Restart}"
                              Command="{Binding $parent[Window].((vm:TrayFlyoutViewModel)DataContext).InvokePowerCommand}"
                              CommandParameter="{x:Static vm:TrayPowerAction.Restart}"/>
                    <MenuItem Header="{local:Localize Tray_Power_Shutdown}"
                              Command="{Binding $parent[Window].((vm:TrayFlyoutViewModel)DataContext).InvokePowerCommand}"
                              CommandParameter="{x:Static vm:TrayPowerAction.Shutdown}"/>
                    <MenuItem Header="{local:Localize Tray_Power_SignOut}"
                              Command="{Binding $parent[Window].((vm:TrayFlyoutViewModel)DataContext).InvokePowerCommand}"
                              CommandParameter="{x:Static vm:TrayPowerAction.SignOut}"/>
                    <MenuItem Header="{local:Localize Tray_Power_Hibernate}"
                              Command="{Binding $parent[Window].((vm:TrayFlyoutViewModel)DataContext).InvokePowerCommand}"
                              CommandParameter="{x:Static vm:TrayPowerAction.Hibernate}"/>
                </MenuFlyout>
            </Button.Flyout>
        </Button>
    </Panel>
</DataTemplate>
```

And add the shared face to `Window.Resources` (create the block if the window has none):

```xml
<Window.Resources>
    <DataTemplate x:Key="TrayTileFace" x:DataType="vm:TrayTile">
        <StackPanel Spacing="6" HorizontalAlignment="Center" VerticalAlignment="Center">
            <Path Data="{Binding Icon}"
                  Fill="{DynamicResource AccentPrimaryBrush}"
                  Width="18" Height="18" Stretch="Uniform"
                  HorizontalAlignment="Center"/>
            <TextBlock Text="{Binding Label}"
                       FontSize="10" FontWeight="Bold"
                       HorizontalAlignment="Center"
                       TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource TextPrimaryBrush}"/>
        </StackPanel>
    </DataTemplate>
</Window.Resources>
```

`InvokePowerCommand` is the generated name for `InvokePowerAsync` and lives on the window's `DataContext`, not on the `TrayTile` — hence the `$parent[Window]` hop. `Placement="Top"` matters: the flyout sits above the tile because the window itself is usually at the bottom-right of the screen, where a downward flyout would open off-screen.

- [ ] **Step 5: Build and deploy**

```powershell
dotnet build remex.desktop -c Release
pwsh -File scripts/update-local-install.ps1
```

- [ ] **Step 6: Verify the layout on the installed exe**

1. Resize the pinned window narrow → 2 columns. Widen → 3, then 4. The tiles reflow rather than stretch.
2. With no phone attached, the Remote tile is dimmed and its tooltip explains why. Attach a phone → it enables **without reopening the flyout**.
3. Unpin all sensors → the bottom strip disappears entirely and the window is shorter.
4. Pin six sensors → the strip scrolls horizontally rather than widening the window.
5. Power ▾ → Restart shows a confirmation; cancelling it does not restart. **Do not confirm it.**
6. All four themes, SolarFlare last and most carefully.

- [ ] **Step 7: Commit**

```powershell
git add remex.desktop/Views/TrayFlyoutWindow.axaml
git commit -m "feat(desktop): action-grid tray flyout with reflowing columns (RemEx-el6gh)"
```

---

### Task 9: Status-aware tray context menu

**Files:**
- Modify: `remex.desktop/App.axaml:329-343`
- Modify: `remex.desktop/App.axaml.cs:598-630`

**Interfaces:**
- Consumes: `PhonePresenceMonitor.Instance`; the `Tray_Menu_*` strings from Task 4.
- Produces: `App.BuildTrayMenu()` and `App.RefreshTrayMenu()`, replacing `FindThemeToggleMenuItem()` and the index-based `UpdateTrayMenuHeaders()`.

- [ ] **Step 1: Strip the declared menu from `App.axaml`**

Replace lines 332–340 (the whole `<TrayIcon.Menu>` block) with nothing, leaving:

```xml
<TrayIcon Icon="avares://Remex.Desktop/Assets/icon.png"
          ToolTipText="{local:Localize Tray_TooltipDefault}"
          IsVisible="True"
          Clicked="OnTrayIconClicked" />
```

- [ ] **Step 2: Build the menu in code**

In `remex.desktop/App.axaml.cs`, delete `FindThemeToggleMenuItem()` and `UpdateTrayMenuHeaders()` and add:

```csharp
    private NativeMenuItem? _statusHeaderItem;
    private NativeMenuItem? _remoteDesktopItem;

    /// <summary>
    /// Builds the tray context menu in code.
    /// </summary>
    /// <remarks>
    /// IN CODE RATHER THAN IN App.axaml BECAUSE THE ITEMS ARE NOT STATIC. The declared menu could
    /// only be updated by index — <c>menu.Items[2] as NativeMenuItem</c> — which silently did
    /// nothing the moment anyone inserted an item, and could not enable or disable anything at all.
    /// Holding the two live items in fields removes both problems.
    /// </remarks>
    private void BuildTrayMenu()
    {
        var strings = LocalizationService.Instance;

        _statusHeaderItem = new NativeMenuItem { IsEnabled = false };
        _remoteDesktopItem = new NativeMenuItem { Header = strings["Tray_Menu_RemoteDesktop"] };
        _remoteDesktopItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToRemoteDesktop();
        };

        var lockItem = new NativeMenuItem { Header = strings["Tray_Menu_LockPc"] };
        lockItem.Click += async (_, _) =>
            await Services.GetRequiredService<ShellViewModel>().Connection.LockAsync();

        var transfersItem = new NativeMenuItem { Header = strings["Tray_Menu_OpenTransfers"] };
        transfersItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToFileTransfer();
        };

        var pairItem = new NativeMenuItem { Header = strings["Tray_Menu_PairDevice"] };
        pairItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToSettings();
        };

        var showItem = new NativeMenuItem { Header = strings["Tray_ShowMainWindow"] };
        showItem.Click += OnShowMainWindow;

        var settingsItem = new NativeMenuItem { Header = strings["Tray_Menu_Settings"] };
        settingsItem.Click += (_, _) =>
        {
            BringMainWindowToFront();
            Services.GetRequiredService<ShellViewModel>().NavigateToSettings();
        };

        var exitItem = new NativeMenuItem { Header = strings["Tray_Exit"] };
        exitItem.Click += OnExitApp;

        var menu = new NativeMenu();
        menu.Items.Add(_statusHeaderItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(lockItem);
        menu.Items.Add(_remoteDesktopItem);
        menu.Items.Add(transfersItem);
        menu.Items.Add(pairItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        if (TrayIcon.GetIcons(this)?.FirstOrDefault() is { } icon)
            icon.Menu = menu;

        RefreshTrayMenu();
    }

    /// <summary>Republishes the state-dependent parts: the status header and Remote's enablement.</summary>
    private void RefreshTrayMenu()
    {
        var presence = PhonePresenceMonitor.Instance;

        if (_statusHeaderItem is not null)
            _statusHeaderItem.Header = presence.PresenceText;

        if (_remoteDesktopItem is not null)
            _remoteDesktopItem.IsEnabled = TrayTileRules.IsRemoteDesktopEnabled(presence.IsPhoneAttached);
    }
```

`RefreshTrayMenu` reuses `TrayTileRules.IsRemoteDesktopEnabled` rather than restating `IsPhoneAttached` — one rule, two surfaces, no chance of them disagreeing. This is the same mistake the flyout's presence dot made before RemEx-7zzw.

**No unit test for this task, deliberately.** `NativeMenu` and `TrayIcon.GetIcons` need a running Avalonia application, and `remex.desktop.tests` has no headless Avalonia package — every test in it is pure logic. The one piece of judgement in this menu, "is Remote enabled", is already tested through `TrayTileRules`; the rest is wiring, verified by the manual steps below. Do not add a headless-Avalonia dependency to the test project for this alone.

- [ ] **Step 3: Call `BuildTrayMenu` and subscribe to presence**

Find `WireTrayTooltipToPhonePresence()` (`App.axaml.cs:443`) — it already subscribes to `PhonePresenceMonitor` for the tooltip. Call `BuildTrayMenu()` immediately before it during startup, and add a `RefreshTrayMenu()` call inside that existing subscription's handler so both the tooltip and the menu update from the one 3-second poll. Also call `BuildTrayMenu()` from wherever `UpdateTrayMenuHeaders()` was previously called on a locale change — rebuilding is the simplest correct response to a language switch.

- [ ] **Step 4: Delete the retired handlers**

`OnToggleLiveGlance` (`App.axaml.cs:408`) and the theme-toggle click handler are now unreferenced. Delete them and any `_themeToggleMenuItem` field. Build with warnings-as-errors if the project is configured that way; otherwise grep for each name to confirm nothing else calls it.

- [ ] **Step 5: Build, deploy, verify**

```powershell
dotnet build remex.desktop -c Release
pwsh -File scripts/update-local-install.ps1
```

1. Right-click the tray icon → the top line names the connected phone (or says none is connected) and cannot be clicked.
2. With no phone attached, "Remote desktop" is greyed. Attach one, wait ~3 seconds, reopen the menu → it is enabled.
3. Every item does what it says.
4. Switch language in Settings → reopen the menu → every item is in the new language, including the status header.

- [ ] **Step 6: Commit**

```powershell
git add remex.desktop/App.axaml remex.desktop/App.axaml.cs
git commit -m "feat(desktop): status-aware tray menu built in code (RemEx-0gbjy)"
```

---

### Task 10: Full gate and close-out

**Files:** none — verification only.

- [ ] **Step 1: Run the full verification gate**

```powershell
pwsh -File scripts/verify.ps1 -Check
```

Expected: PASS, with the test total **higher** than the pre-work baseline by the number of tests added in Tasks 1–3 (about 40, counting each `[Theory]` row individually). A green run whose total did not rise means the run is stale and did not include your work — record the baseline total before starting Task 1 so you have something to compare against.

- [ ] **Step 2: Run the localization gate**

```powershell
pwsh -File scripts/check-localization.ps1
```

Expected: PASS on parity, staleness and references. The reference axis is the one that catches a `Tray_LiveGlance` deletion that missed a call site.

- [ ] **Step 3: Request a C# review**

Dispatch the `csharp-reviewer` agent over the full diff for this epic. Its checklist covers the things most likely to have slipped here: banned `ConfigureAwait`, four-theme safety, 9-file localization, and the Avalonia traps that have shipped bugs in this repo before.

- [ ] **Step 4: Update the CHANGELOG**

Add an entry describing the tray command center, in Connor's voice: first person, plain, understated, progress-update framing. No em-dashes, no marketing adjectives.

- [ ] **Step 5: Close the beads**

```powershell
bd close RemEx-zu09j RemEx-7n5gr RemEx-3m4ho RemEx-5g5b7 RemEx-el6gh RemEx-0gbjy RemEx-sr5uq
bd close RemEx-exfu7
```

Close each only after its own verification passed. `RemEx-74kfg` (the "Send to phone" mislabel) and `RemEx-uov9y` (the PC→phone channel epic) stay open — both are out of scope for this work.

- [ ] **Step 6: Commit the CHANGELOG**

```powershell
git add CHANGELOG.md
git commit -m "docs(changelog): tray command center (RemEx-exfu7)"
```
