using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the elevation language finished by RemEx-la0rk: dashboard <c>DraggableCard</c> and the
/// tray windows ride the animated <c>Elevation1..3Shadow</c> ramp instead of a literal
/// <c>BoxShadow</c>, and <c>Button.card</c> steps depth on pointer/focus/press via ShadowAssist.
/// </summary>
/// <remarks>
/// No headless render exists for this suite, so these are source-scanning guards: a literal
/// shadow or a missing pseudo-class compiles and renders something, silently, with no test
/// failure pointing back at the cause unless the source itself is asserted on.
/// </remarks>
public class ElevationStateTests
{
    private static readonly string[] OwnedViews =
        { "CanvasView.axaml", "TrayFlyoutWindow.axaml", "TrayBalloonWindow.axaml" };

    private static readonly Regex LiteralBoxShadow =
        new(@"BoxShadow=""[0-9 .\-]+#", RegexOptions.Compiled);

    [Fact]
    public void NoOwnedViewDeclaresALiteralBoxShadowOutsideAComment()
    {
        foreach (var view in OwnedViews)
        {
            var text = StripXmlComments(ReadView(view));
            LiteralBoxShadow.IsMatch(text).Should().BeFalse(
                $"{view} should ride the Elevation ramp, not a hard-coded BoxShadow literal");
        }
    }

