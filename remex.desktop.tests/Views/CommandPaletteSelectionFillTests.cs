using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins the command palette's hover and selected fills to a form that renders under Material's
/// ListBoxItem template (RemEx-3ule6).
/// </summary>
/// <remarks>
/// <para>
/// THE BUG. RemEx-f51fa correctly aimed both fills at <c>/template/ ContentPresenter</c>, because
/// Fluent 12.1.1 set its own :pointerover and :selected Backgrounds on PART_ContentPresenter and
/// only a part-level app style outranked that. RemEx-prkot then handed every control template to
/// Material 3.19.0, whose ListBoxItem is
/// <c>Border#PART_RootBorder &gt; Panel#PART_RootPanel &gt; { Border#PART_BehaviourEffect,
/// RippleEffect#PART_Ripple, Border#PART_HoverEffect }</c> — no ContentPresenter part anywhere. Both
/// selectors silently matched nothing.
/// </para>
/// <para>
/// WHY THAT IS WORSE THAN "NO HIGHLIGHT". The RemEx-o9gd recolours further down the same file are
/// keyed on <c>ListBoxItem:selected</c>, not on the part, so they kept firing: the labels went to
/// AccentForegroundBrush over a row that was no longer accent-filled. AccentForegroundBrush is
/// chosen to read ON the accent, so the result is the unreadable selected row o9gd was raised to
/// fix, reintroduced through the fill rather than through the text.
/// </para>
/// <para>
/// WHAT THE FIX IS. Material template-binds Background to PART_RootBorder and sets no competing
/// Background on any part, so a CONTROL-level setter renders — the same shape ShellView's nav rail
/// ships (<c>ListBoxItem.nav-item:selected</c>) and the same one FocusVisibleStyleGuardTests pins as
/// an invariant for ListBoxItem under Material (RemEx-kgs7g). That is what is guarded here, in
/// preference to a part name: a control-level fill cannot be broken by a template that renames its
/// root.
/// </para>
/// <para>
/// ASSERTED ON THE SOURCE. This suite has no Avalonia.Headless reference and no render, so nothing
/// here can apply a template and read back a brush (see <c>CommandPaletteLightDismissTests</c> for
/// the same constraint). What is guarded is the shape that made the fill disappear.
/// </para>
/// </remarks>
public class CommandPaletteSelectionFillTests
{
    /// <summary>
    /// The Material.Avalonia release whose <c>Resources/Themes/ListBoxItem.axaml</c> the part names
    /// below were read against, by hand, for RemEx-3ule6.
    /// </summary>
    private const string VerifiedMaterialVersion = "3.19.0";

    [Fact]
    public void BothFillsAreControlLevelSettersThatMaterialTemplateBindsToItsRootBorder()
    {
        var palette = PaletteWindow();

        FillTokenFor(palette, "pointerover").Should().Be("CardBackgroundHoverBrush");
        FillTokenFor(palette, "selected").Should().Be("AccentPrimaryBrush");
    }

    [Fact]
    public void NoFillSelectorNamesAContentPresenter()
    {
        // The exact regression: Material's ListBoxItem has no ContentPresenter part, so a selector
        // naming one is not "harmlessly redundant" — it is a fill that renders nothing while every
        // :selected text rule beside it still fires.
        PaletteWindow().Should().NotMatchRegex(@"Selector=""ListBoxItem[^""]*/template/\s*ContentPresenter""",
            "Fluent is gone (App.axaml, RemEx-prkot); a ContentPresenter part selector matches "
            + "nothing under Material and leaves the o9gd recolours sitting on an unpainted row");
    }

    [Fact]
    public void NoFillIsPaintedIntoTheTemplateAtAll()
    {
        // Two failure modes at once. PART_HoverEffect is the LAST child of PART_RootPanel — it
        // composites OVER the content and is only ever a translucent veil, so an opaque Background
        // there hides the label instead of backing it. And a fill aimed at ANY part is a part name
        // this file now has no reason to carry: the control-level setters above render through
        // whatever Border the template binds.
        foreach (Match style in StylesReachingIntoTheTemplate(PaletteWindow()))
        {
            style.Value.Should().NotContain("Property=\"Background\"",
                "a fill belongs on the ListBoxItem, which Material template-binds to "
                + "PART_RootBorder; the only thing that needs a part here is the veil override");
        }
    }

