using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The three chip rows this drop converts to <c>PaletteChip</c> (spec 2026-09-13 personalize-tabs
/// §3, RemEx-9ql7v): the scheme-variant strip (now on the Colour tab), the built-in preset gallery
/// and the saved-palette row (now on the Palettes tab, RemEx-4kv0g.4.3). Every other detail of each
/// row's own Button - Classes="tile" where it had it, its command, CommandParameter and
/// AutomationProperties.Name - is untouched; only each Button's inner content changed. The
/// saved-palette row keeps its separate rename TextBox and delete Button unmodified; it gets no
/// IsSelected binding on its chip because a saved palette has no "currently selected" concept today
/// (Apply is one-shot, not a toggle) - see SavedPaletteTileViewModel.cs's DisplayName doc comment.
/// Source-text, like <c>PersonalizationSheetLayoutTests</c> and its siblings -
/// <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizationChipRowsTests
{
    [Fact]
    public void TheVariantRow_KeepsItsButtonChrome_AndHostsAPaletteChip()
    {
        var button = VariantButton();

        button.Should().Contain(@"Classes=""tile""")
            .And.Contain(@"Classes.selected=""{Binding IsSelected}""", "the button keeps the single selection ring (fix round 1, RemEx-4kv0g.4.1)")
            .And.Contain("AutomationProperties.Name=\"{Binding DisplayName}\"")
            .And.Contain("Command=\"{Binding $parent[ItemsControl].((vm:CustomizationViewModel)DataContext).SelectSchemeVariantCommand}\"")
            .And.Contain(@"CommandParameter=""{Binding Variant}""")
            .And.Contain("<controls:PaletteChip")
            .And.Contain(@"CornerRadius=""{DynamicResource CardCornerRadius}""", "the variant strip uses the sheet's card radius (spec §3)");

        button.Should().NotMatchRegex(@"<Border Width=""14"" Height=""14"" CornerRadius=""7""", "the old dot swatches are gone");
        button.Should().NotContain("OnSurfaceBrush", "the variant chip's fourth dot is dropped (spec §3)");
        ChipTag(button).Should().NotContain("IsSelected", "the chip itself carries no selection concept any more - only the button's Classes.selected does");
    }

    [Fact]
    public void ThePresetRow_KeepsItsButtonChrome_AndHostsAPaletteChip()
    {
        var button = PresetButton();

        button.Should().Contain(@"Classes=""tile""")
            .And.Contain(@"Classes.selected=""{Binding IsSelected}""", "the button keeps the single selection ring (fix round 1, RemEx-4kv0g.4.1)")
            .And.Contain("AutomationProperties.Name=\"{Binding DisplayName}\"")
            .And.Contain("Command=\"{Binding $parent[ItemsControl].((vm:CustomizationViewModel)DataContext).SelectThemeCommand}\"")
            .And.Contain(@"CommandParameter=""{Binding Id}""")
            .And.Contain("<controls:PaletteChip")
            .And.Contain(@"CornerRadius=""{Binding PreviewCornerRadius}""", "a preset's own corner radius keeps driving its chip, same as it drove the old Border");

        button.Should().NotMatchRegex(@"<Border Width=""14"" Height=""14"" CornerRadius=""7""", "the old dot swatches are gone");
        button.Should().NotMatchRegex(@"FontSize=""10""", "the old inline-size caption is gone with the Border it lived in");
        ChipTag(button).Should().NotContain("IsSelected", "the chip itself carries no selection concept any more - only the button's Classes.selected does");
    }

    [Fact]
    public void TheSavedPaletteRow_KeepsItsApplyButtonAndRenameBox_AndHostsAPaletteChip()
    {
        var markup = PalettesMarkup();
        var template = Regex.Match(markup, @"x:DataType=""vm:SavedPaletteTileViewModel"">(?<body>[\s\S]*?)</DataTemplate>");
        template.Success.Should().BeTrue();
        var body = template.Groups["body"].Value;

        var button = ButtonInBody(body);
        button.Should().Contain(@"Classes=""tile""")
            .And.Contain("AutomationProperties.Name=\"{Binding Name}\"", "the apply button keeps announcing the live editable Name, not the DisplayName alias")
            .And.Contain("Command=\"{Binding $parent[ItemsControl].((vm:CustomizationViewModel)DataContext).ApplySavedPaletteCommand}\"")
            .And.Contain("<controls:PaletteChip")
            .And.Contain(@"Label=""{Binding DisplayName}""")
            .And.Contain(@"CornerRadius=""{DynamicResource CardCornerRadius}""");
        button.Should().NotContain("IsSelected", "a saved palette has no selected concept today - the chip stays in its resting state");
        button.Should().NotMatchRegex(@"<Border Width=""14"" Height=""14"" CornerRadius=""7""", "the old dot swatches are gone");

        // The rename box and delete button are untouched.
        body.Should().Contain(@"Text=""{Binding Name, Mode=TwoWay}""", "the rename TextBox still edits Name directly");
        body.Should().Contain("DeleteSavedPaletteCommand");
    }

    [Fact]
    public void AllThreeItemsControls_KeepTheirOwnItemsSource()
    {
        ColourMarkup().Should().Contain(@"ItemsSource=""{Binding SchemeVariantStrips}""");
        PalettesMarkup().Should().Contain(@"ItemsSource=""{Binding ThemePresets}""")
            .And.Contain(@"ItemsSource=""{Binding SavedPalettes}""");
    }

    [Fact]
    public void AllThreeChipRows_UseTwoColumnUniformGrid_NoWrapPanel_NoFixedTileWidth()
    {
        // Fix round 2 (RemEx-4kv0g.4.4): three-per-row chips broke long names mid-word under the
        // monospace body font; Connor chose two chips per row, each stretched to half the row.
        // Scoped to just the three chip-row templates - PersonalizeColourTab.axaml and
        // PersonalizePalettesTab.axaml each still legitimately use WrapPanel elsewhere (tonal-ramp
        // rows, the share-palette button row), so a whole-file NotContain would false-positive.
        var variantTemplate = TemplateBody(ColourMarkup(), "vm:SchemeVariantStripViewModel");
        var presetTemplate = TemplateBody(PalettesMarkup(), "vm:SeedPresetTileViewModel");
        var savedTemplate = TemplateBody(PalettesMarkup(), "vm:SavedPaletteTileViewModel");

        foreach (var (name, template) in new[] { ("variant", variantTemplate), ("preset", presetTemplate), ("saved", savedTemplate) })
        {
            template.Should().NotContain("MinWidth=\"{x:Static controls:PaletteChip.ChipWidth}\"", $"the {name} tile button no longer takes a fixed ChipWidth");
        }

        var colourPanel = Regex.Match(ColourMarkup(), @"ItemsSource=""\{Binding SchemeVariantStrips\}""[\s\S]*?ItemsControl\.ItemsPanel>(?<panel>[\s\S]*?)</ItemsControl\.ItemsPanel>");
        colourPanel.Success.Should().BeTrue();
        colourPanel.Groups["panel"].Value.Should().Contain(@"<UniformGrid Columns=""2""").And.NotContain("WrapPanel");

        var presetPanel = Regex.Match(PalettesMarkup(), @"ItemsSource=""\{Binding ThemePresets\}""[\s\S]*?ItemsControl\.ItemsPanel>(?<panel>[\s\S]*?)</ItemsControl\.ItemsPanel>");
        presetPanel.Success.Should().BeTrue();
        presetPanel.Groups["panel"].Value.Should().Contain(@"<UniformGrid Columns=""2""").And.NotContain("WrapPanel");

        var savedPanel = Regex.Match(PalettesMarkup(), @"ItemsSource=""\{Binding SavedPalettes\}""[\s\S]*?ItemsControl\.ItemsPanel>(?<panel>[\s\S]*?)</ItemsControl\.ItemsPanel>");
        savedPanel.Success.Should().BeTrue();
        savedPanel.Groups["panel"].Value.Should().Contain(@"<UniformGrid Columns=""2""").And.NotContain("WrapPanel");

        VariantButton().Should().Contain(@"HorizontalAlignment=""Stretch""");
        PresetButton().Should().Contain(@"HorizontalAlignment=""Stretch""");
    }

    /// <summary>The single <c>&lt;controls:PaletteChip .../&gt;</c> self-closing tag inside a button's
    /// markup - isolated from the surrounding Button's own attributes (notably its
    /// <c>Classes.selected="{Binding IsSelected}"</c>) so a check for "does the chip itself carry
    /// IsSelected" can't false-positive on the button's unrelated binding of the same name.</summary>
    private static string ChipTag(string button)
    {
        var open = button.IndexOf("<controls:PaletteChip", System.StringComparison.Ordinal);
        var close = button.IndexOf("/>", open, System.StringComparison.Ordinal);
        open.Should().BeGreaterOrEqualTo(0);
        close.Should().BeGreaterThan(open);
        return button.Substring(open, close + "/>".Length - open);
    }

    private static string VariantButton() => ButtonInBody(TemplateBody(ColourMarkup(), "vm:SchemeVariantStripViewModel"));

    private static string PresetButton() => ButtonInBody(TemplateBody(PalettesMarkup(), "vm:SeedPresetTileViewModel"));

    private static string TemplateBody(string markup, string dataType)
    {
        var template = Regex.Match(markup, $@"x:DataType=""{Regex.Escape(dataType)}"">(?<body>[\s\S]*?)</DataTemplate>");
        template.Success.Should().BeTrue($"{dataType}'s DataTemplate must still exist");
        return template.Groups["body"].Value;
    }

    /// <summary>The first Button in <paramref name="body"/> - the tile/apply button, which is always
    /// the first Button in every one of these three templates (the saved-palette row's delete Button
    /// comes after it).</summary>
    private static string ButtonInBody(string body)
    {
        var open = body.IndexOf("<Button", System.StringComparison.Ordinal);
        var close = body.IndexOf("</Button>", System.StringComparison.Ordinal);
        open.Should().BeGreaterOrEqualTo(0);
        return body.Substring(open, close + "</Button>".Length - open);
    }

    /// <summary>The scheme-variant strip's chip row (RemEx-4kv0g.4.3: moved onto the Colour tab).</summary>
    private static string ColourMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizeColourTab.axaml"));

    /// <summary>The preset gallery and saved-palette rows (RemEx-4kv0g.4.3: moved onto the Palettes tab).</summary>
    private static string PalettesMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizePalettesTab.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