    [Fact]
    public void GrepForLiteralBoxShadowAcrossViewsFindsNothingOutsideComments()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop", "Views"), "*.axaml", SearchOption.AllDirectories)
            .Where(path => LiteralBoxShadow.IsMatch(StripXmlComments(File.ReadAllText(path))))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.Should().BeEmpty(
            "every Views/*.axaml surface should elevate via {DynamicResource ElevationNShadow}");
    }

    [Fact]
    public void DraggableCardThemeFileDoesNotDeclareALiteralBoxShadowOutsideAComment()
    {
        // The chrome moved out of Views/CanvasView.axaml into Themes/Shared/DraggableCard.axaml
        // (RemEx-9iz00.3), so the grep above no longer sees it — this pins the same rule there.
        var text = StripXmlComments(ReadDraggableCardTheme());
        LiteralBoxShadow.IsMatch(text).Should().BeFalse(
            "Themes/Shared/DraggableCard.axaml should ride the Elevation ramp, not a hard-coded BoxShadow literal");
    }

    [Fact]
    public void DraggableCardTemplateBorderBindsElevation1AndCarriesABoxShadowsTransition()
    {
        var text = ReadDraggableCardTheme();

        text.Should().Contain(
            "Name=\"PART_SurfaceBorder\"",
            "the DraggableCard template border must be named so the pointerover/dragging selectors can target it");
        text.Should().MatchRegex(
            @"PART_SurfaceBorder[^>]*BoxShadow=""\{DynamicResource Elevation1Shadow\}""",
            "PART_SurfaceBorder is the DraggableCard resting surface and starts at Elevation1");
        text.Should().Contain(
            "BoxShadowsTransition",
            "the elevation step must animate, not snap");
    }

    [Fact]
    public void DraggingSelectorIsDeclaredAfterPointeroverSoDragWins()
    {
        var text = ReadDraggableCardTheme();

        var pointerOverIndex = text.IndexOf(
            "^:pointerover /template/ Border#PART_SurfaceBorder", StringComparison.Ordinal);
        var draggingIndex = text.IndexOf(
            "^:dragging /template/ Border#PART_SurfaceBorder", StringComparison.Ordinal);

        pointerOverIndex.Should().BeGreaterThan(-1, "the pointerover elevation step must exist");
        draggingIndex.Should().BeGreaterThan(-1, "the dragging elevation step must exist");
        draggingIndex.Should().BeGreaterThan(pointerOverIndex,
            "the :dragging selector must be declared after :pointerover so a later style-sheet rule wins while dragging");
    }

    [Fact]
    public void DraggableCardSetsTheDraggingPseudoClassFromIsDraggingProperty()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "DraggableCard.cs"));

        text.Should().Contain(
            "PseudoClasses.Set(\":dragging\"",
            "IsDragging must drive a :dragging pseudo-class for CanvasView's elevation selector to key off");
    }

    [Fact]
    public void TrayWindowsElevateThroughTheSharedRampNotALiteralShadow()
    {
        foreach (var view in new[] { "TrayFlyoutWindow.axaml", "TrayBalloonWindow.axaml" })
        {
            var text = ReadView(view);
            text.Should().NotContain("BoxShadow=\"0 ", $"{view} should not carry a literal BoxShadow");
        }
    }

    [Fact]
    public void TrayFlyoutWindowShadowFitsInsideItsMargin()
    {
        // RemEx-la0rk round 3: the flyout is a transparent, undecorated window, so a shadow whose
        // blur+OffsetY exceeds the card's Margin gets clipped by the window bounds into a hard band
        // instead of fading out. Elevation2Shadow is blur 34 / OffsetY 9 (ThemeService.cs:528) -
        // needs >=43px of margin. Elevation3Shadow (48/12, needs >=60px) is deliberately not used
        // here; that much margin would eat too much of the 320x240 MinWidth/MinHeight floor.
        var text = ReadView("TrayFlyoutWindow.axaml");

        text.Should().Contain("BoxShadow=\"{DynamicResource Elevation2Shadow}\"",
            "the flyout card should use Elevation2Shadow, whose 34/9 blur/offset fits a reasonable margin");
        text.Should().NotContain("BoxShadow=\"{DynamicResource Elevation3Shadow}\"",
            "Elevation3Shadow's 48/12 blur/offset needs more margin than the flyout can spare and clips into a band");

        var borderIndex = text.IndexOf("BoxShadow=\"{DynamicResource Elevation2Shadow}\"", StringComparison.Ordinal);
        borderIndex.Should().BeGreaterThan(-1);
        var borderTagStart = text.LastIndexOf("<Border", borderIndex, StringComparison.Ordinal);
        var borderTag = text.Substring(borderTagStart, borderIndex - borderTagStart);
        // The gutter stays 12 to match TrayPlacement's marginLogical:12 and the window's fixed
        // Width/Height; a wider margin shrinks the card instead of growing the window. The
        // Elevation2 tail that runs past 12px is softer than the retired 0 8 28 literal, which
        // shipped over the same gutter. Widening it is a window-geometry change, not a shadow one.
        borderTag.Should().Contain("Margin=\"12\"",
            "the card gutter must stay in step with TrayPlacement marginLogical:12 and the resize grab bands");
    }

    [Fact]
    public void AppAxamlCardButtonShadowAssistStepsRestPointeroverAndPressedDepth()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var restIndex = text.LastIndexOf("Selector=\":is(Button).card\">", StringComparison.Ordinal);
        var pointeroverIndex = text.IndexOf("Selector=\":is(Button).card.interactive:pointerover\">\n            <Setter Property=\"assists:ShadowAssist.ShadowDepth\" Value=\"Depth2\"", StringComparison.Ordinal);
        var pressedIndex = text.IndexOf("Selector=\":is(Button).card.interactive:pressed\">\n            <Setter Property=\"assists:ShadowAssist.ShadowDepth\" Value=\"Depth0\"", StringComparison.Ordinal);

        restIndex.Should().BeGreaterThan(-1, "Button.card must declare a ShadowAssist rest depth rule");
        var restSetterWindow = text.Substring(restIndex, 200);
        restSetterWindow.Should().Contain(
            "assists:ShadowAssist.ShadowDepth\" Value=\"Depth1\"",
            "Button.card should rest at Depth1");
        pointeroverIndex.Should().BeGreaterThan(-1,
            "Button.card.interactive should raise to Depth2 on pointerover");
        pressedIndex.Should().BeGreaterThan(-1,
            "Button.card.interactive should settle to Depth0 on pressed");
        // No focus-visible depth rule yet: FocusVisibleStyleGuardTests enumerates every
        // :focus-visible style as a focus ring and pins their count, so the keyboard step of the
        // elevation ramp needs that guard re-pinned first. Tracked as RemEx-alwfa.5; do not add a
        // guard here that forbids it.
    }

    private static string ReadView(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", fileName));

    private static string ReadDraggableCardTheme()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Themes", "Shared", "DraggableCard.axaml"));

    private static string StripXmlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
