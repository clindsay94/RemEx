using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Media;
// TextElement carries the FontSize/Foreground attached properties Run inherits (Run -> Inline ->
// TextElement) - not TextBlock, which is a sibling control with its own unrelated properties.
using FluentAssertions;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Guards <see cref="MatchHighlightConverter"/>, which lets a command palette row show WHICH
/// characters matched the current search query, not just THAT it did (RemEx-x6a70.2). The
/// <c>Segment</c> tests pin the pure splitting logic in isolation; the <c>Convert</c> tests pin the
/// Avalonia-facing half, in particular that a Run never carries Foreground or Opacity - those must
/// stay unset so the row's selected/hover colour rules (RemEx-o9gd) keep applying to the whole
/// TextBlock, matched text included.
/// </summary>
public class MatchHighlightConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void EmptyQuery_YieldsOneNonMatchSegment()
    {
        var segments = MatchHighlightConverter.Segment("Sensors", "");

        segments.Should().ContainSingle().Which.Should().Be(("Sensors", false));
    }

    [Fact]
    public void WhitespaceOnlyQuery_YieldsOneNonMatchSegment()
    {
        var segments = MatchHighlightConverter.Segment("Sensors", "   ");

        segments.Should().ContainSingle().Which.Should().Be(("Sensors", false));
    }

    [Fact]
    public void QueryTrimmed_MatchesDespiteSurroundingWhitespace()
    {
        var segments = MatchHighlightConverter.Segment("Sensors", "  sen  ");

        segments.Should().Equal(("Sen", true), ("sors", false));
    }

    [Fact]
    public void MatchAtStart()
    {
        var segments = MatchHighlightConverter.Segment("Sensors", "Sen");

        segments.Should().Equal(("Sen", true), ("sors", false));
    }

    [Fact]
    public void MatchInMiddle()
    {
        var segments = MatchHighlightConverter.Segment("Dashboard", "board");

        segments.Should().Equal(("Dash", false), ("board", true));
    }

    [Fact]
    public void MatchAtEnd()
    {
        var segments = MatchHighlightConverter.Segment("Remote Desktop", "Desktop");

        segments.Should().Equal(("Remote ", false), ("Desktop", true));
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var segments = MatchHighlightConverter.Segment("Sensors", "SEN");

        segments.Should().Equal(("Sen", true), ("sors", false));
    }

    [Fact]
    public void MultipleOccurrences_AreAllHighlighted()
    {
        var segments = MatchHighlightConverter.Segment("banana", "an");

        segments.Should().Equal(("b", false), ("an", true), ("an", true), ("a", false));
    }

    [Fact]
    public void MatchBoundary_NeverSplitsACombiningMarkFromItsBase()
    {
        // Decomposed "Ménu": M, e, COMBINING ACUTE ACCENT, n, u. A query of "me" matches the first
        // two UTF-16 units, but the bold run must carry the accent with its base character or the
        // accent starts the next run and shapes onto a dotted circle.
        var segments = MatchHighlightConverter.Segment("Me\u0301nu", "me");

        segments.Should().Equal(("Me\u0301", true), ("nu", false));
        string.Concat(segments.Select(s => s.Text)).Should().Be("Me\u0301nu");
    }

    [Fact]
    public void MatchBoundary_SnapsDownToTheGraphemeStartToo()
    {
        // The query lands on the combining mark's base when the match begins inside a grapheme
        // (a query of "\u0301n" against the same text): the bold run must start at the "e".
        var segments = MatchHighlightConverter.Segment("Me\u0301nu", "\u0301n");

        segments.Should().Equal(("M", false), ("e\u0301n", true), ("u", false));
    }

    [Fact]
    public void OverlappingOccurrences_AreNonOverlapping()
    {
        // "aa" occurs at index 0-1 and 1-2 in "aaa", but a match consumes its characters before the
        // next search starts, so the second occurrence (starting at index 1) is never found.
        var segments = MatchHighlightConverter.Segment("aaa", "aa");

        segments.Should().Equal(("aa", true), ("a", false));
    }

    [Fact]
    public void QueryLongerThanText_YieldsOneNonMatchSegment()
    {
        var segments = MatchHighlightConverter.Segment("ab", "abcd");

        segments.Should().ContainSingle().Which.Should().Be(("ab", false));
    }

    [Fact]
    public void NoOccurrence_YieldsOneNonMatchSegment()
    {
        var segments = MatchHighlightConverter.Segment("Sensors", "zzz");

        segments.Should().ContainSingle().Which.Should().Be(("Sensors", false));
    }

    [Fact]
    public void NullText_YieldsEmptyList()
    {
        var segments = MatchHighlightConverter.Segment(null, "sen");

        segments.Should().BeEmpty();
    }

    [Fact]
    public void EmptyText_YieldsEmptyList()
    {
        var segments = MatchHighlightConverter.Segment("", "sen");

        segments.Should().BeEmpty();
    }

    [Fact]
    public void Convert_BoldsOnlyMatchRuns_AndConcatenatesBackToTheInput()
    {
        var result = MatchHighlightConverter.Instance.Convert(
            new object?[] { "Dashboard", "board" }, typeof(InlineCollection), null, Culture);

        var inlines = result.Should().BeOfType<InlineCollection>().Subject;
        var runs = inlines.OfType<Run>().ToList();

        runs.Should().HaveCount(2);
        string.Concat(runs.Select(r => r.Text)).Should().Be("Dashboard");

        runs[0].FontWeight.Should().Be(FontWeight.Normal);
        runs[1].FontWeight.Should().Be(FontWeight.Bold);
    }

    [Fact]
    public void Convert_NeverSetsForegroundOrFontSizeOnAnyRun()
    {
        var result = MatchHighlightConverter.Instance.Convert(
            new object?[] { "Sensors", "sen" }, typeof(InlineCollection), null, Culture);

        var runs = result.Should().BeOfType<InlineCollection>().Subject.OfType<Run>();

        foreach (var run in runs)
        {
            run.IsSet(TextElement.ForegroundProperty).Should().BeFalse(
                "the row's Foreground must come from the ListBox.Styles selectors, not the Run (RemEx-o9gd)");
            run.IsSet(TextElement.FontSizeProperty).Should().BeFalse();
        }
    }

    [Fact]
    public void Convert_WithNoMatch_ProducesASingleUnboldRun()
    {
        var result = MatchHighlightConverter.Instance.Convert(
            new object?[] { "Sensors", "zzz" }, typeof(InlineCollection), null, Culture);

        var runs = result.Should().BeOfType<InlineCollection>().Subject.OfType<Run>().ToList();

        runs.Should().ContainSingle();
        runs[0].Text.Should().Be("Sensors");
        runs[0].FontWeight.Should().Be(FontWeight.Normal);
    }

    [Fact]
    public void Convert_UnusableInput_YieldsAPlainSingleRun()
    {
        var result = MatchHighlightConverter.Instance.Convert(
            new object?[] { 42, true }, typeof(InlineCollection), null, Culture);

        var runs = result.Should().BeOfType<InlineCollection>().Subject.OfType<Run>().ToList();

        runs.Should().ContainSingle();
        runs[0].FontWeight.Should().Be(FontWeight.Normal);
    }
}
