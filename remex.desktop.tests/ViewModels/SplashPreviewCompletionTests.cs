using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins <see cref="ShellViewModel.ResolveBootCompletion"/> (RemEx-8twk0.8 fix round, HIGH).
/// <see cref="ShellViewModel"/> cannot be constructed headlessly - it needs the full DI graph and a
/// pumped dispatcher, the same reason <see cref="ShellPresencePulseTests"/> avoids doing so - so the
/// decision <c>OnBootSequenceCompleted</c> makes is pulled into this pure, static seam instead.
/// </summary>
public class SplashPreviewCompletionTests
{
    /// <summary>
    /// The case that fails today without the fix: a Splash Preview replay (fired from the
    /// Personalize sheet) completing on a profile that has not persisted <c>HasCompletedTutorial</c> -
    /// e.g. a fresh profile that dismissed the first-run tutorial with Escape (<c>DismissOverlays</c>
    /// closes it WITHOUT persisting) and then opened Personalize and hit Preview. Without the preview
    /// flag, this reads exactly like a genuine first run and raises the full onboarding overlay again,
    /// on top of the sheet.
    /// </summary>
    [Theory]
    [InlineData(true, false, ShellViewModel.BootCompletionAction.UnmountOnly)]
    [InlineData(false, false, ShellViewModel.BootCompletionAction.ShowTutorial)]
    [InlineData(false, true, ShellViewModel.BootCompletionAction.Normal)]
    public void ResolvesTheCorrectActionForEachCombination(
        bool isSplashPreview, bool hasCompletedTutorial, ShellViewModel.BootCompletionAction expected)
    {
        var action = ShellViewModel.ResolveBootCompletion(isSplashPreview, hasCompletedTutorial);

        action.Should().Be(expected);
    }

    /// <summary>
    /// Proves the theory above actually bites: a seam that ignored <c>isSplashPreview</c> entirely
    /// would fail the first case, since it would fall through to <c>ShowTutorial</c> just like the
    /// bug being fixed here.
    /// </summary>
    [Fact]
    public void IgnoringThePreviewFlagWouldReproduceTheBug()
    {
        ShellViewModel.BootCompletionAction ResolveIgnoringPreview(bool hasCompletedTutorial) =>
            hasCompletedTutorial
                ? ShellViewModel.BootCompletionAction.Normal
                : ShellViewModel.BootCompletionAction.ShowTutorial;

        var buggyResult = ResolveIgnoringPreview(hasCompletedTutorial: false);
        var fixedResult = ShellViewModel.ResolveBootCompletion(isSplashPreview: true, hasCompletedTutorial: false);

        buggyResult.Should().Be(ShellViewModel.BootCompletionAction.ShowTutorial);
        fixedResult.Should().Be(ShellViewModel.BootCompletionAction.UnmountOnly);
        fixedResult.Should().NotBe(buggyResult);
    }
}
