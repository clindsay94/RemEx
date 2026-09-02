using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins the command palette's hover and selected fills to a part Material's ListBoxItem template
/// actually has (RemEx-3ule6).
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
/// ASSERTED ON THE SOURCE. This suite has no Avalonia.Headless reference and no render, so nothing
/// here can apply a template and read back a brush (see <c>CommandPaletteLightDismissTests</c> for
/// the same constraint). The part names below were read off Material.Avalonia 3.19.0's
/// <c>Resources/Themes/ListBoxItem.axaml</c>; the XAML is compiled away in the shipped
/// Material.Styles.dll, so it cannot be re-derived at test time either. What is guarded is the
/// shape that made the fill disappear.
/// </para>
/// </remarks>
public class CommandPaletteSelectionFillTests
{
    [Fact]
    public void BothFillsTargetAPartMaterialsListBoxItemTemplateActuallyHas()
    {
        var palette = PaletteWindow();

        FillSelectorFor(palette, "pointerover").Should().Be("CardBackgroundHoverBrush");
        FillSelectorFor(palette, "selected").Should().Be("AccentPrimaryBrush");
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
    public void TheFillIsNeverPaintedOntoPartHoverEffect()
    {
        // PART_HoverEffect is the LAST child of PART_RootPanel — it composites OVER the content and
        // is only ever a translucent veil. It is the obvious-looking target because it is the part
        // Material varies for these states, which is exactly why this is worth pinning: an opaque
        // Background there hides the label instead of backing it.
        foreach (Match style in StylesTargeting(PaletteWindow(), "PART_HoverEffect"))
        {
            style.Value.Should().NotContain("Property=\"Background\"",
                "PART_HoverEffect draws above the row's content, so a fill on it covers the text");
        }
    }

    [Fact]
    public void TheSelectedRowSuppressesMaterialsOwnVeilOverTheAccent()
    {
        // AccentForegroundContrastTests pins AccentForegroundBrush at AA against each theme's RAW
        // accent. Material's :selected style puts MaterialBodyBrush at 0.12 over PART_RootBorder's
        // fill, so leaving it on means the measured guarantee and the pixels disagree.
        var selectedVeil = Regex.Match(
            PaletteWindow(),
            @"<Style Selector=""ListBoxItem:selected /template/ Border#PART_HoverEffect"">.*?</Style>",
            RegexOptions.Singleline);

        selectedVeil.Success.Should().BeTrue(
            "without this override Material's 0.12 MaterialBodyBrush still composites over the accent");
        selectedVeil.Value.Should().MatchRegex(@"Property=""Opacity"" Value=""0(\.0)?""");
    }

    /// <summary>
    /// Returns the DynamicResource key the fill for <paramref name="state"/> is set to, asserting
    /// along the way that the selector reaches Material's own root Border part.
    /// </summary>
    private static string FillSelectorFor(string palette, string state)
    {
        var style = Regex.Match(
            palette,
            $@"<Style Selector=""ListBoxItem:{state} /template/ Border#PART_RootBorder"">.*?</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue(
            $"the :{state} fill has to name Border#PART_RootBorder — the outermost, bottom-most part "
            + "of Material's ListBoxItem template, and the only one that can back the row's content");

        var setter = Regex.Match(
            style.Value,
            @"Property=""Background"" Value=""\{DynamicResource (?<key>\w+)\}""");

        setter.Success.Should().BeTrue(
            $"the :{state} fill must come from a theme token, never a hardcoded colour — all four PC "
            + "themes plus the seed-driven Material palette share this window");

        return setter.Groups["key"].Value;
    }

    private static MatchCollection StylesTargeting(string palette, string part)
        => Regex.Matches(
            palette,
            $@"<Style Selector=""[^""]*#{Regex.Escape(part)}"">.*?</Style>",
            RegexOptions.Singleline);

    private static string PaletteWindow([CallerFilePath] string thisSourceFile = "")
        => File.ReadAllText(Path.Combine(
            RepoRoot(thisSourceFile), "remex.desktop", "Views", "CommandPaletteWindow.axaml"));

    private static string RepoRoot(string thisSourceFile)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
