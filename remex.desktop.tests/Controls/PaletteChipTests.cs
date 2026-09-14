using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// <c>PaletteChip</c> (spec 2026-09-13 personalize-tabs §3, RemEx-9ql7v): geometry C (a 62/38
/// column split, primary spanning both rows), the label on Body2TextBlock Bold with the ink coming
/// from <c>ChipInk</c>, no inline FontSize and no literal colour. Source-text, like every other view
/// test in this suite — <c>remex.desktop.tests</c> has no headless render, and this project has no
/// Avalonia.Headless package reference to build one against (checked the .csproj before writing
/// this file: only xunit/Moq/FluentAssertions).
/// </summary>
public class PaletteChipTests
{
    [Fact]
    public void ThreeFillRegions_BindPrimarySecondaryTertiary()
    {
        var markup = ChipMarkup();

        Regex.Matches(markup, @"Background=""\{Binding \$parent\[controls:PaletteChip\]\.Primary\}""").Count.Should().Be(1);
        Regex.Matches(markup, @"Background=""\{Binding \$parent\[controls:PaletteChip\]\.Secondary\}""").Count.Should().Be(1);
        Regex.Matches(markup, @"Background=""\{Binding \$parent\[controls:PaletteChip\]\.Tertiary\}""").Count.Should().Be(1);
    }

    [Fact]
    public void TheColumnSplitIsSixtyTwoThirtyEight_PrimarySpansBothRows()
    {
        var markup = ChipMarkup();

        markup.Should().Contain(@"ColumnDefinitions=""62*,38*""", "geometry C: primary the left 62%, secondary/tertiary the right 38%");
        markup.Should().Contain(@"RowDefinitions=""*,*""");

        var primary = Regex.Match(markup, @"<Border Grid\.Column=""0""[^>]*Grid\.RowSpan=""2""[^>]*Background=""\{Binding \$parent\[controls:PaletteChip\]\.Primary\}""");
        primary.Success.Should().BeTrue("the primary field spans both rows in column 0");

        var secondary = Regex.Match(markup, @"<Border Grid\.Column=""1"" Grid\.Row=""0""[^>]*Background=""\{Binding \$parent\[controls:PaletteChip\]\.Secondary\}""");
        secondary.Success.Should().BeTrue("secondary sits top-right");

        var tertiary = Regex.Match(markup, @"<Border Grid\.Column=""1"" Grid\.Row=""1""[^>]*Background=""\{Binding \$parent\[controls:PaletteChip\]\.Tertiary\}""");
        tertiary.Success.Should().BeTrue("tertiary sits bottom-right");
    }

    [Fact]
    public void TheLabel_IsBoundToLabel_OnBoldBody2TextBlock_WithTwoLineWrap()
    {
        var markup = ChipMarkup();
        var label = Regex.Match(markup, @"<TextBlock[^>]*Text=""\{Binding \$parent\[controls:PaletteChip\]\.Label\}""[^>]*/>");

        label.Success.Should().BeTrue("the label reads the Label styled property");
        label.Value.Should().Contain(@"Theme=""{StaticResource Body2TextBlock}""")
            .And.Contain(@"FontWeight=""Bold""")
            .And.Contain(@"TextWrapping=""Wrap""")
            .And.Contain(@"MaxLines=""2""")
            .And.Contain(@"TextTrimming=""CharacterEllipsis""");
    }

    [Fact]
    public void ChipWidthAndHeight_AreConstantsNotLiterals()
    {
        var markup = ChipMarkup();

        markup.Should().Contain(@"Width=""{x:Static controls:PaletteChip.ChipWidth}""");
        markup.Should().Contain(@"Height=""{x:Static controls:PaletteChip.ChipHeight}""");

        var code = ChipCode();
        code.Should().Contain("public const double ChipWidth = 104;");
        code.Should().Contain("public const double ChipHeight = 60;");
    }

    [Fact]
    public void NoInlineFontSize_AndNoLiteralColour()
    {
        var markup = ChipMarkup();

        markup.Should().NotMatchRegex(@"FontSize=""\d", "no inline FontSize on a chip - Body2TextBlock carries the size");
        markup.Should().NotMatchRegex(@"#[0-9A-Fa-f]{6}", "no literal colour - every colour is a bound brush or a DynamicResource");
        markup.Should().NotContain("Colors.").And.NotContain("Brushes.");
    }

    [Fact]
    public void LabelBrush_IsDerivedThroughChipInk()
    {
        var code = ChipCode();

        code.Should().Contain("ChipInk.For(", "LabelBrush's ink comes from Remex.Core.Theming.ChipInk, not a hand-rolled contrast check");
        code.Should().Contain("using Remex.Core.Theming;");
    }

    [Fact]
    public void SelectionRing_UsesAccentPrimaryBrush_AndNeverChangesTheOuterFootprint()
    {
        var markup = ChipMarkup();

        var selectedStyle = Regex.Match(markup, @"<Style Selector=""[^""]*\.selected[^""]*"">(?<body>[\s\S]*?)</Style>");
        selectedStyle.Success.Should().BeTrue("a style keyed off the .selected class must exist");
        selectedStyle.Groups["body"].Value.Should()
            .MatchRegex(@"Property=""Margin""\s+Value=""0""", "selected: no margin")
            .And.MatchRegex(@"Property=""BorderThickness""\s+Value=""2""", "selected: 2px ring")
            .And.MatchRegex(@"Property=""BorderBrush""\s+Value=""\{DynamicResource AccentPrimaryBrush\}""", "the selected ring is the accent colour");

        var restingStyle = Regex.Match(markup, @"<Style Selector=""Border#PART_OuterBorder"">(?<body>[\s\S]*?)</Style>");
        restingStyle.Success.Should().BeTrue("the resting (unselected) style must exist");
        restingStyle.Groups["body"].Value.Should()
            .MatchRegex(@"Property=""Margin""\s+Value=""1""", "unselected: 1px margin")
            .And.MatchRegex(@"Property=""BorderThickness""\s+Value=""1""", "unselected: 1px border — same total footprint as selected (1+1 == 0+2)");
    }

    [Fact]
    public void AllThreeChipRows_InstantiatePaletteChip()
    {
        var panel = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

        Regex.Matches(panel, @"<controls:PaletteChip\b").Count.Should().Be(3,
            "the scheme-variant row, the built-in preset row and the saved-palette row each instantiate PaletteChip once per item template");

        var variantTemplate = Regex.Match(panel, @"x:DataType=""vm:SchemeVariantStripViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
        var presetTemplate = Regex.Match(panel, @"x:DataType=""vm:SeedPresetTileViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
        var savedTemplate = Regex.Match(panel, @"x:DataType=""vm:SavedPaletteTileViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
        variantTemplate.Success.Should().BeTrue();
        presetTemplate.Success.Should().BeTrue();
        savedTemplate.Success.Should().BeTrue();

        foreach (var template in new[] { variantTemplate, presetTemplate, savedTemplate })
        {
            template.Groups["body"].Value.Should().NotMatchRegex(@"<Border Width=""14"" Height=""14"" CornerRadius=""7""",
                "the old dot swatches are gone from every converted row");
            template.Groups["body"].Value.Should().Contain("<controls:PaletteChip");
        }
    }

    private static string ChipMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "PaletteChip.axaml"));

    private static string ChipCode() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "PaletteChip.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
