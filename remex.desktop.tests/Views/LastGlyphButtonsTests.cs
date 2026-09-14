using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards RemEx-me22: the last two raw-Unicode-glyph buttons in the desktop app —
/// <c>ColorPickerPopup</c>'s confirm tick and <c>PersonalizationPanelView</c>'s saved-palette
/// delete cross — moved to <c>Material.Icons</c>, matching what <c>MaterialIconAdoptionTests</c>
/// and <c>HomeViewGlyphTests</c> already guard for the rest of the app.
/// </summary>
/// <remarks>
/// <c>ColorPickerPopup.cs</c> is code-behind, not markup, so it falls outside
/// <c>MaterialIconAdoptionTests</c>' XAML-only scan — this file's first two tests are what actually
/// cover it. A SOURCE-TEXT test for the same reason the sibling files give: there is no headless
/// render in this suite, so a broken or reverted icon draws nothing and throws nothing.
/// </remarks>
public class LastGlyphButtonsTests
{
    private const string AvaloniaNamespace = "https://github.com/avaloniaui";
    private const string MaterialIconsNamespace = "using:Material.Icons.Avalonia";

    [Fact]
    public void ColorPickerPopup_NoLongerDrawsTheConfirmTickAsRawText()
    {
        ReadColorPickerPopup().Should().NotContain("\"✓\"",
            "the confirm button's ✓ glyph was replaced by a MaterialIcon (RemEx-me22)");
    }

    [Fact]
    public void ColorPickerPopup_DrawsTheConfirmButtonAsAMaterialIconWithAnAccessibleName()
    {
        var source = ReadColorPickerPopup();

        // Structural, not a bare string.Contains for "MaterialIconKind.Check": that would also be
        // satisfied by the token sitting in a comment. Anchoring on "Content = new MaterialIcon"
        // followed by "Kind = MaterialIconKind.Check" within the same object initializer ties the
        // assertion to the actual button content.
        var contentBlock = Regex.Match(source,
            @"Content\s*=\s*new MaterialIcon\s*\{[^}]*Kind\s*=\s*MaterialIconKind\.(\w+)[^}]*\}",
            RegexOptions.Singleline);

        contentBlock.Success.Should().BeTrue(
            "the apply button's Content should construct a MaterialIcon with an explicit Kind");
        contentBlock.Groups[1].Value.Should().Be("Check",
            "a checkmark glyph maps to MaterialIconKind.Check");

        // A raw glyph carried its accessible name implicitly through the text itself; a
        // MaterialIcon is not a label, so the name must now be set explicitly or a screen reader
        // announces nothing (RemEx-me22).
        source.Should().MatchRegex(@"AutomationProperties\.SetName\(applyBtn,",
            "swapping the glyph for an icon must not silently drop the button's accessible name");
    }

    [Fact]
    public void PersonalizationPanelView_NoLongerDrawsTheDeleteCrossAsRawText()
    {
        ReadPersonalizationPanelView().Should().NotContain("\"✕\"",
            "the saved-palette delete button's ✕ glyph was replaced by a mi:MaterialIcon (RemEx-me22)");
    }

    [Fact]
    public void PersonalizationPanelView_DeletePaletteButton_DrawsAMaterialIconClose()
    {
        var buttons = XDocument.Parse(ReadPersonalizationPanelView())
            .Descendants(XName.Get("Button", AvaloniaNamespace))
            .Where(b => (b.Attribute("Command")?.Value ?? string.Empty).Contains("DeleteSavedPaletteCommand"))
            .ToList();

        buttons.Should().ContainSingle(
            "the saved-palette card has exactly one delete button, bound to DeleteSavedPaletteCommand");

        var button = buttons[0];

        button.Attribute("Content").Should().BeNull(
            "the glyph used to be carried as a Content attribute; it must be gone, not merely unreferenced");

        var icons = button.Descendants(XName.Get("MaterialIcon", MaterialIconsNamespace)).ToList();
        icons.Should().ContainSingle("the delete button draws exactly one MaterialIcon");
        icons[0].Attribute("Kind")?.Value.Should().Be("Close",
            "a delete/cross glyph maps to MaterialIconKind.Close, matching AppLauncherView's Remove button");

        // The glyph carried its accessible name implicitly; confirm the swap did not cost it.
        button.Attribute("AutomationProperties.Name").Should().NotBeNull(
            "the delete button must keep an explicit accessible name now that its content is an icon, not text");
    }

    private static string ReadColorPickerPopup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "ColorPickerPopup.cs"));

    /// <summary>RemEx-4kv0g.4.3: the saved-palette row (and its delete button) moved onto the
    /// Palettes tab.</summary>
    private static string ReadPersonalizationPanelView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizePalettesTab.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
