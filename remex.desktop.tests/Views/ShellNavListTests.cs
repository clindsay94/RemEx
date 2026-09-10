using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the nav rail's move from nine hand-rolled <c>Button</c>s to Material
/// <c>ListBoxItem</c>s inside one <c>ListBox</c> (RemEx-zi3ua).
/// </summary>
/// <remarks>
/// Everything here is a markup/source scan — there is no headless Avalonia harness in this repo
/// (see AGENTS.md and <c>ButtonVocabularyTests</c>/<c>MaterialIconAdoptionTests</c>, which do the
/// same). These tests prove the XAML SHAPE the bead's acceptance criteria depend on, not the
/// runtime click/keyboard behaviour itself. Named for what they actually check, not for the
/// acceptance criterion they support.
///
/// Parsed with <see cref="XDocument"/> (the pattern <c>FocusVisibleStyleGuardTests</c> already
/// uses), not independent <c>Should().Contain()</c> substring checks against the whole file. The
/// first draft of this file did the latter and it is exactly the shape RemEx-hev1g/RemEx-thwlr
/// warn about: three unrelated substrings anywhere in the document, never asserted to sit on the
/// SAME element, so changing <c>&lt;ListBoxItem Tag="7"</c> to <c>&lt;Button Tag="7"</c> left every
/// assertion green. Per-element attribute checks below close that gap.
/// </remarks>
public class ShellNavListTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private static string ShellViewPath()
        => Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml");

    private static string ShellViewCodeBehindPath()
        => Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml.cs");

    private static string ShellViewXaml() => File.ReadAllText(ShellViewPath());

    /// <summary>The single ListBox the nine destinations and the divider live inside.</summary>
    private static XElement NavList()
    {
        var listBox = XDocument.Load(ShellViewPath())
            .Descendants(XName.Get("ListBox", Avalonia))
            .SingleOrDefault(e => e.Attribute("Name")?.Value == "NavList");

        listBox.Should().NotBeNull(
            "the nav destinations have to live in exactly one ListBox named NavList - two lists " +
            "split arrow-key travel into two scopes and made Settings/About unreachable by keyboard");

        return listBox!;
    }

    /// <summary>The nine destination items — everything in NavList wearing .nav-item.</summary>
    private static IEnumerable<XElement> RealNavItems() =>
        NavList().Descendants(XName.Get("ListBoxItem", Avalonia))
            .Where(e => (e.Attribute("Classes")?.Value ?? string.Empty).Split(' ').Contains("nav-item"));

    /// <summary>The one divider item — everything in NavList wearing .nav-divider.</summary>
    private static XElement Divider() =>
        NavList().Descendants(XName.Get("ListBoxItem", Avalonia))
            .Single(e => (e.Attribute("Classes")?.Value ?? string.Empty).Split(' ').Contains("nav-divider"));

    /// <summary>
    /// The hand-rolled selection state the bead names explicitly: nine
    /// <c>Classes.nav-item-active</c> bindings, each re-deriving "am I the active one" from
    /// <c>ActiveNavIndex</c> by hand. If this string is back in the file, the ListBox migration was
    /// reverted or bypassed.
    /// </summary>
    [Fact]
    public void ShellView_NoLongerTogglesAClassForTheActiveDestination()
    {
        // The attribute usage, not the bare name — this file's own doc comments discuss the
        // retired pattern by name, and a substring match on the name alone would fail on prose
        // rather than on code that revived it.
        ShellViewXaml().Should().NotContain("Classes.nav-item-active=\"",
            "selection state belongs to the ListBoxItem now (IsSelected), not a hand-toggled class");
    }

    /// <summary>
    /// Also NOT wired to SelectionChanged, per the HIGH finding this bead's review round fixed:
    /// Avalonia moves selection on arrow keys, so a SelectionChanged handler cannot tell an
    /// arrow-key highlight move from a genuine activation.
    /// </summary>
    [Fact]
    public void ShellView_DoesNotDriveNavigationFromSelectionChanged()
    {
        ShellViewXaml().Should().NotContain("SelectionChanged=",
            "SelectionChanged fires identically for an arrow-key highlight move and a real " +
            "activation - navigation has to come from Tapped/KeyDown instead");
    }

    [Fact]
    public void NavList_IsASingleSelectionListBox()
    {
        // Parenthesized, not a bare ?. chain: item.Attribute(x)?.Value.Should() short-circuits the
        // WHOLE tail (including .Should().Be(...)) to a no-op when the attribute is missing, which
        // is precisely the "asserted nothing at all" shape FocusVisibleStyleGuardTests' own remarks
        // warn about - measured here, not assumed, by a defect injection that stayed green until
        // every occurrence in this file was rewritten this way.
        (NavList().Attribute("SelectionMode")?.Value).Should().Be("Single",
            "more than one destination able to read as selected would make the active one ambiguous");
    }

    [Fact]
    public void ExactlyNineRealDestinationsExist()
    {
        RealNavItems().Should().HaveCount(9,
            "Home, Sensors, Commands, Launcher, Processes, Files, Logs, Settings, About");
    }

    /// <summary>
    /// Each destination has to survive as a <c>ListBoxItem</c> — carrying the <c>Tag</c>
    /// <c>ActivateNavItem</c> reads to find its command, the accessible name the bead's acceptance
    /// criteria required kept, a one-way <c>IsSelected</c> binding against
    /// <c>ActiveNavIndex</c>, and the two activation handlers — ALL ON THE SAME ELEMENT, which is
    /// what an XDocument parse checks and an independent-substring scan cannot.
    /// </summary>
    [Theory]
    [InlineData("Nav_Home", 0)]
    [InlineData("Nav_Sensors", 1)]
    [InlineData("Nav_Commands", 2)]
    [InlineData("Nav_Launcher", 3)]
    [InlineData("Nav_Processes", 4)]
    [InlineData("Nav_Files", 7)]
    [InlineData("Shell_LogsDiagnostics", 8)]
    [InlineData("Nav_Settings", 9)]
    [InlineData("Shell_About", 6)]
    public void EveryDestination_CarriesItsNameSelectionBindingAndActivationHandlersOnOneElement(
        string localizationKey, int navIndex)
    {
        var item = RealNavItems().SingleOrDefault(e => e.Attribute("Tag")?.Value == navIndex.ToString());
        item.Should().NotBeNull($"index {navIndex} has to exist as exactly one real ListBoxItem");

        // Every check below is parenthesized as (item.Attribute(x)?.Value).Should() rather than
        // item.Attribute(x)?.Value.Should() - the latter's ?. short-circuits .Should().Be(...) to a
        // no-op along with .Value when the attribute is absent, so a MISSING attribute would pass
        // silently instead of failing loudly. Confirmed by defect injection: removing KeyDown from
        // one item kept the unparenthesized form green.
        (item!.Attribute("AutomationProperties.Name")?.Value).Should().Be(
            $"{{conv:Localize {localizationKey}}}",
            $"{localizationKey}'s accessible name must survive on the SAME element as Tag={navIndex}");

        (item.Attribute("IsSelected")?.Value).Should().Be(
            "{Binding ActiveNavIndex, Converter={x:Static ObjectConverters.Equal}, " +
            $"ConverterParameter={navIndex}, Mode=OneWay}}",
            $"index {navIndex}'s active state has to be a one-way ListBoxItem selection binding");

        (item.Attribute("Tapped")?.Value).Should().Be("OnNavItemTapped",
            "a click has to commit navigation directly, not wait on a SelectionChanged echo");

        (item.Attribute("KeyDown")?.Value).Should().Be("OnNavItemKeyDown",
            "Enter/Space is the keyboard commit gesture; arrow keys are left to move the highlight");
    }

    [Fact]
    public void TheDivider_IsNotSelectableFocusableOrHitTestable()
    {
        var divider = Divider();

        (divider.Attribute("Focusable")?.Value).Should().Be("False",
            "Focusable=\"False\" is what ItemsControl.GetNextControl uses to skip this item during " +
            "arrow-key traversal (confirmed against Avalonia 12.1.1's own source)");

        (divider.Attribute("IsHitTestVisible")?.Value).Should().Be("False",
            "a pointer click must not be able to land on the divider at all");

        divider.Attribute("Tag").Should().BeNull(
            "the divider is not a destination and must never reach ActivateNavItem's switch");

        // Focusable/IsHitTestVisible keep it out of traversal and hit testing, but Avalonia still
        // builds an automation peer for every ListBoxItem regardless of either - without this a
        // screen reader announces a 10-item list with one unnamed, unreachable entry (review
        // round 2, LOW).
        (divider.Attribute("AutomationProperties.AccessibilityView")?.Value).Should().Be("Raw",
            "the divider must not surface in the accessibility tree at all - the old sibling " +
            "Separator was never a list item and so was never announced as one");
    }

    [Fact]
    public void TheDividerStyle_ZeroesTheBaseThemesDefaultPadding()
    {
        ShellViewXaml().Should().Contain("Selector=\"ListBoxItem.nav-divider\"",
            "without this the base Material ListBoxItem theme's own 8px Padding default shifts " +
            "the divider line away from where the old sibling Separator sat");
    }

    /// <summary>
    /// THE STRUCTURAL GUARD for the MEDIUM finding this review round raised: a Tag with no
    /// matching <c>case</c> in <c>ActivateNavItem</c> falls through silently in a Release build
    /// (<c>Debug.Fail</c> compiles out there), so nothing at runtime would catch a typo'd, missing,
    /// or orphaned Tag. This test can.
    /// </summary>
    [Fact]
    public void TheTagSetInMarkupMatchesTheCaseSetInActivateNavItem()
    {
        var tagValues = RealNavItems()
            .Select(e => int.Parse(e.Attribute("Tag")!.Value))
            .OrderBy(i => i)
            .ToArray();

        tagValues.Should().NotBeEmpty("an empty set would make this test pass vacuously");

        var codeBehind = File.ReadAllText(ShellViewCodeBehindPath());
        var caseValues = Regex.Matches(codeBehind, @"case (\d+): vm\.NavigateTo\w+Command\.Execute\(null\); break;")
            .Select(m => int.Parse(m.Groups[1].Value))
            .OrderBy(i => i)
            .ToArray();

        caseValues.Should().BeEquivalentTo(tagValues,
            "every Tag in NavList has to have exactly one matching case in ActivateNavItem's switch, " +
            "and vice versa - an orphaned case is dead code, an unmatched Tag is a silent dead click");
    }

    /// <summary>
    /// THE STRUCTURAL GUARD for the MEDIUM finding review round 2 raised: the nav list's
    /// <c>:selected</c> highlight now does two jobs (keyboard cursor AND "you are here"), arrow
    /// keys move only the first, and nothing else in this file resyncs the two - so a user who
    /// arrows to a different item and leaves without committing strands the highlight on the wrong
    /// destination, permanently (<c>ActiveNavIndex</c>'s equality-gated setter means even
    /// navigating back to the truly-active page raises no <c>PropertyChanged</c> to re-push the
    /// one-way <c>IsSelected</c> binding).
    /// </summary>
    /// <remarks>
    /// A source scan, not a behavioural test - there is no headless Avalonia harness in this repo,
    /// so nothing here can actually open the drawer, press Down, and look. What this proves is that
    /// the resync hook exists and is wired to the right property; it cannot prove
    /// <c>ResyncNavListSelection</c> resolves the correct container at runtime.
    /// </remarks>
    [Fact]
    public void IsDrawerOpenChanges_ResyncNavListSelectionFromActiveNavIndex()
    {
        var codeBehind = File.ReadAllText(ShellViewCodeBehindPath());

        codeBehind.Should().Contain("nameof(ShellViewModel.IsDrawerOpen)",
            "OnViewModelPropertyChanged has to react to IsDrawerOpen - opening or closing the " +
            "drawer is the resync point, since that is when a stale highlight becomes visible " +
            "again or gets left behind uncommitted");

        codeBehind.Should().Contain("private void ResyncNavListSelection()",
            "the resync method itself has to exist");

        codeBehind.Should().Contain("_navList.SelectedItem = target;",
            "the resync has to move NavList's actual SelectedItem, not just read ActiveNavIndex - " +
            "reading it without writing back to the control would leave the drifted highlight in " +
            "place");

        // Loose position check rather than parsing the method body: the IsDrawerOpen branch has to
        // actually CALL the resync method, not merely exist alongside an unrelated call to it
        // elsewhere in the file (e.g. only from OnLoaded, which would miss every later toggle).
        var drawerBranchIndex = codeBehind.IndexOf("nameof(ShellViewModel.IsDrawerOpen)", StringComparison.Ordinal);
        var nextResyncCallIndex = codeBehind.IndexOf("ResyncNavListSelection();", drawerBranchIndex, StringComparison.Ordinal);

        nextResyncCallIndex.Should().BeGreaterThan(-1,
            "the IsDrawerOpen branch has to call ResyncNavListSelection(), not just check the " +
            "property name without acting on it");
        (nextResyncCallIndex - drawerBranchIndex).Should().BeLessThan(400,
            "the call has to be the one inside the IsDrawerOpen branch, not some unrelated later " +
            "call the string search happened to find next");
    }

    /// <summary>
    /// Ripple and the hover state layer are Material's <c>ListBoxItem</c> control theme's own
    /// (confirmed against Material.Avalonia 3.19.0's own template source, not merely the DLL — see
    /// the comment above these styles in ShellView.axaml). What this app still has to supply itself
    /// is the accent-tinted selected fill/foreground and the per-state icon recolour, since the
    /// base theme has no idea what this app's accent colour is.
    /// </summary>
    [Fact]
    public void TheListBoxItemStyles_CoverBaseHoverAndSelectedStates()
    {
        var shell = ShellViewXaml();

        foreach (var selector in new[]
                 {
                     "ListBoxItem.nav-item",
                     "ListBoxItem.nav-item:pointerover",
                     "ListBoxItem.nav-item:selected",
                     "ListBoxItem.nav-item mi|MaterialIcon",
                     "ListBoxItem.nav-item:pointerover mi|MaterialIcon",
                     "ListBoxItem.nav-item:selected mi|MaterialIcon",
                     "ListBoxItem.nav-item /template/ ripple|RippleEffect#PART_Ripple",
                     "ListBoxItem.nav-item /template/ Border#PART_HoverEffect",
                 })
        {
            shell.Should().Contain($"Selector=\"{selector}\"",
                "the nav list's ripple and base hover state layer come from the ListBoxItem control " +
                "theme itself, but the accent fill/foreground per state is this app's own style and " +
                "has to name the selector that theme actually exposes");
        }
    }

    [Fact]
    public void TheNavRipple_IsClippedToTheItemRadiusAndTintedFromThePalette()
    {
        var shell = ShellViewXaml();

        shell.Should().Contain("xmlns:ripple=\"clr-namespace:Material.Ripple;assembly=Material.Ripple\"",
            "the ripple/hover template-part selectors below need the ripple: xmlns in scope");

        var rippleStyle = ExtractStyleBlock(shell,
            "ListBoxItem.nav-item /template/ ripple|RippleEffect#PART_Ripple");
        rippleStyle.Should().Contain("Property=\"CornerRadius\"").And.Contain("CornerRadiusSmall",
            "the ripple must clip to the item's corner radius instead of flashing square");
        rippleStyle.Should().Contain("Property=\"RippleFill\"").And.Contain("TextPrimaryBrush",
            "the ripple should tint from the app palette, not Material's default body colour");

        var hoverStyle = ExtractStyleBlock(shell,
            "ListBoxItem.nav-item /template/ Border#PART_HoverEffect");
        hoverStyle.Should().Contain("Property=\"CornerRadius\"").And.Contain("CornerRadiusSmall");
        hoverStyle.Should().NotContain("Property=\"Opacity\"",
            "Material's own :pointerover/:selected opacities on the hover state layer must keep winning");
        hoverStyle.Should().NotContain("Property=\"Background\"",
            "only the radius is this app's concern here; the fill stays Material's");
    }

    /// <summary>
    /// Pulls the &lt;Style Selector="..."&gt;...&lt;/Style&gt; block for one selector out of the raw
    /// XAML text, so assertions about what setters live INSIDE that block can't accidentally match a
    /// setter that belongs to some other style elsewhere in the file.
    /// </summary>
    private static string ExtractStyleBlock(string xaml, string selector)
    {
        var marker = $"Selector=\"{selector}\"";
        var start = xaml.IndexOf(marker, System.StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected a Style with Selector=\"{selector}\"");
        var end = xaml.IndexOf("</Style>", start, System.StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"expected a closing </Style> after Selector=\"{selector}\"");
        return xaml[start..end];
    }
}
