using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>The user-palette row on the sheet (spec section 7), pinned over source text like the preset gallery is.</summary>
public class SavedPalettesWiringTests
{
    [Fact]
    public void TheUserRowAppliesAndDeletesThroughTheSavedPaletteCommands()
    {
        var markup = PanelMarkup();
        var row = Regex.Match(markup, @"<ItemsControl ItemsSource=""\{Binding SavedPalettes\}"".*?</ItemsControl>", RegexOptions.Singleline);

        row.Success.Should().BeTrue("the user palettes row binds SavedPalettes");
        row.Value.Should().Contain("ApplySavedPaletteCommand").And.Contain("DeleteSavedPaletteCommand");
        Regex.IsMatch(row.Value, @"#[0-9A-Fa-f]{6}").Should().BeFalse("a tile renders its own palette, never a literal");
    }

    [Fact]
    public void SaveCurrentWritesANameThePersonCanEdit()
    {
        var markup = PanelMarkup();

        markup.Should().MatchRegex(@"Text=""\{Binding NewPaletteName[,}]", "the name box is bound two-way to NewPaletteName");
        markup.Should().Contain("SaveCurrentPaletteCommand");
    }

    [Fact]
    public void PresetsCannotBeDeleted()
    {
        var gallery = Regex.Match(PanelMarkup(), @"<ItemsControl ItemsSource=""\{Binding ThemePresets\}"".*?</ItemsControl>", RegexOptions.Singleline);

        gallery.Success.Should().BeTrue();
        gallery.Value.Should().NotContain("DeleteSavedPaletteCommand");
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
