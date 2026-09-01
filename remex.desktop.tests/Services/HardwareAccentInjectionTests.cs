using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Covers the hardware accent injection path (RemEx-w6c4s). Before this, <c>ApplyHardwareAccent</c>
/// overwrote exactly two of the ~56 resources the palette owns (<c>AccentPrimary</c> and its brush)
/// with the literal hardware colour, leaving the other ~54 on whatever the user's own seed had
/// produced — a palette that agreed with itself nowhere. It also had zero tests despite being a
/// user-facing toggle (Personalization → Hardware Sync).
/// </summary>
/// <remarks>
/// <para>
/// THE COLOUR IS A SEED NOW, NOT A LITERAL. <c>ApplyHardwareAccent</c> runs the injected colour
/// through the same <c>DynamicColorGenerator.Generate</c> → <c>ApplyCustomizationCore</c> path any
/// other palette change uses, so every derived resource moves together. <c>ApplyCustomizationCore</c>
/// is <c>internal</c> specifically so this file can call it directly and read
/// <c>GetOverrideResource</c> back — this assembly has no <c>Avalonia.Headless</c> reference (see
/// <c>DispatcherPostedWorkTests</c>, <c>ThemeKeyCoverageTests</c>), so nothing pumps
/// <c>Dispatcher.UIThread</c> and a callback posted from <c>ApplyCustomization</c>/
/// <c>ApplyHardwareAccent</c>/<c>ClearHardwareAccent</c> never runs in a unit test. Calling the core
/// method directly is what production does ONE dispatcher hop later; every branch inside it is
/// already null-tolerant on <c>Application.Current</c> for the same reason.
/// </para>
/// <para>
/// THE OTHER HALF OF THE BEAD: the user's seed must survive hardware sync being turned on, changed,
/// and turned back off. <c>ThemeService.UserSettings</c> is asserted directly to prove a hardware
/// colour never overwrites it — that field, not the saved profile, is what a hardware injection is
/// allowed to touch.
/// </para>
/// </remarks>
public class HardwareAccentInjectionTests
{
    private static CustomizationSettings UserSettings(string accent) => new()
    {
        AccentColor = accent,
        SchemeVariant = "TonalSpot",
        ThemeMode = ThemeModes.Dark,
        ThemeContrast = 0.0,
    };

    /// <summary>
    /// Several independently-derived roles, not just the accent. A restore that put
    /// <c>AccentPrimary</c> back while leaving the surface and text roles on the hardware seed
    /// would satisfy a single-key assertion and still look wrong on screen, so the assertion has to
    /// span roles that come off different tonal palettes.
    /// </summary>
    private static Dictionary<string, string> Snapshot(ThemeService theme) =>
        new[] { "AccentPrimary", "Surface", "TextPrimary", "TextSecondary", "CardBackground" }
            .ToDictionary(key => key, key => theme.GetOverrideResource(key)?.ToString() ?? "<unset>");

    [Fact]
    public void ApplyHardwareAccent_NeverOverwritesTheUsersStoredSeed()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var user = UserSettings("#224466");
        theme.ApplyCustomization(user);

        theme.ApplyHardwareAccent(Color.FromRgb(0xAA, 0x33, 0x66));

