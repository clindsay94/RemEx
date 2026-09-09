using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Scripts;

/// <summary>
/// scripts/verify.ps1 (RemEx-0tui6) is PowerShell, so nothing here can compile-check it — this is
/// a text assertion over the source, the same idiom used for <see cref="PaletteSweepScriptTests"/>.
/// It pins the dotnet scope's fingerprint patterns in <c>Get-ScopePatterns</c>: an .axaml-only or a
/// scripts/*.ps1-only edit must invalidate the verify receipt, because Avalonia compiles XAML into
/// the assembly and remex.desktop.tests scrapes scripts/*.ps1 as source (see
/// <see cref="PaletteSweepScriptTests"/> and friends). '*.psd1' must stay OUT of the same list —
/// fingerprinting it would invalidate every receipt whenever the ralph loop config changes, which
/// is unrelated to whether the code still verifies (see the .ralph.psd1 header).
/// </summary>
public class VerifyScriptFingerprintTests
{
    private static string ScriptPath() =>
        Path.Combine(RepoRoot(), "scripts", "verify.ps1");

    private static string ScriptText() => File.ReadAllText(ScriptPath());

    [Fact]
    public void ScriptExistsAndIsNotEmpty()
    {
        File.Exists(ScriptPath()).Should().BeTrue("the verify script must be tracked at scripts/verify.ps1");
        ScriptText().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DotnetScopeFingerprintsXamlAndPowerShellSourceButNotRalphConfig()
    {
        var dotnetPatterns = DotnetPatternsBlock();

        dotnetPatterns.Should().Contain("'*.axaml'",
            "Avalonia compiles .axaml into the assembly - a XAML-only edit is a source change the receipt must catch");
        dotnetPatterns.Should().Contain("'*.ps1'",
            "remex.desktop.tests scrapes scripts/*.ps1 as source (PaletteSweepScriptTests and friends) - a script-only edit is a source change the receipt must catch");
        dotnetPatterns.Should().NotContain("'*.psd1'",
            "fingerprinting .ralph.psd1 would invalidate every receipt whenever the ralph loop config changes, which the .ralph.psd1 header explains is deliberately out of scope here");
    }

    /// <summary>Everything between <c>$dotnet = @(</c> and its matching closing <c>)</c>.</summary>
    private static string DotnetPatternsBlock()
    {
        var match = Regex.Match(ScriptText(), @"\$dotnet\s*=\s*@\(\r?\n(.*?)\r?\n    \)\r?\n",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("$dotnet moved or was reshaped in Get-ScopePatterns - re-point this test rather than deleting it");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// RemEx-kou55: a stale JAVA_HOME made Gradle die at JVM init, and the Android leg reported
    /// it as "Android unit tests failed" - which quarantined desktop-only beads. The guard must
    /// check the dead path BEFORE either Gradle invocation, not after.
    /// </summary>
    [Fact]
    public void AndroidLegGuardsAgainstDeadJavaHomeBeforeInvokingGradle()
    {
        var text = ScriptText();

        var firstGradlewCall = text.IndexOf("& $gradlew", StringComparison.Ordinal);
        firstGradlewCall.Should().BeGreaterThan(-1, "the script no longer invokes $gradlew - re-point this test rather than deleting it");

        var javaHomeCheck = Regex.Match(text,
            @"env:JAVA_HOME.*?Test-Path\s+-LiteralPath\s+\$env:JAVA_HOME|Test-Path\s+-LiteralPath\s+\$env:JAVA_HOME");
        javaHomeCheck.Success.Should().BeTrue("no Test-Path guard on $env:JAVA_HOME found - the dead-JAVA_HOME guard was removed");
        javaHomeCheck.Index.Should().BeLessThan(firstGradlewCall,
            "the JAVA_HOME guard must run before Gradle is invoked, otherwise Gradle dies at JVM init with the failure mis-attributed to 'Android unit tests failed'");

        text.Should().Contain("JAVA_HOME points at a missing directory",
            "the problem string must name JAVA_HOME, not report a generic Android test failure");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
