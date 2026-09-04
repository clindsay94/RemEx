using System;
using System.Collections.Generic;
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
        var icons = ParseMaterialIcons();

        icons.Should().NotBeEmpty("a query that matches nothing asserts nothing");

        // Assert over the parsed elements' Kind attribute values, not raw text: a literal
        // Kind="NoteText" surviving only in a comment would satisfy a raw string.Contains and
        // prove nothing (reviewer LOW on RemEx-1ufoa.4 slice 1).
        var kinds = icons.Select(i => i.Attribute("Kind")?.Value).ToList();
        kinds.Should().Contain("NoteText", "the empty Recent Activity state now uses a MaterialIcon");
        kinds.Should().Contain("Github", "the GitHub tile now uses a MaterialIcon");
        kinds.Should().Contain("GooglePlay", "the Play Store tile now uses a MaterialIcon");
        kinds.Should().Contain("Thermometer", "the HW Info tile now uses a MaterialIcon");
    }

    [Fact]
    public void ActivityEntryTemplate_NoLongerBindsGlyph()
    {
        ReadHomeView().Should().NotContain("{Binding Glyph}",
            "the Recent Activity row icon was replaced by a mi:MaterialIcon bound through " +
            "ActivityKindToIconKindConverter (RemEx-1ufoa.4)");
    }

    [Fact]
    public void ActivityEntryTemplate_DrawsItsIconThroughTheActivityKindConverter()
    {
        var icons = ParseMaterialIcons();
        icons.Should().NotBeEmpty("a query that matches nothing asserts nothing");

        var boundThroughConverter = icons
            .Select(i => i.Attribute("Kind")?.Value)
            .Where(v => v is not null)
            .Any(v => v!.Contains("Binding Kind", StringComparison.Ordinal)
                && v.Contains("ActivityKindToIconKindConverter", StringComparison.Ordinal));

        boundThroughConverter.Should().BeTrue(
            "the Recent Activity row's MaterialIcon.Kind must bind through ActivityKindToIconKindConverter");
    }

    private static List<System.Xml.Linq.XElement> ParseMaterialIcons()
        => System.Xml.Linq.XDocument.Parse(ReadHomeView())
            .Descendants(System.Xml.Linq.XName.Get("MaterialIcon", "using:Material.Icons.Avalonia"))
            .ToList();

    private static string ReadHomeView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