        // The stored preference is exactly what was requested — a hardware colour is layered on
        // top for display, it is never written back into what "the user's seed" means.
        theme.UserSettings.Should().Be(user);
        theme.UserSettings!.AccentColor.Should().Be("#224466");
    }

    [Fact]
    public void WithHardwareAccentOverride_SwapsOnlyTheAccentColour_AndNeverMutatesTheOriginalRecord()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var user = UserSettings("#224466") with { CornerRadius = 20, GlowStrength = 5 };
        theme.ApplyCustomization(user);

        theme.ApplyHardwareAccent(Color.FromRgb(0xAA, 0x33, 0x66));
        var effective = theme.WithHardwareAccentOverride(user);

        effective.AccentColor.Should().Be("#AA3366", "the hardware colour becomes the seed");
        effective.CornerRadius.Should().Be(20, "everything else about the user's settings carries through unchanged");
        effective.GlowStrength.Should().Be(5);

        // CustomizationSettings is a record: `with` returns a new instance. The caller's own copy
        // — the one that gets saved — is a completely different object and was never touched.
        user.AccentColor.Should().Be("#224466");
    }

    [Fact]
    public void ClearHardwareAccent_WithNothingActive_IsANoOp()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };

        var act = () => theme.ClearHardwareAccent();

        act.Should().NotThrow("disabling sync before anything ever injected a colour must be harmless");
        theme.HardwareAccentOverride.Should().BeNull();
    }

    [Fact]
    public void ClearHardwareAccent_RemovesTheOverride_SoTheEffectiveSettingsRevertToTheUsersSeed()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var user = UserSettings("#224466");
        theme.ApplyCustomization(user);
        theme.ApplyHardwareAccent(Color.FromRgb(0xAA, 0x33, 0x66));
        theme.HardwareAccentOverride.Should().NotBeNull();

        theme.ClearHardwareAccent();

        theme.HardwareAccentOverride.Should().BeNull();
        theme.WithHardwareAccentOverride(user).AccentColor.Should().Be("#224466",
            "with the override cleared, the effective settings must be the user's own seed again");
    }

    [Fact]
    public void ApplyCustomizationCore_DerivesTheWholePaletteFromTheInjectedSeed_NotJustOneBrush()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var hardwareColor = Color.FromRgb(0x22, 0x99, 0x55);
        var effective = UserSettings("#224466") with { AccentColor = "#229955" };

        theme.ApplyCustomizationCore(effective);

        var expected = DynamicColorGenerator.Generate(
            hardwareColor, effective.SchemeVariant, isDark: true, contrast: effective.ThemeContrast);

        // FOUR INDEPENDENT ROLES, not one. The bug this replaces overwrote AccentPrimary and its
        // brush only — every other key here would still hold whatever the PREVIOUS seed produced,
        // or nothing at all on a service that had never applied anything. Matching all four against
        // a palette generated straight from the same seed is what "coherent" means: every role
        // traces back to one Generate() call, not a patchwork of one new colour and 54 old ones.
        theme.GetOverrideResource("AccentPrimary").Should().Be(expected.Primary);
        theme.GetOverrideResource("GlassBaseDark").Should().Be(expected.Surface);
        theme.GetOverrideResource("TextPrimary").Should().Be(expected.OnSurface);
        theme.GetOverrideResource("SystemAccentColor").Should().Be(expected.Primary);

        theme.GetOverrideResource("AccentPrimaryBrush").Should().BeOfType<SolidColorBrush>()
            .Which.Color.Should().Be(expected.Primary);
    }

    [Fact]
    public void InjectThenClear_RestoresExactlyWhatWasPaintedBeforeTheOverride()
    {
        // NOTHING IN THIS TEST DRIVES AN APPLY. That is the entire point, and the first version of
        // it got this wrong: it called ApplyCustomizationCore itself after ClearHardwareAccent, so
        // it went green with the restore DELETED from ClearHardwareAccent — measured by injection,
        // build exit 0. All it really proved was that the generator is deterministic. Redirecting
        // the dispatcher hop inline lets the production methods do their own work where the test
        // can see it, so a missing restore now fails here rather than in nothing.
        var theme = new ThemeService { PostToUiThread = action => action() };

        theme.ApplyCustomization(UserSettings("#224466"));
        var baseline = Snapshot(theme);

        theme.ApplyHardwareAccent(Color.FromRgb(0xAA, 0x33, 0x66));
        var injected = Snapshot(theme);
        injected.Should().NotBeEquivalentTo(baseline, "a different seed must paint a different palette");

        theme.ClearHardwareAccent();
        Snapshot(theme).Should().BeEquivalentTo(baseline,
            "turning hardware sync off must restore the user's chosen seed exactly — and restore " +
            "the WHOLE palette, not just the accent, or roles derived from the hardware colour are " +
            "left behind under an accent that no longer matches them");

        theme.UserSettings!.AccentColor.Should().Be("#224466", "the stored seed was never overwritten by the override");
    }

    [Fact]
    public void ApplyHardwareAccentAndClearHardwareAccent_PostToTheSameApplyPathAsAnyOtherChange()
    {
        // ASSERTED ON THE SOURCE, same reason as ThemeKeyCoverageTests: production's dispatcher hop
        // cannot be exercised here. What is pinned is the shape that makes the crossfade free — both
        // methods must resolve to ApplyCustomizationCore through Dispatcher.UIThread.Post, exactly
        // like ApplyCustomization does, rather than a second bespoke apply path.
        //
        // CAPTURED BY \n    \}, same as ThemeKeyCoverageTests.TheCustomAccentBoxRejectsAHexThatWillNotParse
        // — a plain [^}]* balked on `is not { } baseline`'s own inner brace pair.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        var applyBody = Regex.Match(source, @"public void ApplyHardwareAccent\(Color color\).*?\n    \}\);", RegexOptions.Singleline);
        applyBody.Success.Should().BeTrue("ApplyHardwareAccent moved or changed shape");
        applyBody.Value.Should().Contain("PostToUiThread(",
            "a hardware injection must ride the same posted apply as every other palette change");
        applyBody.Value.Should().Contain("ApplyCustomizationCore(",
            "a hardware injection must resolve to the one apply path, not a bespoke one");

        var clearBody = Regex.Match(source, @"public void ClearHardwareAccent\(\).*?\n    \}\);", RegexOptions.Singleline);
        clearBody.Success.Should().BeTrue("ClearHardwareAccent moved or changed shape");
        clearBody.Value.Should().Contain("PostToUiThread(",
            "restoring the user's seed must ride the same posted apply too");
        clearBody.Value.Should().Contain("ApplyCustomizationCore(");

        // THE OLD BUG, NAMED SO IT CANNOT COME BACK QUIETLY. This literal shape — writing the raw
        // hardware colour straight onto AccentPrimary — is exactly what this bead replaced.
        source.Should().NotMatchRegex(
            @"SetResourceOverrideInternal\(""AccentPrimary"",\s*color\)",
            "the hardware colour must go through the seed/generator path, never straight onto one brush");
    }

    [Fact]
    public void DisablingHardwareSync_ClearsTheOverrideOnThemeService()
    {
        // ASSERTED ON THE SOURCE for the same reason: HardwareThemeService owns a DispatcherTimer,
        // and constructing/driving one outside a running Avalonia app is exactly the kind of thing
        // this assembly has no headless backend for. The wiring itself is one line and easy to pin.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "HardwareThemeService.cs"));

        source.Should().MatchRegex(
            @"else\s*\{[^}]*_timer\.Stop\(\);[^}]*_themeService\.ClearHardwareAccent\(\);",
            "turning sync off must stop the poller AND restore the user's seed, not just stop polling");
    }

    [Fact]
    public void ApplyingTheSameHardwareColourTwice_AppliesOnce()
    {
        // NO CustomizationApplied EVENT EXISTS ON ThemeService to subscribe to (checked before
        // writing this), so "did a second apply actually happen" is observed the same way
        // ApplyCustomizationCore_DerivesTheWholePaletteFromTheInjectedSeed does: ApplyCustomizationCore
        // (ThemeService.cs:355) always does `new SolidColorBrush(palette.Primary)` when it runs, so
        // the AccentPrimaryBrush *instance* changes on every real apply and stays the exact same
        // reference when the dedupe guard (ThemeService.cs:~635) skips the second call.
        var theme = new ThemeService { PostToUiThread = action => action() };
        theme.ApplyCustomization(UserSettings("#224466"));
        var color = Color.FromRgb(0xAA, 0x33, 0x66);

        theme.ApplyHardwareAccent(color);
        var brushAfterFirstApply = theme.GetOverrideResource("AccentPrimaryBrush");

        theme.ApplyHardwareAccent(color);
        var brushAfterSecondCall = theme.GetOverrideResource("AccentPrimaryBrush");

        brushAfterSecondCall.Should().BeSameAs(brushAfterFirstApply,
            "a second call with the identical colour must be deduped, not regenerate the whole " +
            "palette and restart RemEx-zgtn1's crossfade for no visible change");
        theme.HardwareAccentOverride.Should().Be(color);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
