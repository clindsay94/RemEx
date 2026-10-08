using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Every source file build-remex.ps1 names under the repo root exists with exactly that case (hard rule 4). The
/// script asked for <c>RemEx.sln</c> while the file is <c>Remex.sln</c>: Windows found it anyway, and every PC
/// build on Linux died at <c>dotnet restore</c> ("Project file does not exist"), found while cutting 3.1.0.
/// </summary>
public sealed class BuildScriptPathCaseTests
{
    // Folders and files the build creates, or that are machine-local; they need not exist in a clean checkout.
    private static readonly string[] Generated = ["artifacts", "build_output", "installer/Output", "remex.android/local.properties"];

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Fact]
    public void EveryRepoPathTheBuildScriptNames_ExistsWithThatExactCase()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "build-remex.ps1"));
        var paths = Regex.Matches(script, @"Join-Paths \$RepoRoot((?:\s+""[^""$]*"")+)(?!\s+""[^""]*\$)")
            .Select(m => string.Join("/", Regex.Matches(m.Groups[1].Value, @"""([^""]*)""").Select(g => g.Groups[1].Value)))
            .Distinct()
            .Where(p => !Generated.Any(g => p == g || p.StartsWith(g + "/", StringComparison.Ordinal)))
            .ToList();

        paths.Should().Contain(["Remex.sln", "Directory.Build.props", "installer/RemEx.iss"],
            "the scan must still see the paths it exists to check");

        var wrong = paths.Where(p => !ExistsWithExactCase(RepoRoot(), p)).ToList();
        wrong.Should().BeEmpty("a path whose case differs from the file works on Windows and fails on Linux");
    }

    /// <summary>Walks each segment and compares names ordinally, so the answer is the same on Windows and Linux.</summary>
    private static bool ExistsWithExactCase(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            if (!Directory.Exists(current)) return false;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), segment, StringComparison.Ordinal));
            if (match is null) return false;
            current = match;
        }
        return true;
    }
}
