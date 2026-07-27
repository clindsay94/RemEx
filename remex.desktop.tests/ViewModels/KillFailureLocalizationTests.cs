using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Covers turning a host "end process" failure into a sentence in the user's language.
/// </summary>
/// <remarks>
/// The host cannot localize these itself: it does not know which language this window is running
/// in, and the phone does not share the PC's <c>.resx</c> files at all. So it sends
/// <c>"code␟arg␟englishFallback"</c> and each client owns the wording (RemEx-r37a), the same shape
/// remote desktop has used since RemEx-728.
/// </remarks>
public class KillFailureLocalizationTests
{
    private static string Coded(string code, string fallback, string? arg = null)
        => ProcessKillErrorCodes.Format(code, fallback, arg);

    [Theory]
    [InlineData(ProcessKillErrorCodes.AccessDenied)]
    [InlineData(ProcessKillErrorCodes.NotRunning)]
    [InlineData(ProcessKillErrorCodes.Failed)]
    public void AKnownCode_IsReplacedByLocalizedText(string code)
    {
        var englishFallback = "Some host English that must not reach the user.";

        var shown = TaskManagerViewModel.LocalizeKillFailure(Coded(code, englishFallback));

        shown.Should().NotBe(englishFallback, "the whole point is that the host's English is replaced");
        shown.Should().NotContain(ProcessKillErrorCodes.Delimiter.ToString(),
            "the wire encoding must never be visible on screen");
        shown.Should().NotBe(code, "a key that resolved to its own name means the .resx entry is missing");
    }

    [Fact]
    public void TheIdentityMismatchArgument_ReachesTheMessage()
    {
        var shown = TaskManagerViewModel.LocalizeKillFailure(
            Coded(ProcessKillErrorCodes.IdentityMismatch, "fallback", "sshd"));

        shown.Should().Contain("sshd",
            "naming what now owns the PID is the difference between an actionable message and " +
            "'something went wrong'");
    }

    /// <summary>
    /// A mismatch with no argument must still say something sensible rather than render an empty
    /// placeholder.
    /// </summary>
    [Fact]
    public void AnIdentityMismatchWithoutAnArgument_FallsBackToTheNotRunningWording()
    {
        var shown = TaskManagerViewModel.LocalizeKillFailure(
            Coded(ProcessKillErrorCodes.IdentityMismatch, "fallback", arg: null));

        shown.Should().NotBeNullOrWhiteSpace();
        shown.Should().NotContain("{0}", "an unsubstituted placeholder is worse than a vaguer sentence");
    }

    /// <summary>
    /// Untagged text from an older host is shown as-is.
    /// </summary>
    /// <remarks>
    /// This is the backward-compatibility hinge in the client direction, and getting it wrong would
    /// be a silent downgrade rather than a crash: every failure reason from an un-updated PC would
    /// be replaced by a generic sentence, losing information the user could have acted on.
    /// </remarks>
    [Fact]
    public void UntaggedHostText_IsPassedThroughUnchanged()
    {
        const string Legacy = "Access denied. Run the host as Administrator or retry with KillProcessElevated.";

        TaskManagerViewModel.LocalizeKillFailure(Legacy).Should().Be(Legacy);
    }

    [Fact]
    public void AnUnknownCode_FallsBackToTheHostsEnglishRatherThanShowingTheCode()
    {
        const string Fallback = "A newer host knows something this client does not.";

        var shown = TaskManagerViewModel.LocalizeKillFailure(Coded("kill_some_future_code", Fallback));

        shown.Should().Be(Fallback,
            "which is why the host must keep sending a complete English sentence, not a stub");
    }
}
