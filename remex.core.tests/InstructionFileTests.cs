using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Remex.Core.Tests;

/// <summary>
/// The agent instructions are one hand-written file, <c>.claude/CLAUDE.md</c>, with no generated
/// blocks in it (RemEx-wcvck).
/// </summary>
/// <remarks>
/// <para>
/// **TWO TOOLS REGENERATE THE FILES THIS MERGE DELETED, AND NEITHER ONE SAYS SO.** A bare
/// <c>npx gitnexus analyze</c> (no <c>--skip-agents-md</c>) recreates a root <c>AGENTS.md</c> and a
/// root <c>CLAUDE.md</c>, each carrying a generated block that says to run <c>impact</c> before
/// every edit. <c>bd setup claude</c> writes a beads block into a root <c>CLAUDE.md</c>. Claude Code
/// loads a root <c>CLAUDE.md</c> alongside <c>.claude/CLAUDE.md</c>, so the contradicting text would
/// be back in every session, silently.
/// </para>
/// <para>
/// This replaces <c>GeneratedDocBlockTests</c>, which kept hand-written guidance out of the gitnexus
/// block while the two files still carried one (RemEx-thwlr). With no generated block left, the
/// invariant is simpler: the block must not come back at all.
/// </para>
/// </remarks>
public class InstructionFileTests
{
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    private static string InstructionFile => Path.Combine(RepoRoot(), ".claude", "CLAUDE.md");

    /// <summary>Markers that only ever appear inside a tool-generated block.</summary>
    public static TheoryData<string> GeneratedMarkers() => new()
    {
        "<!-- gitnexus:start -->",
        "<!-- BEGIN BEADS INTEGRATION",
        "<!-- AUTO-MANAGED:",
    };

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    public void NoInstructionFileIsRegeneratedAtTheRepoRoot(string name)
    {
        Assert.False(
            File.Exists(Path.Combine(RepoRoot(), name)),
            $"{name} is back at the repo root. A tool regenerated it: `npx gitnexus analyze` without " +
            "--skip-agents-md, or `bd setup claude`. Delete it; the rules live in .claude/CLAUDE.md.");
    }

    [Theory]
    [MemberData(nameof(GeneratedMarkers))]
    public void TheInstructionFileCarriesNoGeneratedBlock(string marker)
    {
        // ANTI-VACUITY FIRST. A moved or emptied file would contain no marker and pass for ever.
        Assert.True(File.Exists(InstructionFile), ".claude/CLAUDE.md moved or was renamed");
        var doc = File.ReadAllText(InstructionFile);
        Assert.Contains("## Hard rules", doc, StringComparison.Ordinal);

        // A marker counts only where it opens its own line. Prose that names a marker in backticks
        // must not trip this.
        var generated = Regex.IsMatch(doc, $@"^{Regex.Escape(marker)}", RegexOptions.Multiline);
        Assert.False(
            generated,
            $".claude/CLAUDE.md contains a generated block ({marker}). Remove it: this file is " +
            "hand-maintained, and the generated text contradicts it.");
    }

    [Fact]
    public void TheHandWrittenBeadSplittingRulesSurvive()
    {
        // The rule most likely to be trimmed away as "just an essay". It is load-bearing: five
        // stranded logic halves before it was written down (RemEx-hev1g).
        var doc = File.ReadAllText(InstructionFile);
        Assert.Contains("## Splitting beads", doc, StringComparison.Ordinal);
        Assert.Contains("Put the join in the first half", doc, StringComparison.Ordinal);
    }
}
