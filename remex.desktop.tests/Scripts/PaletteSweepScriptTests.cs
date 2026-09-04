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
/// backup/restore safety net, or losing an axis of the matrix it claims to sweep.
/// </summary>
public class PaletteSweepScriptTests
{
    private static string ScriptPath() =>
        Path.Combine(RepoRoot(), "scripts", "ui-palette-sweep.ps1");

    [Fact]
    public void ScriptExistsAndIsNotEmpty()
    {
        File.Exists(ScriptPath()).Should().BeTrue("the sweep script must be tracked at scripts/ui-palette-sweep.ps1");
        var text = File.ReadAllText(ScriptPath());
        text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MatrixCoversBothThemeModes()
    {
        var text = File.ReadAllText(ScriptPath());
        text.Should().Contain("Light");
        text.Should().Contain("Dark");
    }

    [Fact]
    public void MatrixIncludesTheShippedDefaultPreset()
    {
        var text = File.ReadAllText(ScriptPath());
        (text.Contains("#6C4CFF") || text.Contains("BaseDarkGlass")).Should().BeTrue(
            "the sweep's Default cell must be the shipped preset (SeedPresetCatalog.BaseDarkGlass), not just another arbitrary seed");
    }

    [Fact]
    public void MatrixHasAtLeastThreeDistinctAdversarialSeedHexLiterals()
    {
        var text = File.ReadAllText(ScriptPath());

        // Distinct hex colour literals anywhere in the script, e.g. '#6C4CFF', '#F5F5F5'.
        var hexLiterals = Regex.Matches(text, @"#[0-9A-Fa-f]{6}")
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct()
            .ToList();

        hexLiterals.Should().HaveCountGreaterThanOrEqualTo(3,
            "the axis is the default preset plus three adversarial seeds (near-white, near-black, max-chroma) - losing one silently shrinks the matrix");
    }

    [Fact]
    public void RestoresTheBackupInAFinallyBlock()
    {
        var text = File.ReadAllText(ScriptPath());

        // A real 'finally {' block statement, not the word merely appearing in a doc comment
        // (the header prose says "restored in a finally block" regardless of whether the code
        // still has one).
        Regex.IsMatch(text, @"(?m)^\s*finally\s*\{").Should().BeTrue(
            "the profile restore MUST run in a finally block - a crash mid-sweep must not leave an adversarial palette as the user's real profile");
    }

    [Fact]
    public void BacksUpTheProfileBeforeWriting()
    {
        var text = File.ReadAllText(ScriptPath());
        text.Should().Contain("sweep-backup",
            "the backup/restore/refuse-if-backup-exists contract is built around this exact suffix - dashboard_layout.json.sweep-backup");
    }

    [Fact]
    public void NeverBuildsTheHost()
    {
        var text = File.ReadAllText(ScriptPath());

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

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
