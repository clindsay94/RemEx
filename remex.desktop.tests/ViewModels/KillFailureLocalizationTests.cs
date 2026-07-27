using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Every code the host can send has a branch here — checked against the declaration file, not
    /// against a hand-written list.
    /// </summary>
    /// <remarks>
    /// Android has had this guard since RemEx-r37a; the PC had only the hand-authored
    /// <c>[InlineData]</c> cases above, which can prove the mappings that EXIST do not fall through
    /// but can never notice a code that was never added to them. That asymmetry was flagged in
    /// review and is what this closes (RemEx-v1is).
    /// <para>
    /// The failure it guards is quiet rather than loud: <see cref="TaskManagerViewModel"/>'s switch
    /// ends in a default arm that falls back to the host's English, so a missing branch is a missing
    /// TRANSLATION, not a crash or a raw code on screen. That is the safe direction, and it is
    /// exactly why nothing would otherwise report it.
    /// </para>
    /// <para>
    /// The check is "the result differs from the English fallback". A code with no branch returns
    /// the fallback verbatim, so a distinctive sentinel is passed as the fallback rather than
    /// realistic English — realistic text could coincide with a real translation and let a gap pass.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDeclaredCode_HasAMappingRatherThanFallingThrough()
    {
        const string Sentinel = "!!UNMAPPED-FALLBACK-SENTINEL!!";

        var declared = DeclaredCodes();
        declared.Should().NotBeEmpty(
            "the regex must still match ProcessKillErrorCodes.cs, or this test passes vacuously");

        var unmapped = declared
            .Where(code => TaskManagerViewModel.LocalizeKillFailure(
                ProcessKillErrorCodes.Format(code, Sentinel, arg: "probe")) == Sentinel)
            .ToList();

        unmapped.Should().BeEmpty(
            "a code with no arm in LocalizeKillFailure reaches the user as the host's English " +
            "instead of a translated sentence, and nothing else reports it");
    }

    /// <summary>
    /// Every declared code must also resolve to a real resource, not to its own key name.
    /// </summary>
    /// <remarks>
    /// Separate from the mapping check because they fail for different reasons and one masks the
    /// other: a branch can exist and still show rubbish if the <c>.resx</c> entry is missing, since
    /// <c>LocalizationService</c>'s indexer ends in <c>?? key</c> and renders the developer string.
    /// RemEx-2s91 shipped exactly that.
    /// </remarks>
    [Fact]
    public void NoDeclaredCode_RendersAResourceKeyName()
    {
        foreach (var code in DeclaredCodes())
        {
            var shown = TaskManagerViewModel.LocalizeKillFailure(
                ProcessKillErrorCodes.Format(code, "fallback", arg: "probe"));

            shown.Should().NotStartWith("TaskManager_",
                $"code '{code}' resolved to a resource key name, which means the .resx entry is missing");
        }
    }

    /// <summary>Codes as the host declares them, parsed from the C# source rather than mirrored.</summary>
    private static List<string> DeclaredCodes()
    {
        var path = Path.Combine(RepoRoot(), "remex.core", "Models", "ProcessKillErrorCodes.cs");
        File.Exists(path).Should().BeTrue($"the declaration file must be readable at {path}");

        return Regex.Matches(File.ReadAllText(path), @"public\s+const\s+string\s+\w+\s*=\s*""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    /// <summary>
    /// The repository root, resolved from THIS source file rather than from the test assembly.
    /// </summary>
    /// <remarks>
    /// Walking up from <c>AppContext.BaseDirectory</c> couples the test to build output living
    /// inside the repo, so building with <c>--artifacts-path</c> elsewhere breaks it with an error
    /// that says nothing about the change that caused it — see RemEx-6i1l, where exactly that
    /// happened.
    /// </remarks>
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        // <repo>/remex.desktop.tests/ViewModels/ThisFile.cs -> <repo>
        var directory = Path.GetDirectoryName(thisSourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
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
