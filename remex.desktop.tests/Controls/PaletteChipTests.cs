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
    public void ChipMinWidthAndHeight_AreConstantsNotLiterals_RootStretchesRatherThanFixedWidth()
    {
        // Fix round 2 (RemEx-4kv0g.4.4): two chips per row, stretched, instead of a fixed 104px
        // three-per-row box that broke long names mid-word under the monospace body font.
        var markup = ChipMarkup();

        markup.Should().Contain(@"MinWidth=""{x:Static controls:PaletteChip.ChipMinWidth}""");
        markup.Should().Contain(@"Height=""{x:Static controls:PaletteChip.ChipHeight}""");
        markup.Should().NotMatchRegex(@"<Grid[^>]*\sWidth=""\{x:Static controls:PaletteChip\.ChipWidth\}""", "the root no longer takes a fixed Width - it stretches to its host");
        markup.Should().Contain(@"Margin=""8,0""", "label margin shrank from 10 to 8 to give the label more room at the new, narrower two-per-row size");

        var code = ChipCode();
        code.Should().Contain("public const double ChipMinWidth = 150;");
        code.Should().Contain("public const double ChipHeight = 60;");
        code.Should().NotContain("ChipWidth = 104");
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
    public void NoOwnRing_TheHostButtonIsTheOnlySelectionRing()
    {
        var markup = ChipMarkup();

        markup.Should().NotContain("IsSelected", "the chip has no selection concept of its own any more (fix round 1, RemEx-4kv0g.4.1) - the host Button's .tile.selected ring is the only ring");
        markup.Should().NotMatchRegex(@"Selector=""[^""]*\.selected[^""]*""", "no style is keyed off a .selected class in this control any more");
        markup.Should().NotMatchRegex(@"BorderThickness|BorderBrush", "PART_OuterBorder no longer draws any border of its own - fill and label only, clipped to CornerRadius");

        var code = ChipCode();
        code.Should().NotContain("IsSelectedProperty").And.NotContain("public bool IsSelected");
    }

    [Fact]
    public void NoTargetNullValue_LabelFallsBackThroughAStyle()
    {
        var markup = ChipMarkup();

        markup.Should().NotContain("TargetNullValue=", "a DynamicResourceExtension stored in TargetNullValue is never evaluated (review-1-report.md MEDIUM) - the fallback must be a real Style instead");

        var defaultStyle = Regex.Match(markup, @"<Style Selector=""TextBlock#PART_Label"">(?<body>[\s\S]*?)</Style>");
        defaultStyle.Success.Should().BeTrue("a default-foreground style for the label must exist");
        defaultStyle.Groups["body"].Value.Should().MatchRegex(@"Property=""Foreground""\s+Value=""\{DynamicResource TextPrimaryBrush\}""");

        var inkedStyle = Regex.Match(markup, @"<Style Selector=""TextBlock#PART_Label\.inked"">(?<body>[\s\S]*?)</Style>");
        inkedStyle.Success.Should().BeTrue("an .inked style bound to LabelBrush must exist");
        inkedStyle.Groups["body"].Value.Should().MatchRegex(@"Property=""Foreground""\s+Value=""\{Binding \$parent\[controls:PaletteChip\]\.LabelBrush\}""");

        markup.Should().Contain(@"Name=""PART_Label""", "the label needs a name for the two Foreground styles to select on");
    }

    [Fact]
    public void LabelBrush_IsRegisteredAsADirectProperty()
    {
        var code = ChipCode();

        code.Should().MatchRegex(@"DirectProperty<PaletteChip,\s*IBrush\?>\s+LabelBrushProperty", "LabelBrush must be read-only - a DirectProperty, not a StyledProperty callers could SetValue on")
            .And.Contain("RegisterDirect<PaletteChip, IBrush?>(nameof(LabelBrush)")
            .And.MatchRegex(@"private\s+set\s*=>\s*SetAndRaise\(LabelBrushProperty", "the setter must be private");
    }

    [Fact]
    public void AllThreeChipRows_InstantiatePaletteChip()
    {
        // RemEx-4kv0g.4.3: the three rows split across two tabs - the scheme-variant strip moved
        // onto Colour, the preset gallery and saved-palette row onto Palettes.
        var colour = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizeColourTab.axaml"));
        var palettes = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizePalettesTab.axaml"));

        Regex.Matches(colour, @"<controls:PaletteChip\b").Count.Should().Be(1, "the scheme-variant row instantiates PaletteChip once per item template");
        Regex.Matches(palettes, @"<controls:PaletteChip\b").Count.Should().Be(2, "the built-in preset row and the saved-palette row each instantiate PaletteChip once per item template");

        var variantTemplate = Regex.Match(colour, @"x:DataType=""vm:SchemeVariantStripViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
        var presetTemplate = Regex.Match(palettes, @"x:DataType=""vm:SeedPresetTileViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
        var savedTemplate = Regex.Match(palettes, @"x:DataType=""vm:SavedPaletteTileViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
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