    [Fact]
    public void TheSelectedRowSuppressesMaterialsOwnVeilExactlyWhereTheContrastIsMeasured()
    {
        // AccentForegroundContrastTests pins AccentForegroundBrush at AA against each theme's RAW
        // accent. Material's :selected style puts MaterialBodyBrush at 0.12 over the row's fill, so
        // leaving it on means the measured guarantee and the pixels disagree.
        //
        // :not(:pointerover) is not decoration (review). Suppressing the veil unconditionally left
        // the selected row pixel-identical hovered and unhovered — the one row that stopped
        // answering the pointer. The AA measurement describes the keyboard path, so that is the
        // state the suppression is scoped to.
        var selectedVeil = Regex.Match(
            PaletteWindow(),
            @"<Style Selector=""ListBoxItem:selected:not\(:pointerover\) /template/ Border#PART_HoverEffect"">.*?</Style>",
            RegexOptions.Singleline);

        selectedVeil.Success.Should().BeTrue(
            "without this override Material's 0.12 MaterialBodyBrush still composites over the "
            + "accent; without the :not(:pointerover) the selected row gives no hover feedback");
        selectedVeil.Value.Should().MatchRegex(@"Property=""Opacity"" Value=""0(\.0)?""");
    }

    [Fact]
    public void TheVeilOverrideIsTiedToTheMaterialVersionItsPartNameWasReadAgainst()
    {
        // CHANGE THIS TEST ON PURPOSE, the same way MaterialPackagePinTests asks to be changed.
        // Everything above is a regex over source text; nothing in this project can apply a
        // template and prove PART_HoverEffect still exists (Material.Styles.dll ships the XAML
        // compiled away). So if Material renames or restructures the ListBoxItem template, the
        // veil override would silently stop matching and the selected row would go back to wearing
        // a 0.12 body-coloured scrim over an accent whose contrast is pinned without one — this
        // exact bead, second occurrence, and green tests throughout.
        //
        // The control-level fills above are immune to that, which is why only this one assertion
        // is version-locked rather than the whole suite.
        PinnedVersionOf("Material.Avalonia").Should().Be(VerifiedMaterialVersion,
            $"the PART_HoverEffect override was hand-verified against Material.Avalonia "
            + $"{VerifiedMaterialVersion}'s Resources/Themes/ListBoxItem.axaml. Re-read that file "
            + "for the new version, confirm the selected state layer is still a Border named "
            + "PART_HoverEffect, then update VerifiedMaterialVersion");
    }

    /// <summary>
    /// Returns the DynamicResource key the fill for <paramref name="state"/> is set to, asserting
    /// along the way that it is a control-level setter rather than a part-level one.
    /// </summary>
    private static string FillTokenFor(string palette, string state)
    {
        var style = Regex.Match(
            palette,
            $@"<Style Selector=""ListBoxItem:{state}"">.*?</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue(
            $"the :{state} fill belongs on the ListBoxItem itself — Material template-binds "
            + "Background to PART_RootBorder, the outermost and bottom-most part of its template, "
            + "and sets no competing Background on any part (ShellView's nav rail ships the same "
            + "shape, and FocusVisibleStyleGuardTests pins it as the invariant)");

        var setter = Regex.Match(
            style.Value,
            @"Property=""Background"" Value=""\{DynamicResource (?<key>\w+)\}""");

        setter.Success.Should().BeTrue(
            $"the :{state} fill must come from a theme token, never a hardcoded colour — all four PC "
            + "themes plus the seed-driven Material palette share this window");

        return setter.Groups["key"].Value;
    }

    private static MatchCollection StylesReachingIntoTheTemplate(string palette)
        => Regex.Matches(palette, @"<Style Selector=""[^""]*/template/[^""]*"">.*?</Style>",
            RegexOptions.Singleline);

    /// <summary>Reads a centrally managed package version, ignoring comment nodes.</summary>
    /// <remarks>
    /// Parsed rather than regexed for the reason <c>MaterialPackagePinTests</c> documents: this repo
    /// quotes elements inside comments as house style, and a raw text match cannot tell a stale
    /// commented entry from the live one.
    /// </remarks>
    private static string PinnedVersionOf(string package, [CallerFilePath] string thisSourceFile = "")
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(thisSourceFile), "Directory.Packages.props"));

        var entry = props.Descendants("PackageVersion")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == package);

        entry.Should().NotBeNull($"{package} must be pinned in Directory.Packages.props");

        return (string?)entry!.Attribute("Version") ?? string.Empty;
    }

    private static string PaletteWindow([CallerFilePath] string thisSourceFile = "")
        => File.ReadAllText(Path.Combine(
            RepoRoot(thisSourceFile), "remex.desktop", "Views", "CommandPaletteWindow.axaml"));

    private static string RepoRoot(string thisSourceFile)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
