using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Audit RemEx-4kv0g.5.4: <c>VirtualCursorPad</c> registered eight brushes with hard-coded violet
/// ARGB defaults plus a fixed grid pen, and <c>RemoteDesktopView</c>'s style only themed three of
/// them (<c>PadBorderBrush</c>, <c>PadOuterGlowBrush</c>, <c>PadClickLabelBrush</c>). Source-level,
/// matching <see cref="SparklineControlStyleTests"/>'s pattern: the failure mode is silent — a
/// missing or mistyped Setter still compiles and the control just keeps rendering its hard-coded
/// default, so this has to read the actual Style and StyledProperty declarations rather than trust
/// that adding a property implies it got styled.
/// </summary>
public class VirtualCursorPadBrushTests
{
    private const string AllNineBrushProperties =
        "PadBackgroundBrush|PadHoveredBrush|PadCenterBrush|PadCenterPressedBrush|PadBorderBrush|"
        + "PadOuterGlowBrush|PadArrowBrush|PadClickLabelBrush|PadGridBrush";

    [Theory]
    [InlineData("PadBackgroundBrush", "CardBackgroundBrush")]
    [InlineData("PadHoveredBrush", "CardBackgroundHoverBrush")]
    [InlineData("PadCenterBrush", "PalettePrimaryContainerBrush")]
    [InlineData("PadCenterPressedBrush", "AccentPrimaryBrush")]
    [InlineData("PadArrowBrush", "TextPrimaryBrush")]
    [InlineData("PadGridBrush", "CardBorderBrush")]
    public void TheViewStyleSetsEachOfTheFiveNewlyThemedBrushesPlusTheGridPen(
        string property, string resourceKey)
    {
        var style = StyleFor("controls|VirtualCursorPad");

        style.Should().MatchRegex(
            $@"<Setter\s+Property=""{property}""\s+Value=""\{{DynamicResource {resourceKey}\}}""\s*/>",
            $"{property} has to resolve from the live theme ({resourceKey}), not the control's own "
            + "hard-coded violet ARGB default");
    }

    [Theory]
    [InlineData("PadBorderBrush")]
    [InlineData("PadOuterGlowBrush")]
    [InlineData("PadClickLabelBrush")]
    public void TheThreeAlreadyThemedBrushesStillHaveASetter(string property)
    {
        var style = StyleFor("controls|VirtualCursorPad");

        style.Should().MatchRegex(
            $@"Property=""{property}""",
            $"{property} was already styled before this bead and must not regress");
    }

    [Fact]
    public void AllNineBrushPropertiesAreAccountedForInTheStyle()
    {
        var style = StyleFor("controls|VirtualCursorPad");
        var expected = AllNineBrushProperties.Split('|');

        foreach (var property in expected)
        {
            style.Should().MatchRegex($@"Property=""{property}""",
                $"the style block must carry a Setter for {property} — all nine brushes, no gaps");
        }
    }

    [Fact]
    public void TheControlRegistersThePadGridBrushStyledProperty()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "VirtualCursorPad.cs"));

        source.Should().Contain("PadGridBrushProperty =",
            "the grid pen (previously a bare Color.FromArgb(60,128,128,255) literal in Render) must "
            + "become a ninth StyledProperty so the control keeps working unstyled and can be themed");

        source.Should().MatchRegex(
            @"PadGridBrushProperty\s*=\s*\n?\s*AvaloniaProperty\.Register<VirtualCursorPad,\s*IBrush>\(nameof\(PadGridBrush\),\s*\n?\s*defaultValue:\s*new SolidColorBrush\(Color\.FromArgb\(60,\s*128,\s*128,\s*255\)\)\)",
            "the eight/nine literal defaults in the .cs stay as defaults — that is the fallback "
            + "contract for an unstyled control");

        source.Should().Contain("var pen = new Pen(PadGridBrush, 1);",
            "Render must draw the divider lines through the new property, not the old inline literal");
    }

    private static string StyleFor(string selector)
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteDesktopView.axaml"));

        var match = Regex.Match(
            view, $@"<Style Selector=""{Regex.Escape(selector)}"">.*?</Style>", RegexOptions.Singleline);

        match.Success.Should().BeTrue($"RemoteDesktopView.axaml has to carry the {selector} rule");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
