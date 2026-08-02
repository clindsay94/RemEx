using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that the desktop's catch blocks do not swallow an exception without trace (RemEx-43ha).
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT: several catch blocks wrote to <c>Debug.WriteLine</c> or to nothing at all. Debug
/// output exists only under a debugger, so on a user's machine those failures left NO trace
/// anywhere — including in the diagnostics export, which is the artefact a support case is
/// actually built from. An error nobody can see is indistinguishable from an error that did not
/// happen.
/// </para>
/// <para>
/// Asserted on the SOURCE rather than by driving the app, because reproducing the conditions —
/// app initialization failing, a consent dialog throwing, no browser installed — is not something
/// a unit test can arrange. The check is therefore structural: an empty catch is visible in the
/// text, and that is enough to stop one being re-introduced.
/// </para>
/// </remarks>
public class SwallowedErrorLoggingTests
{
    /// <summary>
    /// Files this rule is enforced on. Deliberately a list rather than the whole tree: a sweep would
    /// need every existing empty catch triaged first, and a rule nobody can keep green gets
    /// suppressed rather than obeyed.
    /// </summary>
    public static readonly string[] GuardedFiles =
    [
        Path.Combine("remex.desktop", "App.axaml.cs"),
        Path.Combine("remex.desktop", "ViewModels", "AboutViewModel.cs")
    ];

    [Theory]
    [MemberData(nameof(GuardedFileCases))]
    public void NoCatchBlockIsCompletelyEmpty(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, relativePath));

        // `catch { }` or `catch (X) { }` with nothing but whitespace and comments between the
        // braces. A comment is not a trace: "// Silently fail if browser can't be opened" is what
        // this defect looked like, and it reassured every reader who met it.
        var emptyCatch = new Regex(
            @"catch\s*(\([^)]*\))?\s*\{\s*(//[^\n]*\s*)*\}",
            RegexOptions.Multiline);

        var matches = emptyCatch.Matches(source);

        Assert.True(matches.Count == 0,
            $"{relativePath} has {matches.Count} catch block(s) that record nothing. " +
            "Append to InMemoryLogSink so the failure reaches the diagnostics export.");
    }

    [Fact]
    public void TheGuardedFilesActuallyReachTheInAppLog()
    {
        // The control. Without it, the rule above could be satisfied by a catch block that merely
        // calls Debug.WriteLine - which is exactly the state being fixed, since Debug output never
        // leaves a debugger and so never reaches the export.
        foreach (var relativePath in GuardedFiles)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot, relativePath));

            Assert.Contains("InMemoryLogSink.Append", source);
        }
    }

    public static TheoryData<string> GuardedFileCases()
    {
        var data = new TheoryData<string>();
        foreach (var file in GuardedFiles) data.Add(file);
        return data;
    }

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GetThisFilePath())!, "..", ".."));

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;
}
