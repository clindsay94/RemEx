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

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
