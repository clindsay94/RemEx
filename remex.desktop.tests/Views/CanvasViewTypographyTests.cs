using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the Material type-scale sweep of CanvasView (RemEx-evw7y): no inline
/// <c>FontSize</c> on a <c>TextBlock</c> (the two survivors on <c>TextBox</c> are exception 4,
/// conventions brief), and none of the glyph/emoji icon text this sweep replaced with
/// <c>mi:MaterialIcon</c> survives anywhere in the file.
/// </summary>
public class CanvasViewTypographyTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void NoTextBlockInCanvasView_CarriesAnInlineFontSize()
    {
        var doc = XDocument.Parse(ViewSource());

        var elements = doc.Descendants(XName.Get("TextBlock", Avalonia)).ToList();
        elements.Should().NotBeEmpty("the parsed CanvasView document should contain TextBlocks to check");

        var offenders = elements.Where(e => e.Attribute("FontSize") != null).ToList();

        offenders.Should().BeEmpty("TextBlocks move onto Theme={StaticResource ...TextBlock} keys");
    }

    [Fact]
    public void TheOnlySurvivingFontSizes_AreOnTextBoxes()
    {
        var doc = XDocument.Parse(ViewSource());

        var textBoxesWithFontSize = doc.Descendants(XName.Get("TextBox", Avalonia))
            .Count(e => e.Attribute("FontSize") != null);

        textBoxesWithFontSize.Should().Be(2, "the connection HostAddress box and the inline rename " +
            "editor keep their inline size — Theme would replace the TextBox's own theme and kill " +
            "SelectionBrush (exception 4, conventions brief)");

        var nonTextBoxWithFontSize = doc.Descendants()
            .Where(e => e.Name.LocalName != "TextBox")
            .Count(e => e.Attribute("FontSize") != null);

        nonTextBoxWithFontSize.Should().Be(0, "no other element in CanvasView should carry an inline FontSize");
    }

    [Theory]
    [InlineData("↩")] // ↩ undo glyph
    [InlineData("↪")] // ↪ redo glyph
    [InlineData("☍")] // ☍ minimap toggle glyph
    [InlineData("📌")] // 📌 pin emoji
    [InlineData("?")]
    public void NoTextBlockOrMenuItemIcon_CarriesTheReplacedGlyphOrEmoji(string glyph)
    {
        var source = ViewSource();

        source.Should().NotContain(glyph, $"'{glyph}' was replaced by an mi:MaterialIcon in RemEx-evw7y");
    }

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
