using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-kgs7g. Pins the keyboard focus ring in <c>App.axaml</c>, which is the one accessibility
/// feature in this app that can disappear without anything noticing.
/// </summary>
/// <remarks>
/// <para>
/// A style selector that matches nothing is not an error in Avalonia. It compiles, it is applied to
/// every control, it never fires, and there is no warning and no failing test — the only symptom is
/// that somebody navigating by keyboard cannot see where they are. That is what happened when the
/// Fluent themes were replaced with Material: our
/// <c>ListBoxItem:focus-visible /template/ ContentPresenter</c> selector kept compiling, but
/// Material's ListBoxItem template has no ContentPresenter in it at all.
/// </para>
/// <para>
/// There is nothing underneath to catch it either. Material sets <c>FocusAdorner="{x:Null}"</c> on
/// nine of the thirteen controls checked in the RemEx-3e65x audit, ListBoxItem among them.
/// </para>
/// <para>
/// This is a source-level pin rather than a rendering test for the reason recorded in RemEx-r8c6:
/// there is no Avalonia headless harness in this assembly, so no test here can focus a control and
/// look at it. The defect is visible in the markup, and the markup is where it will be reintroduced
/// — by someone migrating another control onto a Material template and copying the selector shape
/// from the three above it that legitimately still use it.
/// </para>
/// </remarks>
public class FocusVisibleStyleGuardTests
{
    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private static string AppMarkup() => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

    /// <summary>Matches one <c>:focus-visible</c> style, capturing the control and the rest of the selector.</summary>
    private static readonly Regex FocusVisibleStyle =
        new(@"<Style\s+Selector=""(?<control>\w+):focus-visible(?<rest>[^""]*)"">(?<body>.*?)</Style>",
            RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// The only controls whose Material template still contains a <c>ContentPresenter</c> for the
    /// selector to reach, verified against Material.Avalonia 3.19.0 — each templates one named
    /// <c>PART_ContentPresenter</c>, and the selector matches on type rather than name.
    /// </summary>
    private static readonly string[] MayUseTheTemplateForm = ["Button", "ToggleButton", "RepeatButton"];

    /// <summary>
    /// Every control the app rings on focus. Losing one is a silent accessibility regression, so the
    /// list is pinned rather than derived.
    /// </summary>
    private static readonly string[] RingedControls =
    [
        "Button", "ToggleButton", "RepeatButton", "ListBoxItem",
        "TextBox", "ComboBox", "CheckBox", "RadioButton", "ToggleSwitch", "Slider",
    ];

    [Fact]
    public void EveryInteractiveControlStillHasAFocusRing()
    {
        var found = Styles().Select(s => s.Control).ToArray();

        found.Should().BeEquivalentTo(RingedControls);
    }

    [Fact]
    public void OnlyTheControlsThatActuallyTemplateAContentPresenterReachIntoTheTemplate()
    {
        // The guard itself. A selector reaching for a template part that the theme does not contain
        // matches nothing, silently, and the ring is gone.
        var reachingIntoTheTemplate = Styles()
            .Where(s => s.Suffix.Contains("/template/"))
            .Select(s => s.Control)
            .ToArray();

        reachingIntoTheTemplate.Should().BeEquivalentTo(MayUseTheTemplateForm);
    }

    [Fact]
    public void TheListBoxItemRingIsOnTheControlBecauseMaterialsTemplateHasNoPresenterToReach()
    {
        // Named explicitly as well as covered by the rule above, because this is the one that was
        // actually broken and the one a future Material upgrade could break again.
        var listBoxItem = Styles().Single(s => s.Control == "ListBoxItem");

        listBoxItem.Suffix.Trim().Should().BeEmpty();
    }

    [Fact]
    public void EveryRingUsesTheThemeAccentRatherThanALiteralColour()
    {
        // A literal survives the theme it was picked against and dies under the other three, and a
        // focus ring that vanishes on one theme reads as a broken tab order rather than as styling
        // (RemEx-2p19).
        foreach (var style in Styles())
        {
            style.Body.Should().Contain("{DynamicResource AccentPrimaryBrush}", $"{style.Control} rings on focus");
            style.Body.Should().MatchRegex(@"BorderThickness""\s+Value=""2""");
        }
    }

    private static IEnumerable<(string Control, string Suffix, string Body)> Styles() =>
        FocusVisibleStyle.Matches(AppMarkup())
            .Select(m => (m.Groups["control"].Value, m.Groups["rest"].Value, m.Groups["body"].Value));
}
