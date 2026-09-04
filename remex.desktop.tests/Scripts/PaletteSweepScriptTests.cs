using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Scripts;

/// <summary>
/// scripts/ui-palette-sweep.ps1 (RemEx-8q7de) is PowerShell, so nothing here can compile-check
/// it — these are text assertions over the source, the same shape .NET already uses elsewhere in
/// this repo for things a build cannot verify (see <c>CustomizationSettingsRoundTripTests</c>).
/// They exist to catch the two ways this script could quietly stop doing its job: losing the
/// backup/restore/no-live-host safety net, or losing an axis of the matrix it claims to sweep.
/// </summary>
/// <remarks>
/// Several assertions are anchored to <see cref="MatrixDataBlock"/> / <see cref="TryBlockBody"/> /
/// <see cref="FinallyBlockBody"/> rather than the whole file, specifically because the header
/// comment restates the matrix and the safety story in prose (for a human skimming the script) —
/// text that stays behind even if the code it describes is deleted. A whole-file
/// <c>Contains</c> check on those same words would keep passing after exactly the regression it
/// claims to catch (RemEx-8q7de round 2 review).
/// </remarks>
public class PaletteSweepScriptTests
{
    private static string ScriptPath() =>
        Path.Combine(RepoRoot(), "scripts", "ui-palette-sweep.ps1");

    private static string ScriptText() => File.ReadAllText(ScriptPath());

