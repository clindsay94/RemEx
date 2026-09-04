using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-text assertions over the drawer nav's staggered entrance (RemEx-alwfa.2 slice 2), the
/// same StaggeredEntrance gate (RemEx-dnfq0) as <see cref="ViewEntranceTests"/> reused on a
/// <c>ListBox</c>/<c>ListBoxItem</c> container instead of a <c>StackPanel</c>/<c>:is(Control)</c>
/// one, and on ShellView's own <c>ShellViewModel</c> DataContext instead of a <c>Shell</c>
/// property. remex.desktop.tests has no headless render, so paint correctness is checked as text
/// rather than pixels - see <see cref="HomeViewEntranceTests"/> for the original version of these
/// checks and <see cref="ViewEntranceTests"/> for the parametrised slice-1 version.
/// </summary>
public class ShellNavEntranceTests
{
    private const string ContainerName = "NavList";

    [Fact]
    public void NavListListBoxIsNamed()
    {
        Markup().Should().Contain("Name=\"NavList\"");
    }

    [Fact]
    public void EntranceNthChildStyleCountMatchesNavListsDirectChildCount()
    {
        var xaml = Markup();
        var expected = XamlContainerHelper.CountDirectChildren(xaml, ContainerName);
        expected.Should().BeGreaterThan(0, $"{ContainerName} should have at least one direct child to animate");

        var stylePattern = @"ListBox#NavList\.entrance > ListBoxItem:nth-child\((\d+)\)";
        Regex.Matches(xaml, stylePattern).Count.Should().Be(expected,
            $"NavList has {expected} direct ListBoxItem children, so it needs one nth-child style per child");

        // Avalonia has no keyframe animator for RenderTransform itself; animating it crashes at
        // startup in Animation.InterpretKeyframes before first paint (RemEx-qolhg). Keyframes must
        // target the transform sub-property instead. Scoped to NavList's own entrance style blocks
        // rather than a file-wide scan, so an unrelated RenderTransform setter elsewhere in
        // ShellView.axaml (the drawer-toggle button's :pressed scale, the gear FAB's hover/press
        // scale) cannot be mistaken for one of these blocks (review lesson from RemEx-alwfa.2
        // slice 1, ViewEntranceTests).
        foreach (Match block in EntranceStyleBlocks(xaml))
        {
            block.Value.Should().NotContain("Property=\"RenderTransform\"",
                "keyframe setters must animate TranslateTransform.Y, never RenderTransform");
        }

        Regex.Matches(xaml, @"Property=""TranslateTransform\.Y""").Count.Should().Be(expected * 2,
            "each entrance animation sets TranslateTransform.Y at 0% and 100%");
    }

    [Fact]
    public void EntranceAnimationsStayWithinTheThreeHundredMillisecondBudget()
    {
        var xaml = Markup();
        var expected = XamlContainerHelper.CountDirectChildren(xaml, ContainerName);

        var blocks = EntranceStyleBlocks(xaml);
        blocks.Count.Should().Be(expected, "one Animation per nth-child style");

        var maxTotalMs = 0;
        foreach (Match block in blocks)
        {
            // Duration and Delay are extracted INDEPENDENTLY (review of RemEx-dnfq0), rather than
            // by one regex requiring them adjacent in a fixed order, so either attribute could move
            // without breaking this test for a reason unrelated to the budget itself.
            var durationMatch = Regex.Match(block.Value, @"Duration=""0:0:0\.(\d+)""");
            var delayMatch = Regex.Match(block.Value, @"Delay=""0:0:0\.(\d+)""");
            durationMatch.Success.Should().BeTrue("every entrance Animation sets a Duration");
            delayMatch.Success.Should().BeTrue("every entrance Animation sets a Delay");

            var total = NormaliseToMilliseconds(durationMatch.Groups[1].Value)
                      + NormaliseToMilliseconds(delayMatch.Groups[1].Value);
            if (total > maxTotalMs) maxTotalMs = total;
        }

        maxTotalMs.Should().BeLessOrEqualTo(300);
    }

    [Fact]
    public void CodeBehindGatesTheEntranceOnStaggeredEntranceAndReducedMotion()
    {
        var code = CodeBehind();
        code.Should().Contain("StaggeredEntrance.ShouldPlay");

        // ShellView's DataContext IS the ShellViewModel - unlike the per-page views in
        // ViewEntranceTests, there is no Shell property to read reduced motion off of.
        code.Should().Contain("vm.IsReducedMotion");
        code.Should().NotContain("vm.Shell.IsReducedMotion");

        code.Should().Contain("OnAttachedToVisualTree");
        code.Should().Contain($"{ContainerName}\")?.Classes.Add(StaggeredEntrance.Class)");

        // The gate key is the literal "ShellNav", not nameof(ShellView) - see the remark on
        // ShellView.axaml.cs's OnAttachedToVisualTree for why a distinct literal was chosen.
        code.Should().MatchRegex(@"StaggeredEntrance\.ShouldPlay\(""ShellNav"",",
            "the once-per-process gate key must be a key distinct from any per-view nameof(...) slot");
    }

    /// <summary>
    /// NavList's own entrance style blocks, from the nth-child selector to its closing tag. Both
    /// the RenderTransform guard and the budget check iterate these rather than the whole file, so
    /// an unrelated style elsewhere in ShellView.axaml cannot be miscounted or mis-flagged.
    /// </summary>
    private static MatchCollection EntranceStyleBlocks(string xaml)
        => Regex.Matches(xaml,
            @"ListBox#NavList\.entrance > ListBoxItem:nth-child\(\d+\)"">[\s\S]*?</Style>");

    private static int NormaliseToMilliseconds(string fractionalSecondsDigits)
    {
        // "0:0:0.120" -> 120ms; "0:0:0.020" -> 20ms. The regex group captures the digits after the
        // decimal point verbatim (3 digits for millisecond-precision Avalonia TimeSpan literals).
        var padded = fractionalSecondsDigits.PadRight(3, '0');
        return int.Parse(padded);
    }

    private static string Markup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string CodeBehind()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
