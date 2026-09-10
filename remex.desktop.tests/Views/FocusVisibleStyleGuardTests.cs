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
/// that somebody navigating by keyboard cannot see where they are. A ring is the one piece of this
/// app's chrome whose absence nothing reports.
/// </para>
/// <para>
/// The specific shape guarded against is a selector that reaches into a template for a part the
/// template does not have. <c>ListBoxItem:focus-visible /template/ ContentPresenter</c> renders
/// nothing under Material 3.19.0, whose ListBoxItem template has no ContentPresenter at all — the
/// ring has to be set on the control itself instead (RemEx-kgs7g). HomeView supplies its own
/// replacement template too, and the same selector drew its ring around the item's slot rather than
/// the card inside it.
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
    /// The controls whose ring is allowed to reach into the template for a <c>ContentPresenter</c>.
    /// </summary>
    /// <remarks>
    /// Material 3.19.0 — the theme this app applies — templates a ContentPresenter for the other
    /// three content controls, but not for ListBoxItem: its ListBoxItem template has none at all, so
    /// the ring is set on the control itself there instead of reached in through a template part.
    /// The three that remain are not templated anywhere in this repo.
    /// </remarks>
    private static readonly string[] MayUseTheTemplateForm = ["Button", "ToggleButton", "RepeatButton"];

    /// <summary>
    /// Every control the app rings on focus. Losing one is a silent accessibility regression, so the
    /// list is pinned rather than derived.
    /// </summary>
    private static readonly string[] RingedControls =
    [
        "Button", "ToggleButton", "RepeatButton", "ListBoxItem",
        "TextBox", "ComboBox", "CheckBox", "RadioButton", "ToggleSwitch", "Slider",
        "TabItem",
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
    public void TheListBoxItemRingIsOnTheControlBecauseViewsReplaceItsTemplate()
    {
        FocusStyles().Single(s => s.Control == "ListBoxItem").Suffix.Trim().Should().BeEmpty();
    }

    [Fact]
    public void TheTabItemRingIsOnTheControlSoItFramesTheHeaderNotTheHeaderText()
    {
        // RemEx-83zq1. Not because the template form is inert — a ContentPresenter draws its own
        // BorderBrush/BorderThickness, which is why the Button/ToggleButton/RepeatButton
        // template-form selectors work. `/template/ ContentPresenter` matches inside Material
        // 3.19.0's RippleEffect, where the presenter wraps only the header text: probed on a 360x48
        // header it drew the ring at 150,14,59,21, hugging the label — RemEx-kgs7g's "ring around
        // the wrong rectangle". The control-level form reaches Border#PART_RootBorder, which
        // template-binds BorderBrush, BorderThickness and CornerRadius, so the ring frames the
        // header itself.
        FocusStyles().Single(s => s.Control == "TabItem").Suffix.Trim().Should().BeEmpty();
    }

    [Fact]
    public void NoViewReplacesTheTabItemTemplateAndDropsTheBorderTheRingNeeds()
    {
        // The other half of the ListBoxItem lesson, held before it can bite: a control-level ring
        // renders only through a border the effective template binds. Nothing in this repo replaces
        // the TabItem template today, so Material's PART_RootBorder is what the ring reaches. If a
        // view starts supplying one, it inherits the obligation — make this assert the two
        // TemplateBindings the way the ListBoxItem test does rather than deleting it.
        TemplateOverrides("TabItem").Should().BeEmpty();
    }

    [Fact]
    public void TheFocusVisibleElevationStepsAreNotCountedAsRings()
    {
        // RemEx-alwfa.5. A ShadowAssist-only :focus-visible style is a depth step on the .card
        // elevation ramp, not a focus ring - FocusStyles() must not count it or
        // EveryInteractiveControlStillHasAFocusRing would fail every time a card button picks up
        // its keyboard elevation step. This is the positive half of that exclusion: prove the
        // excluded style still exists and still says what it should, so the skip in FocusStyles()
        // can't quietly start hiding a real ring instead.
        var elevationStyles = ElevationFocusStyles().ToArray();

        elevationStyles.Should().NotBeEmpty(
            "a ShadowAssist-only :focus-visible elevation step should exist for card buttons");

        foreach (var style in elevationStyles)
        {
            style.Control.Should().Contain(".card.interactive",
                "the elevation focus-visible step targets card buttons, not every focusable control");
        }

        // Read the pointerover depth from App.axaml rather than hardcoding "Depth2" twice, so the
        // two rules can't silently drift apart.
        var pointeroverDepth = Elements(DesktopPath("App.axaml"), "Style")
            .Where(s => s.Attribute("Selector")?.Value == ":is(Button).card.interactive:pointerover")
            .SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "Setter"))
            .Where(e => e.Attribute("Property")?.Value == "assists:ShadowAssist.ShadowDepth")
            .Select(e => e.Attribute("Value")?.Value)
            // Two styles share this selector in App.axaml; Avalonia applies the later one, so the
            // effective depth is the LAST setter, not the first.
            .LastOrDefault();

        pointeroverDepth.Should().NotBeNullOrEmpty(
            "the pointerover rule must set a ShadowAssist depth for the focus-visible step to match");

        foreach (var style in elevationStyles)
        {
            style.Setters.Should().Contain(
                s => s.Property == "assists:ShadowAssist.ShadowDepth" && s.Value == pointeroverDepth,
                $"{style.Control} focus-visible elevation should lift to the same depth as pointerover");
        }
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
        // border the template binds; Material's ListBoxItem template binds all three, but a view
        // that supplies its own inherits the obligation. HomeView's pinned-sensor list is the case in
        // point - its template was a bare ContentPresenter, so the control-level ring reached
        // nothing there and only there.
        var overrides = TemplateOverrides("ListBoxItem").ToArray();

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
    /// Every <c>:focus-visible</c> style in <c>App.axaml</c> that is a focus RING - i.e. not a
    /// ShadowAssist-only elevation step (RemEx-alwfa.5). See <see cref="ElevationFocusStyles"/> for
    /// the excluded half.
    /// </summary>
    /// <remarks>
    /// Parsed as XML rather than matched with a regex, so a style that has been commented out stops
    /// counting. App.axaml runs to twenty lines of comment for every four of markup around here, and
    /// "commented it out while debugging a layout shift and forgot to put it back" is the likeliest
    /// way one of these disappears.
    /// </remarks>
    private static IEnumerable<FocusStyle> FocusStyles() => AllFocusVisibleStyles().Where(s => !IsElevationStep(s));

    /// <summary>
    /// The <c>:focus-visible</c> styles excluded from <see cref="FocusStyles"/> because every setter
    /// they carry targets <c>assists:ShadowAssist.*</c> - a keyboard step on an elevation ramp
    /// (RemEx-alwfa.5's <c>:is(Button).card.interactive:focus-visible</c>), not a ring drawn with
    /// <c>BorderBrush</c>/<c>BorderThickness</c>.
    /// </summary>
    private static IEnumerable<FocusStyle> ElevationFocusStyles() => AllFocusVisibleStyles().Where(IsElevationStep);

    private static bool IsElevationStep(FocusStyle style) =>
        style.Setters.Count > 0 &&
        style.Setters.All(s => s.Property?.StartsWith("assists:ShadowAssist.", StringComparison.Ordinal) == true);

    private static IEnumerable<FocusStyle> AllFocusVisibleStyles()
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
    /// The root element of every <c>Template</c> setter for <paramref name="control"/> anywhere in
    /// the desktop project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scanned across the whole project rather than just <c>Views/</c>, and across
    /// <c>ControlTheme</c> as well as <c>Style</c>. A ListBoxItem template can arrive from
    /// <c>Themes/</c> or <c>Controls/</c> just as easily, and a
    /// <c>ControlTheme TargetType="ListBoxItem"</c> carries one the same way a selector does — that
    /// second form is also what an <c>ItemContainerTheme</c> resolves to, because the property
    /// element is named <c>ListBox.ItemContainerTheme</c> and the theme inside it is a
    /// <c>ControlTheme</c>. A guard that only looked where the current instance happens to live
    /// would pass by never opening the file that broke.
    /// </para>
    /// <para>
    /// One shape this still cannot see: a <c>Template</c> setter whose value is a
    /// <c>{StaticResource}</c> pointing at a ControlTemplate in a resource dictionary. There is none
    /// today, and following the indirection is more machinery than the risk warrants.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string File, XElement Root)> TemplateOverrides(string control)
    {
        var project = DesktopPath();

        foreach (var markup in Directory.EnumerateFiles(project, "*.axaml", SearchOption.AllDirectories))
        {
            // Leading "/" so the two checks also catch obj/ and bin/ at the project root, which is
            // exactly where they sit and exactly what a bare "/obj/" substring test misses.
            var relative = Path.GetRelativePath(project, markup).Replace('\\', '/');
            if (("/" + relative).Contains("/obj/", StringComparison.Ordinal)
                || ("/" + relative).Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var owner in XDocument.Load(markup).Descendants()
                         .Where(e => e.Name.LocalName is "Style" or "ControlTheme")
                         .Where(owner => Targets(owner, control)))
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

    /// <summary>Whether a style or theme element applies to <paramref name="control"/>.</summary>
    private static bool Targets(XElement owner, string control) =>
        (owner.Attribute("Selector")?.Value ?? owner.Attribute("TargetType")?.Value ?? string.Empty)
            .Contains(control, StringComparison.Ordinal);

    private static IEnumerable<XElement> Elements(string path, string localName) =>
        XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == localName);

    private sealed record FocusStyle(string Control, string Suffix, IReadOnlyList<StyleSetter> Setters);

    private sealed record StyleSetter(string? Property, string? Value);
}
