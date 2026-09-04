using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-text assertions over the staggered entrance (RemEx-dnfq0) as it is reused on the Remote,
/// Settings and App Launcher views (RemEx-alwfa.2 slice 1) and re-checked here on HomeView too.
/// remex.desktop.tests has no headless render, so paint correctness is checked as text rather than
/// pixels — see <see cref="HomeViewEntranceTests"/> for the original, non-parametrised version of
/// these checks.
/// </summary>
public class ViewEntranceTests
{
    public static TheoryData<string, string> Views => new()
    {
        { "HomeView", "DashboardSections" },
        { "RemoteView", "RemoteSections" },
        { "SettingsView", "SettingsSections" },
        { "AppLauncherView", "LauncherSections" },
    };

    public static TheoryData<string, string> DrawerViews => new()
    {
        { "RemoteView", "RemoteSections" },
        { "SettingsView", "SettingsSections" },
        { "AppLauncherView", "LauncherSections" },
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void ContainerStackPanelIsNamed(string viewFile, string containerName)
    {
        Markup(viewFile).Should().Contain($"StackPanel Name=\"{containerName}\"");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void EntranceNthChildStyleCountMatchesTheContainersDirectChildCount(string viewFile, string containerName)
    {
        var xaml = Markup(viewFile);
        var expected = XamlContainerHelper.CountDirectChildren(xaml, containerName);
        expected.Should().BeGreaterThan(0, $"{containerName} should have at least one direct child to animate");

        var stylePattern = $@"StackPanel#{containerName}\.entrance > :is\(Control\):nth-child\((\d+)\)";
        Regex.Matches(xaml, stylePattern).Count.Should().Be(expected,
            $"{viewFile}'s {containerName} has {expected} direct children, so it needs one nth-child style per child");

        // Avalonia has no keyframe animator for RenderTransform itself; animating it crashes at
        // startup in Animation.InterpretKeyframes before first paint (RemEx-qolhg). Keyframes must
        // target the transform sub-property instead. Scoped to this container's entrance style
        // blocks: a file-wide "<KeyFrame ... RenderTransform" scan would trip on an unrelated
        // RenderTransform setter (AppLauncherView's drag scale) the moment it moved below the
        // first keyframe (review of RemEx-alwfa.2).
        foreach (Match block in EntranceStyleBlocks(xaml, containerName))
        {
            block.Value.Should().NotContain("Property=\"RenderTransform\"",
                "keyframe setters must animate TranslateTransform.Y, never RenderTransform");
        }

        Regex.Matches(xaml, @"Property=""TranslateTransform\.Y""").Count.Should().Be(expected * 2,
            "each entrance animation sets TranslateTransform.Y at 0% and 100%");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void EntranceAnimationsStayWithinTheThreeHundredMillisecondBudget(string viewFile, string containerName)
    {
        var xaml = Markup(viewFile);
        var expected = XamlContainerHelper.CountDirectChildren(xaml, containerName);

        // Scoped to this container's own style blocks (rather than a file-wide Duration/Delay
        // scan) so an unrelated Duration+Delay pair elsewhere in the same file cannot be
        // miscounted as one of this container's entrance animations.
        var blocks = EntranceStyleBlocks(xaml, containerName);
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

    [Theory]
    [MemberData(nameof(DrawerViews))]
    public void CodeBehindGatesTheEntranceOnStaggeredEntranceAndReducedMotion(string viewFile, string containerName)
    {
        var code = CodeBehind(viewFile);
        code.Should().Contain("StaggeredEntrance.ShouldPlay");
        code.Should().Contain("vm.Shell.IsReducedMotion");
        code.Should().Contain("OnAttachedToVisualTree");
        code.Should().Contain($"{containerName}.Classes.Add(StaggeredEntrance.Class)");
        code.Should().MatchRegex($@"StaggeredEntrance\.ShouldPlay\(nameof\({viewFile}\),",
            "the once-per-process gate key must be this view's own name, or it would share a slot with another view's gate");
    }

    /// <summary>
    /// The container's own entrance style blocks, from the nth-child selector to its closing
    /// tag. Both the RenderTransform guard and the budget check iterate these rather than the
    /// whole file, so unrelated styles elsewhere in the view cannot be miscounted or mis-flagged.
    /// </summary>
    private static MatchCollection EntranceStyleBlocks(string xaml, string containerName)
        => Regex.Matches(xaml,
            $@"StackPanel#{containerName}\.entrance > :is\(Control\):nth-child\(\d+\)"">[\s\S]*?</Style>");

    private static int NormaliseToMilliseconds(string fractionalSecondsDigits)
    {
        // "0:0:0.175" -> 175ms; "0:0:0.025" -> 25ms. The regex group captures the digits after the
        // decimal point verbatim (3 digits for millisecond-precision Avalonia TimeSpan literals).
        var padded = fractionalSecondsDigits.PadRight(3, '0');
        return int.Parse(padded);
    }

    private static string Markup(string viewFile)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", $"{viewFile}.axaml"));

    private static string CodeBehind(string viewFile)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", $"{viewFile}.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
