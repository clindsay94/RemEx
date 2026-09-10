using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using FluentAssertions;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Guards that switching theme replaces the base theme dictionary and nothing else (RemEx-gcqw5).
/// </summary>
/// <remarks>
/// <para>
/// THE SILENT ONE. <c>ApplyBaseThemeInternal</c> used to clear every merged dictionary that was not
/// the override dictionary before inserting the newly selected theme file. That does replace the
/// previous theme, but it also deletes any other <c>ResourceInclude</c> <c>App.axaml</c> merges —
/// on the FIRST theme switch, never at startup, with no exception and no log line. RemEx-qbzl1 had
/// to move the Card <c>ControlTheme</c> out of a merged dictionary and into an own key of
/// <c>Application.Resources</c> to escape it (see <c>CardSurfaceTests</c>); the next merged
/// dictionary anyone added would have hit the same trap.
/// </para>
/// <para>
/// TWO KINDS OF BYSTANDER ARE COVERED, because the narrow fix only handles one of them. A plain
/// <see cref="ResourceDictionary"/> is never mistakable for a theme file; a
/// <see cref="ResourceInclude"/> whose source sits under <c>Themes/</c> very much is, and one
/// already exists — <c>Themes/Chrome/WindowChrome.axaml</c>, merged by <c>MainWindow.axaml</c>.
/// A predicate that matched the folder prefix rather than the four theme files would eat that
/// include on the first switch and leave the real theme behind the selection, where it keeps
/// painting. Both bystanders are therefore in the fixture.
/// </para>
/// <para>
/// ASSERTED ON THE COLLECTION, NOT A LIVE APP. This assembly has no <c>Avalonia.Headless</c>
/// reference (see <c>DispatcherPostedWorkTests</c>), so there is no <c>Application.Current</c> for
/// <c>ApplyBaseThemeInternal</c> to act on — it returns at its first line. The swap it delegates to
/// is a static that takes the merged-dictionary list, so the real production code path is exercised
/// here against a plain <see cref="ResourceDictionary"/> built into the shape <c>App.axaml</c> plus
/// <c>ThemeService</c>'s constructor produce.
/// </para>
/// </remarks>
public class ThemeSwapMergedDictionaryTests
{
    /// <summary>
    /// The <c>Themes/</c> file <c>MainWindow.axaml</c> merges, which is NOT a base theme. Spelled
    /// out rather than derived, because the whole point is that it shares the folder prefix with
    /// the four theme files and must survive anyway.
    /// </summary>
    private const string ChromeSource = "avares://Remex.Desktop/Themes/Chrome/WindowChrome.axaml";

    /// <summary>
    /// Every source string a base theme file can have, written out rather than reused from
    /// production. If <c>ThemeService</c> ever widens its own predicate — back to a folder prefix,
    /// say — the tests below have to keep counting theme includes the strict way, or they would
    /// widen with it and stop noticing.
    /// </summary>
    private static readonly HashSet<string> BaseThemeSources = new(StringComparer.Ordinal)
    {
        "avares://Remex.Desktop/Themes/BaseDarkGlass.axaml",
        "avares://Remex.Desktop/Themes/CyberNOC.axaml",
        "avares://Remex.Desktop/Themes/SolarFlare.axaml",
        "avares://Remex.Desktop/Themes/Monolith.axaml",
    };

    /// <summary>Every preset a user can land on, plus <c>Dynamic</c>, which has no file of its own.</summary>
    public static TheoryData<AppTheme> AllThemes() =>
        new(Enum.GetValues<AppTheme>());

