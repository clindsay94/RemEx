using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins that the command palette's row labels highlight their search match (RemEx-x6a70.2): both row
/// TextBlocks must bind <c>Inlines</c> through <c>MatchHighlightConverter</c> over their own text
/// (<c>Label</c> / <c>Category</c>) and the live <c>SearchText</c>, rather than a plain
/// <c>Text=</c> binding that cannot render part of itself bold. See
/// <c>CommandPaletteMaterialSurfaceTests</c> for the sibling checks on this same window that this
/// bead must not disturb (Theme type scale stays, no inline Foreground/Opacity).
/// </summary>
public class CommandPaletteHighlightTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Theory]
    [InlineData("palette-label", "Label")]
    [InlineData("palette-category", "Category")]
    public void RowTextBlockBindsInlinesThroughMatchHighlightConverter(string cssClass, string ownTextPath)
    {
        var doc = PaletteWindowDoc();

        var textBlock = doc.Descendants(Avalonia + "TextBlock")
            .FirstOrDefault(e => (string?)e.Attribute("Classes") == cssClass);
        textBlock.Should().NotBeNull($"the {cssClass} TextBlock must still exist");

        textBlock!.Attribute("Text").Should().BeNull(
            "a plain Text= binding cannot render part of itself bold; the row must bind Inlines instead");
        textBlock.Attribute("Theme").Should().NotBeNull("the Theme type-scale key must survive this change");

        var inlinesProperty = textBlock.Elements(Avalonia + "TextBlock.Inlines").FirstOrDefault();
        inlinesProperty.Should().NotBeNull($"{cssClass} must bind Inlines via a property element");

        var multiBinding = inlinesProperty!.Elements(Avalonia + "MultiBinding").FirstOrDefault();
        multiBinding.Should().NotBeNull($"{cssClass}'s Inlines must be driven by a MultiBinding");

        var converter = (string?)multiBinding!.Attribute("Converter");
        converter.Should().NotBeNullOrEmpty();
        converter.Should().Contain("MatchHighlightConverter.Instance",
            "the row must highlight its match through MatchHighlightConverter");

        var bindings = multiBinding.Elements(Avalonia + "Binding").ToList();
        bindings.Should().HaveCount(2, "the MultiBinding needs exactly the row's own text and the live query");

        var paths = bindings.Select(b => (string?)b.Attribute("Path")).ToList();
        paths.Should().Contain(ownTextPath, $"one Binding must read the row's own {ownTextPath}");
        paths.Should().Contain(p => p != null && p.Contains("SearchText"),
            "one Binding must reach the live CommandPaletteViewModel.SearchText");
    }

    private static XDocument PaletteWindowDoc([CallerFilePath] string thisSourceFile = "")
        => XDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(thisSourceFile), "remex.desktop", "Views", "CommandPaletteWindow.axaml")));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
