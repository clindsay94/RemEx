using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Source-text pins for <c>BusyPlaceholder.axaml</c> (RemEx-kjdi §2): every brush is a theme
/// token — no hex literal, so the shimmer reads correctly on every palette-sweep cell rather than
/// just the one it was designed against — and the shimmer animation only ever runs when motion is
/// not reduced. This test project has no headless render, so source-text is the idiom the other
/// Controls tests already use (see <c>AuroraMeshTests</c>).
/// </summary>
public class BusyPlaceholderTests
{
    private static string ControlSource() => File.ReadAllText(ControlPath());

    [Fact]
    public void EveryBrushIsADynamicResourceToken_NoHexLiteralsAnywhere()
    {
        var text = ControlSource();

        Regex.IsMatch(text, @"#[0-9A-Fa-f]{3,8}").Should().BeFalse(
            "a hex literal here bypasses the theme and will not read correctly on a light palette");

        // Every colour-bearing property this template sets must actually go through a
        // DynamicResource token, not merely avoid hex — asserts the POSITIVE, not just the negative.
        // Covers both direct-attribute (Foreground="...") and Setter (Property="Background"
        // Value="...") syntax, since this template uses both.
        foreach (var property in new[] { "Background", "BorderBrush", "Foreground" })
        {
            var directAttribute = new Regex($@"[^-\.]{property}=""([^""]*)""");
            foreach (Match match in directAttribute.Matches(text))
            {
                match.Groups[1].Value.Should().StartWith("{DynamicResource ",
                    $"{property} must be a theme token, found: {match.Groups[1].Value}");
            }

            var setterSyntax = new Regex($@"Property=""{property}""\s+Value=""([^""]*)""");
            foreach (Match match in setterSyntax.Matches(text))
            {
                match.Groups[1].Value.Should().StartWith("{DynamicResource ",
                    $"{property} must be a theme token, found: {match.Groups[1].Value}");
            }
        }
    }

    [Fact]
    public void TheShimmerAnimation_OnlyRunsWhenMotionIsNotReduced()
    {
        var text = ControlSource();

        text.Should().Contain("Style.Animations", "the skeleton bars must actually animate somewhere");

        // The Style whose Selector carries the animation must be gated on :not(:reduced-motion) —
        // find the Style element containing Style.Animations and check ITS OWN Selector, not just
        // that the substring exists somewhere in the file.
        var animatedStyleMatch = Regex.Match(
            text,
            @"<Style Selector=""([^""]+)"">\s*<Style\.Animations>",
            RegexOptions.Singleline);

        animatedStyleMatch.Success.Should().BeTrue("expected a Style element wrapping Style.Animations directly");
        animatedStyleMatch.Groups[1].Value.Should().Contain(":not(:reduced-motion)",
            "an ungated shimmer would keep animating for a reduced-motion user");
    }

    [Fact]
    public void ReducedMotion_GetsAStaticPlaceholderInstead()
    {
        var text = ControlSource();

        // A separate, non-animated rule must apply when :reduced-motion is set — the static
        // fallback the bead calls for, not merely "no animation" (which would leave the resting
        // 0.55 opacity from Border.skeleton-bar and look identical to the idle state).
        Regex.IsMatch(text, @"<Style Selector=""[^""]*:reduced-motion[^""]*"">\s*<Setter Property=""Opacity""")
            .Should().BeTrue("reduced motion needs its own static Opacity setter, not just an absent animation");
    }

    private static string ControlPath() =>
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "BusyPlaceholder.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