    [Fact]
    public void ScriptExistsAndIsNotEmpty()
    {
        File.Exists(ScriptPath()).Should().BeTrue("the sweep script must be tracked at scripts/ui-palette-sweep.ps1");
        ScriptText().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MatrixHasExactlyThirteenCells()
    {
        // Negative lookbehind for a letter so "ThemeId = 'BaseDarkGlass'" isn't double-counted as
        // an "Id = " row alongside the actual "Id = 'Default'" it sits next to.
        var idRows = Regex.Matches(MatrixDataBlock(), @"(?<![A-Za-z])Id\s*=\s*'[^']+'");
        idRows.Should().HaveCount(13,
            "the axis is 1 default + 3 adversarial seeds x 2 modes x 2 contrasts = 13 - a row added or lost here silently shrinks or pads the sweep");
    }

    [Fact]
    public void MatrixCoversBothThemeModes()
    {
        var matrix = MatrixDataBlock();
        matrix.Should().MatchRegex(@"Mode\s*=\s*'Light'", "the matrix data must include a Light-mode row");
        matrix.Should().MatchRegex(@"Mode\s*=\s*'Dark'", "the matrix data must include a Dark-mode row");
    }

    [Fact]
    public void MatrixIncludesTheShippedDefaultPreset()
    {
        var matrix = MatrixDataBlock();
        matrix.Should().MatchRegex(@"Id\s*=\s*'Default'.*?ThemeId\s*=\s*'BaseDarkGlass'",
            "the Default cell must be the shipped preset (SeedPresetCatalog.BaseDarkGlass), not just another arbitrary seed");
        matrix.Should().Contain("#6C4CFF",
            "the Default cell's seed must be the shipped preset's own seed colour");
    }

    [Fact]
    public void MatrixHasAtLeastThreeDistinctAdversarialSeedHexLiterals()
    {
        // Distinct hex colour literals in the DATA ROWS ONLY (see MatrixDataBlock) - the header
        // comment above the data also spells out Chalk/Ink/Chroma's hex values, so scanning the
        // whole file here would keep passing even if every seed row were deleted.
        var hexLiterals = Regex.Matches(MatrixDataBlock(), @"#[0-9A-Fa-f]{6}")
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct()
            .ToList();

        hexLiterals.Should().HaveCountGreaterThanOrEqualTo(3,
            "the axis is the default preset plus three adversarial seeds (near-white, near-black, max-chroma) - losing one silently shrinks the matrix");
    }

    [Fact]
    public void RestoresTheBackupInAFinallyBlock()
    {
        // A real 'finally {' block statement, not the word merely appearing in a doc comment
        // (the header prose says "restored in a finally block" regardless of whether the code
        // still has one).
        Regex.IsMatch(ScriptText(), @"(?m)^\s*finally\s*\{").Should().BeTrue(
            "the profile restore MUST run in a finally block - a crash mid-sweep must not leave an adversarial palette as the user's real profile");
    }

    [Fact]
    public void FinallyBlockActuallyRestoresTheBackupFromTheRightPath()
    {
        var finallyBody = FinallyBlockBody();
        finallyBody.Should().MatchRegex(@"Copy-Item\s+-Path\s+\$backupPath\s+-Destination\s+\$profilePath",
            "the finally block must actually copy the backup back over the real profile ($backupPath -> $profilePath), not merely exist");
    }

    [Fact]
    public void BacksUpTheProfileBeforeWriting()
    {
        ScriptText().Should().Contain("sweep-backup",
            "the backup/restore/refuse-if-backup-exists contract is built around this exact suffix - dashboard_layout.json.sweep-backup");
    }

    [Fact]
    public void NeverBuildsTheHost()
    {
        var text = ScriptText();

        // Every actual invocation of ui-hotreload.ps1 with '-Start' must carry '-NoBuild' - a
        // build mid-sweep can lock the very DLLs a running host holds (ui-hotreload.ps1's own
        // MSB3026 note) and, worse, silently screenshot a stale binary if it fails after the
        // process kept running. Anchored to '& $hotReloadScript' so prose in the header comment
        // that merely mentions '-Start' (e.g. "run ... -Start once by hand first") is not a hit.
        var startInvocations = Regex.Matches(text, @"&\s*\$hotReloadScript\s+-Start\b[^\r\n]*");
        startInvocations.Should().NotBeEmpty("the sweep must actually start the host somewhere");
        foreach (Match invocation in startInvocations)
        {
            invocation.Value.Should().Contain("-NoBuild",
                $"found a '-Start' without '-NoBuild': \"{invocation.Value.Trim()}\"");
        }
    }

    /// <summary>
    /// RemEx-8q7de round 2 (CRITICAL): <c>ui-hotreload.ps1 -Stop</c> relaunches the installed
    /// Release build by default, which would auto-connect and read/write the profile the sweep is
    /// mid-write on. Every <c>-Stop</c> INSIDE the main loop must carry <c>-NoRelaunch</c>; only
    /// the one <c>-Stop</c> after the loop, in <c>finally</c>, may relaunch — and only once the
    /// real profile is safely restored.
    /// </summary>
    [Fact]
    public void EveryStopInsideTheSweepLoopCarriesNoRelaunch()
    {
        var tryBody = TryBlockBody();

        var stopInvocations = Regex.Matches(tryBody, @"&\s*\$hotReloadScript\s+-Stop\b[^\r\n]*");
        stopInvocations.Should().NotBeEmpty("the sweep's main loop must actually stop the host somewhere");
        foreach (Match invocation in stopInvocations)
        {
            invocation.Value.Should().Contain("-NoRelaunch",
                $"found a '-Stop' inside the sweep loop without '-NoRelaunch': \"{invocation.Value.Trim()}\" - " +
                "a relaunched Release host would auto-connect and read/write the profile mid-sweep");
        }
    }

    /// <summary>
    /// Everything between the top-level <c>$Script:CellMatrix = @(</c> and its matching closing
    /// <c>)</c> — the DATA, not the header comment above it that restates the same facts in prose.
    /// </summary>
    private static string MatrixDataBlock()
    {
        var match = Regex.Match(ScriptText(), @"\$Script:CellMatrix\s*=\s*@\(\r?\n(.*?)\r?\n\)\r?\n",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("$Script:CellMatrix moved or was reshaped - re-point this test rather than deleting it");
        return match.Groups[1].Value;
    }

    /// <summary>The body of the top-level <c>try { ... }</c> that precedes <c>finally</c>.</summary>
    private static string TryBlockBody()
    {
        var match = Regex.Match(ScriptText(), @"(?m)^try\s*\{\r?\n(.*?)\r?\n^\}\r?$",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("expected a top-level 'try { ... }' block before 'finally' - re-point this test if the script's shape changed");
        return match.Groups[1].Value;
    }

    /// <summary>The body of the top-level <c>finally { ... }</c> block.</summary>
    private static string FinallyBlockBody()
    {
        var match = Regex.Match(ScriptText(), @"(?m)^finally\s*\{\r?\n(.*?)\r?\n^\}\r?$",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("expected a top-level 'finally { ... }' block - re-point this test if the script's shape changed");
        return match.Groups[1].Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
