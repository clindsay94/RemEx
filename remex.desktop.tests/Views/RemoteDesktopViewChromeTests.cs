using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-scan guard for the RemEx-jttwu chrome sweep of <c>RemoteDesktopView.axaml</c>. There is no
/// headless render in this repo, so this asserts against the raw XAML text: the stream host
/// (ViewportBorder / TransformContainer / ScreenImage) stays byte-identical and untouched by the
/// Material chrome sweep, and every <c>material:Card</c> added carries <c>Classes="surface"</c> so
/// its hover RenderTransform (App.axaml:255-283) never shifts the viewport.
/// </summary>
public class RemoteDesktopViewChromeTests
{
    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string ViewSource()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteDesktopView.axaml");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ViewportBorder_IsABorderWithHoldingWiring()
    {
        var source = ViewSource();
        var match = Regex.Match(
            source,
            @"<Border\s+x:Name=""ViewportBorder""[^>]*>",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue("ViewportBorder must remain a <Border>, not a Card");
        match.Value.Should().Contain("ClipToBounds=\"True\"");
        match.Value.Should().Contain("InputElement.IsHoldingEnabled=\"True\"");
        match.Value.Should().Contain("Holding=\"OnViewportHolding\"");
    }

    [Fact]
    public void TransformContainer_IsAPanelWithScaleThenTranslateTransform()
    {
        var source = ViewSource();
        var match = Regex.Match(
            source,
            @"<Panel\s+x:Name=""TransformContainer"".*?</Panel\.RenderTransform>",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue("TransformContainer must remain a <Panel>");
        var body = match.Value;

        var scaleIndex = body.IndexOf("<ScaleTransform", StringComparison.Ordinal);
        var translateIndex = body.IndexOf("<TranslateTransform", StringComparison.Ordinal);
        scaleIndex.Should().BeGreaterThan(-1);
        translateIndex.Should().BeGreaterThan(-1);
        scaleIndex.Should().BeLessThan(translateIndex, "ScaleTransform must precede TranslateTransform in the TransformGroup");
    }

    [Fact]
    public void ScreenImage_IsAnImage()
    {
        var source = ViewSource();
        Regex.IsMatch(source, @"<Image\s+x:Name=""ScreenImage""").Should().BeTrue();
    }

    [Fact]
    public void EveryMaterialCard_CarriesTheSurfaceClass()
    {
        var source = ViewSource();
        var cardOpenTags = Regex.Matches(source, @"<material:Card\b[^>]*>");

        cardOpenTags.Count.Should().BeGreaterThan(0, "the sweep is expected to introduce material:Card chrome");
        foreach (Match tag in cardOpenTags)
        {
            Regex.IsMatch(tag.Value, @"Classes=""[^""]*\bsurface\b[^""]*""")
                .Should().BeTrue($"every material:Card must carry Classes=\"surface\" to suppress the hover RenderTransform, but found: {tag.Value}");
        }
    }

    [Fact]
    public void EveryEdgeToEdgeChromeCard_PinsItsBorderThickness()
    {
        // The Card ControlTheme template-binds BorderThickness to CardBorderThickness (1px on most
        // presets, 3px on Monolith). The old chrome Borders drew no outline, so a CornerRadius="0"
        // Card that leaves BorderThickness unset grows a full box outline on Monolith and nobody
        // sees it: docs/REGRESSION-GUARDS.md "An unset property is not a neutral property".
        var source = ViewSource();
        var chromeCards = Regex.Matches(source, @"<material:Card\b[^>]*CornerRadius=""0""[^>]*>");

        chromeCards.Count.Should().Be(4, "toolbar, connection panel, window panel and status bar are the edge-to-edge chrome");
        foreach (Match tag in chromeCards)
        {
            Regex.IsMatch(tag.Value, @"BorderThickness=""0,[01],0,[01]""")
                .Should().BeTrue($"an edge-to-edge chrome Card must pin BorderThickness to a divider or zero, but found: {tag.Value}");
        }
    }

    [Fact]
    public void NoMaterialCard_EnclosesTheViewport()
    {
        var source = ViewSource();
        var viewportBorderIndex = source.IndexOf("x:Name=\"ViewportBorder\"", StringComparison.Ordinal);
        var transformContainerIndex = source.IndexOf("x:Name=\"TransformContainer\"", StringComparison.Ordinal);

        viewportBorderIndex.Should().BeGreaterThan(-1);
        transformContainerIndex.Should().BeGreaterThan(viewportBorderIndex);

        var between = source.Substring(viewportBorderIndex, transformContainerIndex - viewportBorderIndex);

        // A Card may appear as a *sibling* in this span (e.g. the placeholder-hint card), but none
        // may still be open when TransformContainer starts - that would mean it *encloses* the viewport.
        var opens = Regex.Matches(between, @"<material:Card\b[^>]*?(/?)>");
        var closes = Regex.Matches(between, @"</material:Card>");
        var selfClosingOpens = 0;
        foreach (Match open in opens)
        {
            if (open.Groups[1].Value == "/")
            {
                selfClosingOpens++;
            }
        }

        var openCount = opens.Count - selfClosingOpens;
        openCount.Should().Be(closes.Count, "no material:Card may still be open (i.e. enclosing) when TransformContainer begins");
    }

    [Fact]
    public void WindowPanel_TwelvePointTextUsesCaptionNotBody2()
    {
        // Regression guard for RemEx-jttwu review round 3: WindowControlCapabilityText and each
        // window row's Title were originally FontSize="12" and must map to CaptionTextBlock per
        // docs/TYPOGRAPHY-VOCABULARY.md's 11/12/13 -> Caption band, not Body2TextBlock (14px).
        var source = ViewSource();

        var capabilityLine = Regex.Match(source, @"<TextBlock\s+Text=""\{Binding WindowControlCapabilityText\}""[^>]*/>");
        capabilityLine.Success.Should().BeTrue("WindowControlCapabilityText TextBlock must be present");
        capabilityLine.Value.Should().Contain("Theme=\"{StaticResource CaptionTextBlock}\"",
            "WindowControlCapabilityText was originally FontSize=\"12\" and belongs on CaptionTextBlock, not Body2TextBlock");

        var titleLine = Regex.Match(source, @"<TextBlock\s+Text=""\{Binding Title\}""[^>]*/>");
        titleLine.Success.Should().BeTrue("the window-row Title TextBlock must be present");
        titleLine.Value.Should().Contain("Theme=\"{StaticResource CaptionTextBlock}\"",
            "the window-row Title was originally FontSize=\"12\" and belongs on CaptionTextBlock, not Body2TextBlock");
    }
}
