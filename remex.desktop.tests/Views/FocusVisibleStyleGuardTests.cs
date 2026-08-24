using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
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
/// <b>The invariant these pin is not "control-level selectors are safe".</b> It is that a ring set on
/// the control only renders if the item's effective template binds it — so a view that replaces the
/// template takes on that obligation. <c>HomeView.axaml</c> does replace it, and the first draft of
/// this fix broke the ring there while every test here passed, which is precisely the failure the
/// guard exists to prevent.
/// </para>
/// <para>
/// A source-level pin rather than a rendering test for the reason recorded in RemEx-r8c6: there is no
/// Avalonia headless harness in this assembly, so no test here can focus a control and look at it.
/// </para>
/// </remarks>
public class FocusVisibleStyleGuardTests
{
    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private static string DesktopPath(params string[] parts)
        => Path.Combine(new[] { RepoRoot(), "remex.desktop" }.Concat(parts).ToArray());

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
        FocusStyles().Select(s => s.Control).Should().BeEquivalentTo(RingedControls);
    }

    [Fact]
    public void OnlyTheControlsThatActuallyTemplateAContentPresenterReachIntoTheTemplate()
    {
        var reachingIntoTheTemplate = FocusStyles()
            .Where(s => s.Suffix.Contains("/template/"))
            .Select(s => s.Control);

        reachingIntoTheTemplate.Should().BeEquivalentTo(MayUseTheTemplateForm);
    }

    [Fact]
    public void TheListBoxItemRingIsOnTheControlBecauseMaterialsTemplateHasNoPresenterToReach()
    {
        FocusStyles().Single(s => s.Control == "ListBoxItem").Suffix.Trim().Should().BeEmpty();
    }

    [Fact]
    public void EveryRingUsesTheThemeAccentRatherThanALiteralColour()
    {
        // A literal survives the theme it was picked against and dies under the other three, and a
        // focus ring that vanishes on one theme reads as a broken tab order rather than as styling
        // (RemEx-2p19).
        foreach (var style in FocusStyles())
        {
            style.Setters.Should().Contain(
                s => s.Property == "BorderBrush" && s.Value == "{DynamicResource AccentPrimaryBrush}",
                $"{style.Control} rings on focus");
            style.Setters.Should().Contain(s => s.Property == "BorderThickness" && s.Value == "2");
        }
    }

    [Fact]
    public void AViewThatReplacesTheListBoxItemTemplateStillBindsTheBorderTheRingIsPaintedOn()
    {
        // THE ONE THE FIRST DRAFT GOT WRONG. A ring set on the control renders through whatever
        // border the template binds; Material's PART_RootBorder binds it, but a view that supplies
        // its own template inherits the obligation. HomeView's pinned-sensor list is the case in
        // point - its template was a bare ContentPresenter, so the control-level ring reached
        // nothing there and only there.
        var overrides = ListBoxItemTemplateOverrides().ToArray();

        overrides.Should().NotBeEmpty("HomeView replaces this template - if that stops being true, " +
                                      "delete this test rather than letting it pass vacuously");

        foreach (var (file, root) in overrides)
        {
            // BOTH, and neither optional. A thickness with no brush is a ring that reserves its
            // 2px, shifts the item, and paints nothing — the same invisible failure, one attribute
            // narrower. An earlier draft asserted the brush through `?.`, which asserted nothing at
            // all when the attribute was missing.
            var attributes = root.Attributes().Select(a => a.Name.LocalName).ToArray();

            attributes.Should().Contain(
                ["BorderBrush", "BorderThickness"],
                $"{file} replaces the ListBoxItem template, so its root must carry the focus ring");

            root.Attribute("BorderThickness")!.Value.Should().Be("{TemplateBinding BorderThickness}");
            root.Attribute("BorderBrush")!.Value.Should().Be("{TemplateBinding BorderBrush}");
        }
    }

    /// <summary>
    /// Every <c>:focus-visible</c> style in <c>App.axaml</c>.
    /// </summary>
    /// <remarks>
    /// Parsed as XML rather than matched with a regex, so a style that has been commented out stops
    /// counting. App.axaml runs to twenty lines of comment for every four of markup around here, and
    /// "commented it out while debugging a layout shift and forgot to put it back" is the likeliest
    /// way one of these disappears.
    /// </remarks>
    private static IEnumerable<FocusStyle> FocusStyles()
    {
        foreach (var style in Elements(DesktopPath("App.axaml"), "Style"))
        {
            var selector = style.Attribute("Selector")?.Value;
            var marker = selector?.IndexOf(":focus-visible", StringComparison.Ordinal) ?? -1;
            if (selector is null || marker < 0)
            {
                continue;
            }

            yield return new FocusStyle(
                selector[..marker],
                selector[(marker + ":focus-visible".Length)..],
                style.Elements().Where(e => e.Name.LocalName == "Setter")
                    .Select(e => new StyleSetter(e.Attribute("Property")?.Value, e.Attribute("Value")?.Value))
                    .ToArray());
        }
    }

    /// <summary>
    /// The root element of every ListBoxItem <c>Template</c> setter anywhere in the desktop project.
    /// </summary>
    /// <remarks>
    /// Scanned across the whole project rather than just <c>Views/</c>, and across
    /// <c>ControlTheme</c> and <c>ItemContainerTheme</c> as well as <c>Style</c>. A ListBoxItem
    /// template can arrive from <c>Themes/</c> or <c>Controls/</c> just as easily, and a
    /// <c>ControlTheme TargetType="ListBoxItem"</c> carries one the same way a selector does. A
    /// guard that only looked where the current instance happens to live would pass by never opening
    /// the file that broke.
    /// </remarks>
    private static IEnumerable<(string File, XElement Root)> ListBoxItemTemplateOverrides()
    {
        var project = DesktopPath();

        foreach (var markup in Directory.EnumerateFiles(project, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, markup).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var owner in XDocument.Load(markup).Descendants()
                         .Where(e => e.Name.LocalName is "Style" or "ControlTheme" or "ItemContainerTheme")
                         .Where(TargetsListBoxItem))
            {
                var template = owner.Elements()
                    .Where(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "Template")
                    .SelectMany(e => e.Elements())
                    .FirstOrDefault(e => e.Name.LocalName == "ControlTemplate");

                if (template?.Elements().FirstOrDefault() is { } root)
                {
                    yield return (relative, root);
                }
            }
        }
    }

    /// <summary>Whether a style or theme element applies to ListBoxItem.</summary>
    private static bool TargetsListBoxItem(XElement owner) =>
        (owner.Attribute("Selector")?.Value ?? owner.Attribute("TargetType")?.Value ?? string.Empty)
            .Contains("ListBoxItem", StringComparison.Ordinal)
        || owner.Name.LocalName == "ItemContainerTheme";

    private static IEnumerable<XElement> Elements(string path, string localName) =>
        XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == localName);

    private sealed record FocusStyle(string Control, string Suffix, IReadOnlyList<StyleSetter> Setters);

    private sealed record StyleSetter(string? Property, string? Value);
}
