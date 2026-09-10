using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the two remaining static live indicators turned continuous (RemEx-s19yc): the Home
/// footer presence halo and the Remote Desktop live-stream dot. Both use a class-gated
/// <c>Style.Animations</c> pulse, matching <see cref="PresenceBadgePulseTests"/> — never
/// <c>MaterialAnimationAssist.ContinuousAnimation</c>, whose Changed handler cannot be stopped by
/// clearing it back to null (bd memory: material-animation-assist-cannot-be-stopped).
/// </summary>
/// <remarks>
/// A source scan — there is no headless Avalonia render in this suite to actually watch either
/// indicator breathe.
/// </remarks>
public class LiveIndicatorPulseTests
{
    [Fact]
    public void HomeHaloHasAnInfinitePulseStyleBoundToShellPresencePulse()
    {
        var markup = HomeViewMarkup();

        markup.Should().MatchRegex(
            @"<Style Selector=""Ellipse\.status-ring\.pulse"">\s*<Style\.Animations>\s*<Animation\b[^>]*IterationCount=""Infinite""",
            "the footer radar halo needs a class-gated pulse that repeats indefinitely while pulsing");

        markup.Should().MatchRegex(
            @"<Ellipse[^>]*Classes=""status-ring""[^>]*Classes\.pulse=""\{Binding Shell\.ShowPresencePulse\}""",
            "the halo's pulse class has to follow ShellViewModel.ShowPresencePulse (phone presence + reduced motion), not a local axis");
    }

    [Fact]
    public void RemoteDesktopLiveDotHasAnInfinitePulseStyleBoundToShowStreamPulse()
    {
        var markup = RemoteDesktopViewMarkup();

        markup.Should().MatchRegex(
            @"<Style Selector=""Border\.live-dot\.pulse"">\s*<Style\.Animations>\s*<Animation\b[^>]*IterationCount=""Infinite""",
            "the live-stream dot needs a class-gated pulse that repeats indefinitely while streaming");

        markup.Should().MatchRegex(
            @"<Border[^>]*Classes=""live-dot""[^>]*Classes\.pulse=""\{Binding ShowStreamPulse\}""",
            "the dot's pulse class has to follow ShowStreamPulse (IsStreaming + reduced motion)");
    }

    [Fact]
    public void NeitherViewUsesTheUnstoppableAnimationAssist()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "remex.desktop", "Views"), "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("MaterialAnimationAssist.ContinuousAnimation",
                $"{Path.GetFileName(file)} must use a class-gated Style.Animations pulse, not the assist whose Changed handler cannot be stopped");
            text.Should().NotContain("MaterialAnimationAssist.ReverseAfterEndAnimation",
                $"{Path.GetFileName(file)} must use a class-gated Style.Animations pulse, not the assist whose Changed handler cannot be stopped");
        }
    }

    [Fact]
    public void RemoteDesktopViewModelComputesShowStreamPulseFromStreamingAndReducedMotion()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "RemoteDesktopViewModel.cs"));

        source.Should().MatchRegex(
            @"ShowStreamPulse\s*=>\s*IsStreaming\s*&&\s*!_shell\.IsReducedMotion",
            "the live dot must only pulse while actually streaming and reduced motion is off");
    }

    private static string HomeViewMarkup([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RemoteDesktopViewMarkup([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "Views", "RemoteDesktopView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
