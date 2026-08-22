using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the two files that carry this product's version number to the same value (RemEx-ixfbn).
/// </summary>
/// <remarks>
/// <para>
/// THE TWO HEADS SHARE ONE VERSION, AND ONLY ONE OF THEM IS THE SOURCE. <c>build-remex.ps1</c>
/// reads <c>versionName</c> out of <c>remex.android/app/version.properties</c> and then rewrites
/// <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c> to match it. That makes Android the source
/// of truth and the desktop the copy, which is fine — what is not fine is that the copy is a
/// TRACKED file. Editing it directly looks like it worked, survives review, survives a commit, and
/// is then silently undone by the next full build with a cheerful green
/// "Synchronized Directory.Build.props to version 2.4.0".
/// </para>
/// <para>
/// That is exactly what happened: 3ead2fe bumped the desktop to 2.5.0 and deliberately left Android
/// alone, because a <c>versionCode</c> bump implies a Play submission and is the operator's call.
/// The right instinct left the two files disagreeing, with a script that resolves the disagreement
/// in favour of the stale one — so the bump was already reverted in the working tree before anyone
/// noticed. No build failed, no test failed, and the About page would simply have gone back a
/// release.
/// </para>
/// <para>
/// A version bump is a rare, deliberate act, so nothing else in the suite is watching this seam.
/// These tests are the watch: they fail on the DISAGREEMENT itself, before a build gets the chance
/// to resolve it invisibly.
/// </para>
/// </remarks>
public sealed class VersionSourceOfTruthTests
{
    [Fact]
    public void TheDesktopVersionMatchesTheAndroidVersionName()
    {
        var desktop = DesktopVersion();
        var android = AndroidVersionName();

        // ANTI-VACUITY FIRST, and this is the whole reason the two reads are asserted before they
        // are compared. Both helpers return null when their pattern does not match, and null equals
        // null — so a renamed element or a reformatted properties file would turn this test green at
        // precisely the moment it lost the ability to see anything at all.
        desktop.Should().MatchRegex(@"^\d+\.\d+\.\d+$",
            "Directory.Build.props must carry a plain three-part <Version>, or this test is reading "
            + "something other than the version");
        android.Should().MatchRegex(@"^\d+\.\d+\.\d+$",
            "version.properties must carry a plain three-part versionName, since Android's "
            + "versionName cannot express a prerelease or a four-part version");

        desktop.Should().Be(android,
            "build-remex.ps1 rewrites <Version> from versionName on every full build, so a desktop "
            + "version that differs is not a second opinion — it is a value with a countdown on it");
    }

    [Fact]
    public void TheBuildScriptStillSynchronisesTheDesktopFromAndroid()
    {
        // The parity assertion above says nothing about WHICH file wins, so on its own it can be
        // satisfied by deleting the synchronisation entirely and letting the two heads drift under
        // their own steam. That is a real option (it is option (c) on this bead) but it is a
        // decision, not a side effect — and taking it means this test has to be rewritten, which is
        // the point of pinning the direction here.
        //
        // COMMENTS STRIPPED FIRST. build-remex.ps1 explains this block in prose directly above it,
        // so a grep of the raw file matches the explanation of the code as readily as the code, and
        // would keep passing after the replacement itself was removed (the BuildIdTests lesson).
        var script = StripPowerShellComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "build-remex.ps1")));

        // ONE REGEX OVER THE WHOLE STATEMENT, not two independent Contains. Two were tried first and
        // both were reachable without the synchronisation: "version.properties" is held alive on its
        // own by the Test-Path guard and its Write-Error message ~120 lines earlier, and a bare
        // "<Version>" survives in any trailing comment — which StripPowerShellComments does not
        // remove, since it only takes <# #> blocks and whole-line #. Requiring the element and the
        // $Version variable inside a single -replace is what makes deleting the write turn this red.
        script.Should().MatchRegex(
            @"-replace\s*'[^']*<Version>[^']*'\s*,\s*""[^""]*<Version>\$Version</Version>[^""]*""",
            "the desktop version has to be WRITTEN into Directory.Build.props from $Version, or the "
            + "desktop head is versioned by nothing and the parity test above guards a coincidence");

        script.Should().Contain("version.properties",
            "and $Version itself has to be READ from Android's file for it to be the source of truth");
    }

    /// <summary>Reads <c>&lt;Version&gt;</c> from <c>Directory.Build.props</c>, or null.</summary>
    private static string? DesktopVersion()
    {
        var match = Regex.Match(
            File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props")),
            @"<Version>([^<]*)</Version>");

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>Reads <c>versionName</c> from Android's <c>version.properties</c>, or null.</summary>
    /// <remarks>
    /// Deliberately parsed the same shape the Gradle script does — a line-oriented
    /// <c>key=value</c> lookup — rather than with a properties reader, so a file this test calls
    /// well-formed is one the build also calls well-formed.
    /// </remarks>
    private static string? AndroidVersionName()
    {
        var path = Path.Combine(RepoRoot(), "remex.android", "app", "version.properties");

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("versionName=", System.StringComparison.Ordinal))
            {
                return trimmed["versionName=".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>Removes <c>#</c> line comments and <c>&lt;# … #&gt;</c> blocks from a script.</summary>
    private static string StripPowerShellComments(string script)
    {
        var withoutBlocks = Regex.Replace(script, @"<#.*?#>", string.Empty, RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"(?m)^\s*#.*$", string.Empty);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
