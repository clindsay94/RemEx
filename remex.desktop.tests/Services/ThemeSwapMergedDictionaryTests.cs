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
    /// <summary>Every preset a user can land on, plus <c>Dynamic</c>, which has no file of its own.</summary>
    public static TheoryData<AppTheme> AllThemes() =>
        new(Enum.GetValues<AppTheme>());

    [Fact]
    public void ANonThemeMergedDictionarySurvivesAFullSwitchAcrossEveryPreset()
    {
        var resources = AppResourcesShape(out var foreign, out var overrides);

        IResourceProvider? tracked = null;
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            tracked = ThemeService.SwapBaseTheme(
                resources.MergedDictionaries, tracked, ThemeService.BaseThemeUri(theme));

            resources.MergedDictionaries.Should().Contain(foreign,
                $"switching to {theme} replaces the base theme, and a merged dictionary that is not "
                + "a theme is not the base theme");
            resources.MergedDictionaries.Should().Contain(overrides,
                $"switching to {theme} must not drop the live customization overrides either");
        }

        // The point of the guard, stated the way a caller would notice it breaking: the key is
        // still reachable. A cleared dictionary resolves nothing and throws nothing.
        foreign.TryGetResource("ForeignMergedKey", null, out var value).Should().BeTrue(
            "a key in a non-theme merged dictionary has to keep resolving after a theme cycle");
        value.Should().Be("kept");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void SwitchingLeavesExactlyOneBaseTheme_TheOneJustSelected(AppTheme theme)
    {
        var resources = AppResourcesShape(out _, out _);

        var inserted = ThemeService.SwapBaseTheme(
            resources.MergedDictionaries, current: null, ThemeService.BaseThemeUri(theme));

        // The startup include App.axaml declared has to go, even though nothing tracked it: it is
        // still a base theme, and position in this list is priority. Two of them would leave the
        // stale one painting over the selection.
        ThemeIncludes(resources).Should().ContainSingle().Which.Should().BeSameAs(inserted);
        ((ResourceInclude)inserted).Source.Should().Be(ThemeService.BaseThemeUri(theme));
    }

    [Fact]
    public void TheNewThemeGoesUnderTheOverrideDictionary_NotOverIt()
    {
        var resources = AppResourcesShape(out _, out var overrides);

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
    /// <c>Application.Resources</c> as the app actually builds it: the base theme <c>App.axaml</c>
    /// declares, then a second merged dictionary standing in for whatever else it merges, then the
    /// override dictionary <c>ThemeService</c>'s constructor appends.
    /// </summary>
    private static ResourceDictionary AppResourcesShape(
        out ResourceDictionary foreign, out ResourceDictionary overrides)
    {
        var declared = ThemeService.BaseThemeUri(AppTheme.BaseDarkGlass);
        foreign = new ResourceDictionary { ["ForeignMergedKey"] = "kept" };
        overrides = new ResourceDictionary();

        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceInclude(declared) { Source = declared });
        resources.MergedDictionaries.Add(foreign);
        resources.MergedDictionaries.Add(overrides);
        return resources;
    }

    private static List<IResourceProvider> ThemeIncludes(ResourceDictionary resources) =>
        resources.MergedDictionaries
            .Where(dictionary => dictionary is ResourceInclude { Source: { } source }
                                 && source.OriginalString.StartsWith(
                                     "avares://Remex.Desktop/Themes/", StringComparison.Ordinal))
            .ToList();
}
