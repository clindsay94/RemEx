using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-text assertions over HomeView.axaml / .axaml.cs for the dashboard entrance
/// (RemEx-dnfq0). remex.desktop.tests has no headless render, so paint correctness is checked as
/// text here rather than pixels.
/// </summary>
public class HomeViewEntranceTests
{
    [Fact]
    public void DashboardSectionsStackPanelIsNamed()
    {
        Home().Should().Contain("StackPanel Name=\"DashboardSections\"");
    }

    [Fact]
    public void ExactlySixEntranceNthChildStylesExist()
    {
        var xaml = Home();

        // Retargeted to the container's ACTUAL direct-child count (parsed XAML) rather than a
        // bare hardcoded 6, per review of this bead — see XamlContainerHelper. The literal 6
        // stays as a sanity pin: if DashboardSections ever grows or shrinks a section, this line
        // is what forces that to be a deliberate edit here rather than a silent drift.
        var expectedFromMarkup = XamlContainerHelper.CountDirectChildren(xaml, "DashboardSections");
        expectedFromMarkup.Should().Be(6);

        var count = Count(xaml,
            @"StackPanel#DashboardSections\.entrance > :is\(Control\):nth-child\((\d)\)");
        count.Should().Be(expectedFromMarkup);

        // Avalonia has no keyframe animator for RenderTransform itself; the first cut of this
        // bead animated it and every launch died in Animation.InterpretKeyframes before first
        // paint (RemEx-qolhg). Keyframes must target the transform sub-property instead.
        xaml.Should().NotMatchRegex(@"<KeyFrame[\s\S]*?Property=""RenderTransform""",
            "keyframe setters must animate TranslateTransform.Y, never RenderTransform");
        Count(xaml, @"Property=""TranslateTransform\.Y""").Should().Be(expectedFromMarkup * 2,
            "each entrance animation sets TranslateTransform.Y at 0% and 100%");

        for (var n = 1; n <= expectedFromMarkup; n++)
        {
            xaml.Should().Contain(
                $"StackPanel#DashboardSections.entrance > :is(Control):nth-child({n})");
        }
    }

    [Fact]
    public void EntranceAnimationsStayWithinTheThreeHundredMillisecondBudget()
    {
        var text = Home();
        var matches = Regex.Matches(text,
            @"Duration=""0:0:0\.(\d+)""\s+Delay=""0:0:0\.(\d+)""");

        matches.Count.Should().Be(6, "one Animation per nth-child style");

        var maxTotalMs = 0;
        foreach (Match m in matches)
        {
            // The captured fractional-seconds digits may be 2 or 3 wide (e.g. "175" vs "25");
            // normalise both to milliseconds.
            var durationMs = NormaliseToMilliseconds(m.Groups[1].Value);
            var delayMs = NormaliseToMilliseconds(m.Groups[2].Value);
            var total = durationMs + delayMs;
            if (total > maxTotalMs) maxTotalMs = total;
        }

        maxTotalMs.Should().BeLessOrEqualTo(300);
    }

    [Fact]
    public void CodeBehindGatesTheEntranceOnStaggeredEntranceAndReducedMotion()
    {
        var code = HomeViewCodeBehind();
        code.Should().Contain("StaggeredEntrance.ShouldPlay");
        code.Should().Contain("vm.Shell.IsReducedMotion");
        code.Should().Contain("OnAttachedToVisualTree");
    }

    private static int NormaliseToMilliseconds(string fractionalSecondsDigits)
    {
        // "0:0:0.175" -> 175ms; "0:0:0.025" -> 25ms. The regex group captures the digits after the
        // decimal point verbatim (3 digits for millisecond-precision Avalonia TimeSpan literals).
        var padded = fractionalSecondsDigits.PadRight(3, '0');
        return int.Parse(padded);
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    private static string Home()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string HomeViewCodeBehind()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
