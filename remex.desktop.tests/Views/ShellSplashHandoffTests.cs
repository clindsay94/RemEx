using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the splash-to-shell handoff and the Material restyle of the first-run tutorial overlay
/// (RemEx-72s7l). Source-scan only - this repo has no headless render, so these assertions are
/// text-level proxies for "no visible cut or colour jump" and "readable on every preset".
/// </summary>
public class ShellSplashHandoffTests
{
    [Fact]
    public void BootSplash_CrossfadesInsteadOfCutting()
    {
        var markup = ShellMarkup();
        var match = Regex.Match(markup,
            @"<splash:SkiaSplashControl\b[^>]*x:Name=""BootSplash""[^>]*>.*?</splash:SkiaSplashControl>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("BootSplash has to still exist as a named, closed element");

        var element = match.Value;
        element.Should().Contain("IsVisible=\"{Binding IsWelcomeSplashMounted}\"",
            "BootSplash must stay mounted through its fade instead of vanishing the instant ShowWelcomeSplash flips");
        element.Should().Contain("Opacity=\"{Binding ShowWelcomeSplash,",
            "the fade itself is driven by ShowWelcomeSplash's opacity, not a hard cut");
        element.Should().MatchRegex(@"<DoubleTransition\s+Property=""Opacity""",
            "the opacity change must animate rather than snap");
    }

    [Fact]
    public void ShellViewModel_DeclaresMountedFlagAndKeepsBootSequenceHook()
    {
        var source = ShellViewModelSource();
        source.Should().Contain("_isWelcomeSplashMounted",
            "the crossfade needs a mount flag distinct from ShowWelcomeSplash");
        source.Should().Contain("public void OnBootSequenceCompleted()");
        source.Should().Contain("ShowWelcomeSplash = false;",
            "OnBootSequenceCompleted must still be the thing that starts the fade-out");
    }

    [Fact]
    public void TutorialOverlay_IsRestyledOnMaterialSurfaces()
    {
        var block = TutorialOverlayBlock();

        block.Should().Contain("material:Card",
            "the tutorial card must be a real Material Card, not a plain Border pretending to be one");
        block.Should().NotContain("GlassBaseDarkBrush",
            "every surface in the tutorial block must come from the seeded palette, not the old glass brush");
        block.Should().NotMatchRegex(@"FontSize=""22""",
            "page titles must ride the Headline5 type-scale key instead of an inline literal");
        block.Should().Contain("{DynamicResource OverlayBackdropBrush}",
            "the scrim behind the card stays the documented seeded backdrop brush");

        block.Should().Contain("Classes=\"tertiary compact\"", "Skip must use the tertiary button class");
        block.Should().Contain("Classes=\"secondary pill\"", "Back must use the secondary button class");
        block.Should().Contain("Classes=\"primary pill\"", "Next must use the primary button class");
        block.Should().Contain("Classes=\"primary pill success\"", "Finish must use the primary+success button class");
    }

    private static string TutorialOverlayBlock()
    {
        var markup = ShellMarkup();
        var startIndex = markup.IndexOf("TUTORIAL OVERLAY");
        startIndex.Should().BeGreaterThan(-1, "the tutorial overlay comment marker must still exist");
        return markup[startIndex..];
    }

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string ShellViewModelSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