    [Fact]
    public void EveryBaseThemeUriIsOneOfTheFourFiles()
    {
        // Pins the set the strict tests below are written against. Adding a sixth AppTheme with a
        // new file is fine; doing it without updating BaseThemeSources would quietly turn
        // ThemeIncludes blind to it, and this fails first instead.
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            BaseThemeSources.Should().Contain(ThemeService.BaseThemeUri(theme).OriginalString,
                $"{theme} resolves to a theme file this fixture has to know about");
        }
    }

    [Fact]
    public void ANonThemeMergedDictionarySurvivesAFullSwitchAcrossEveryPreset()
    {
        var resources = AppResourcesShape(out var foreign, out var chrome, out var overrides);

        IResourceProvider? tracked = null;
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            tracked = ThemeService.SwapBaseTheme(
                resources.MergedDictionaries, tracked, ThemeService.BaseThemeUri(theme));

            resources.MergedDictionaries.Should().Contain(foreign,
                $"switching to {theme} replaces the base theme, and a merged dictionary that is not "
                + "a theme is not the base theme");
            resources.MergedDictionaries.Should().Contain(chrome,
                $"switching to {theme} must not eat WindowChrome either, even though its source "
                + "sits under the same Themes/ folder as the four theme files");
            resources.MergedDictionaries.Should().Contain(overrides,
                $"switching to {theme} must not drop the live customization overrides either");
        }

        // The point of the guard, stated the way a caller would notice it breaking: the key still
        // resolves THROUGH THE PARENT, which is how every DynamicResource in the app reaches it.
        // Asking `foreign` directly would pass either way — the old loop dropped the dictionary out
        // of MergedDictionaries, it never emptied it — so that form of the assertion was green with
        // or without the fix.
        resources.TryGetResource("ForeignMergedKey", null, out var value).Should().BeTrue(
            "a key in a non-theme merged dictionary has to keep resolving after a theme cycle");
        value.Should().Be("kept");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void SwitchingLeavesExactlyOneBaseTheme_TheOneJustSelected(AppTheme theme)
    {
        var resources = AppResourcesShape(out _, out var chrome, out _);

        var inserted = ThemeService.SwapBaseTheme(
            resources.MergedDictionaries, current: null, ThemeService.BaseThemeUri(theme));

        // The startup include App.axaml declared has to go, even though nothing tracked it: it is
        // still a base theme, and position in this list is priority. Two of them would leave the
        // stale one painting over the selection.
        ThemeIncludes(resources).Should().ContainSingle().Which.Should().BeSameAs(inserted);
        ((ResourceInclude)inserted).Source.Should().Be(ThemeService.BaseThemeUri(theme));

        // And the identification must be by file, not by folder: a prefix match would have taken
        // WindowChrome instead and left the declared BaseDarkGlass include outranking `inserted`.
        resources.MergedDictionaries.Should().Contain(chrome);
    }

    [Fact]
    public void ADuplicateBaseThemeIsSweptOut_NotLeftBehindToOutrankTheSelection()
    {
        var resources = AppResourcesShape(out _, out _, out _);
        var stale = ThemeService.BaseThemeUri(AppTheme.Monolith);
        resources.MergedDictionaries.Insert(1, new ResourceInclude(stale) { Source = stale });

        var inserted = ThemeService.SwapBaseTheme(
            resources.MergedDictionaries, current: null, ThemeService.BaseThemeUri(AppTheme.CyberNOC));

        // Removing only the FIRST match would leave Monolith merged above the selection, which is
        // the wrong-palette-forever state. The clear-everything loop this replaced self-healed that
        // by construction; the sweep is what keeps that property.
        ThemeIncludes(resources).Should().ContainSingle().Which.Should().BeSameAs(inserted);
    }

    [Fact]
    public void TheNewThemeGoesUnderTheOverrideDictionary_NotOverIt()
    {
        var resources = AppResourcesShape(out _, out _, out var overrides);

        var inserted = ThemeService.SwapBaseTheme(
            resources.MergedDictionaries, current: null, ThemeService.BaseThemeUri(AppTheme.CyberNOC));

        // ThemeService's constructor appends the override dictionary "after theme files so it takes
        // precedence". Insert-at-0 is what keeps that true; an append would put the theme file on
        // top of the user's own accent, radius and opacity values.
        resources.MergedDictionaries.IndexOf(inserted).Should().Be(0);
        resources.MergedDictionaries.IndexOf(overrides)
            .Should().BeGreaterThan(resources.MergedDictionaries.IndexOf(inserted));
    }

    /// <summary>
    /// <c>Application.Resources</c> as the app would build it once anything else under
    /// <c>Themes/</c> is merged at app scope: a <c>ResourceInclude</c> that is NOT a theme, the
    /// base theme <c>App.axaml</c> declares, a plain merged dictionary standing in for any other
    /// app-scope merge, then the override dictionary <c>ThemeService</c>'s constructor appends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CHROME SITS BEFORE THE THEME, DELIBERATELY. A folder-prefix predicate that took only its
    /// first match would find the declared theme first if the theme came first, and the bug would
    /// hide. In this order it takes WindowChrome instead: chrome styling disappears AND the real
    /// BaseDarkGlass include survives at a higher index than the selection, so theme switching
    /// stops working, silently. Merge order in <c>App.axaml</c> is arbitrary, so the fixture uses
    /// the order that can actually fail.
    /// </para>
    /// <para>
    /// The includes are never loaded — nothing here asks them for a key, and the lookup in the
    /// survives-a-cycle test finds <c>ForeignMergedKey</c> in the reverse walk before it reaches
    /// either include — so this needs no Avalonia asset loader.
    /// </para>
    /// </remarks>
    private static ResourceDictionary AppResourcesShape(
        out ResourceDictionary foreign, out ResourceInclude chrome, out ResourceDictionary overrides)
    {
        var declared = ThemeService.BaseThemeUri(AppTheme.BaseDarkGlass);
        var chromeUri = new Uri(ChromeSource);
        chrome = new ResourceInclude(chromeUri) { Source = chromeUri };
        foreign = new ResourceDictionary { ["ForeignMergedKey"] = "kept" };
        overrides = new ResourceDictionary();

        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(chrome);
        resources.MergedDictionaries.Add(new ResourceInclude(declared) { Source = declared });
        resources.MergedDictionaries.Add(foreign);
        resources.MergedDictionaries.Add(overrides);
        return resources;
    }

    private static List<IResourceProvider> ThemeIncludes(ResourceDictionary resources) =>
        resources.MergedDictionaries
            .Where(dictionary => dictionary is ResourceInclude { Source: { } source }
                                 && BaseThemeSources.Contains(source.OriginalString))
            .ToList();
}
