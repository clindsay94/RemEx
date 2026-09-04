using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards RemEx-1ufoa.4: HomeView's Recent Activity empty state and the three Links tiles
/// (GitHub, Play Store, HW Info) used to draw hand-typed emoji glyphs in a <c>TextBlock</c>
/// instead of a <c>mi:MaterialIcon</c>. Nothing in <c>HomeViewCharacterisationTests</c> pins
/// those four elements — they carry no binding, name, or literal colour — so a regression here
/// is otherwise invisible to the rest of the suite.
/// </summary>
public class HomeViewGlyphTests
{
    [Theory]
    [InlineData("🗒")]
    [InlineData("🐙")]
    [InlineData("▶")]
    [InlineData("🌡")]
    public void HomeView_NoLongerDrawsTheGlyphAsText(string glyph)
    {
        ReadHomeView().Should().NotContain(glyph,
            $"the {glyph} glyph was replaced by a mi:MaterialIcon (RemEx-1ufoa.4)");
    }

    [Fact]
    public void HomeView_DrawsTheFourReplacementsAsMaterialIcons()
    {
        var text = ReadHomeView();

        var icons = System.Xml.Linq.XDocument.Parse(text)
            .Descendants(System.Xml.Linq.XName.Get("MaterialIcon", "using:Material.Icons.Avalonia"))
            .ToList();

        icons.Should().NotBeEmpty("a query that matches nothing asserts nothing");

        text.Should().Contain("Kind=\"NoteText\"", "the empty Recent Activity state now uses a MaterialIcon");
        text.Should().Contain("Kind=\"Github\"", "the GitHub tile now uses a MaterialIcon");
        text.Should().Contain("Kind=\"GooglePlay\"", "the Play Store tile now uses a MaterialIcon");
        text.Should().Contain("Kind=\"Thermometer\"", "the HW Info tile now uses a MaterialIcon");
    }

    private static string ReadHomeView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
