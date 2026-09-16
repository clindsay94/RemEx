using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Audit RemEx-4kv0g.5.5: the nine power tiles' glyphs used to inherit the neutral
/// <c>TextPrimaryBrush</c> — every glyph looked the same regardless of what it did. The six
/// Graceful tiles' glyphs must be tinted <c>AccentPrimaryBrush</c> and the three Forced tiles'
/// glyphs <c>SystemErrorBrush</c>, matching the section headers just above each group
/// (RemoteView.axaml:149 uses <c>TextMutedBrush</c> for "Graceful", :190 uses
/// <c>SystemErrorBrush</c> for "Forced"). Source-level, matching the other surface tests in this
/// folder: there is no headless render, and a missing Foreground on one tile paints nothing wrong
/// loudly enough for a test that only counts DOM nodes to catch.
/// </summary>
public class RemoteViewSurfaceTests
{
    private static string RemoteViewMarkup() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteView.axaml"));

    [Fact]
    public void SixTileGlyphsCarryTheAccentPrimaryBrush()
    {
        var matches = Regex.Matches(
            RemoteViewMarkup(),
            @"<mi:MaterialIcon\s+Kind=""[^""]+""\s+Width=""32""\s+Height=""32""\s+HorizontalAlignment=""Center""\s+Foreground=""\{DynamicResource AccentPrimaryBrush\}""\s*/>");

        matches.Count.Should().Be(6,
            "the six Graceful tiles (Lock, SignOut, Shutdown, Sleep, Restart, Hibernate) each need "
            + "their glyph tinted with the app's accent, not left neutral");
    }

    [Fact]
    public void ThreeTileGlyphsCarryTheSystemErrorBrush()
    {
        var matches = Regex.Matches(
            RemoteViewMarkup(),
            @"<mi:MaterialIcon\s+Kind=""[^""]+""\s+Width=""32""\s+Height=""32""\s+HorizontalAlignment=""Center""\s+Foreground=""\{DynamicResource SystemErrorBrush\}""\s*/>");

        matches.Count.Should().Be(3,
            "the three Forced tiles (ForceShutdown, ForceRestart, RebootUefi) each need their glyph "
            + "tinted with the app's error colour, matching the danger classes already on those "
            + "buttons and the SystemErrorBrush already on the \"Forced\" section header");
    }

    [Fact]
    public void NoPowerTileGlyphIsLeftUnthemed()
    {
        // Every 32x32 centered MaterialIcon in this file is one of the nine power-tile glyphs (the
        // Wake-on-LAN header icon above them is 20x20). A bare, Foreground-less one would mean a
        // tile slipped through the sweep and silently kept inheriting TextPrimaryBrush.
        var bareGlyphs = Regex.Matches(
            RemoteViewMarkup(),
            @"<mi:MaterialIcon\s+Kind=""[^""]+""\s+Width=""32""\s+Height=""32""\s+HorizontalAlignment=""Center""\s*/>");

        bareGlyphs.Count.Should().Be(0,
            "every 32x32 power-tile glyph must carry an explicit Foreground — accent for Graceful, "
            + "error for Forced");
    }

    [Fact]
    public void LabelsAreUntouched()
    {
        // The brief is explicit that labels stay as-is (Foreground inherited from .tile's Button
        // template) — only the glyphs change. A TextBlock carrying an explicit AccentPrimaryBrush/
        // SystemErrorBrush Foreground right before one of these MaterialIcons would mean the edit
        // over-reached onto the label too.
        Regex.IsMatch(
            RemoteViewMarkup(),
            @"<TextBlock[^>]*Foreground=""\{DynamicResource (AccentPrimaryBrush|SystemErrorBrush)\}""[^>]*/>\s*<mi:MaterialIcon")
            .Should().BeFalse("only the glyph's own Foreground should have changed, not the tile label's");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
