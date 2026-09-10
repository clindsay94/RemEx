using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Remex.Core.Tests;

/// <summary>
/// Hand-written guidance does not live inside a generated block (RemEx-thwlr).
/// </summary>
/// <remarks>
/// <para>
/// **THE "SPLITTING A BEAD" RULES WERE FOUND INSIDE THE GITNEXUS BLOCK.** They are hand-written
/// institutional knowledge — four observed failures, a decision not to automate the check, and the
/// reason a reference-count scan was rejected — and they sat between the generator's markers, where
/// the next <c>npx gitnexus analyze</c> would have overwritten them. Confirmed in the generator
/// rather than assumed: with no keep marker it rebuilds the region as <c>before + content + after</c>,
/// so everything between the markers is destroyed. Nothing would have failed. The file would simply
/// have got shorter, and the next agent to split a bead would not have been told any of it.
/// </para>
/// <para>
/// This is the failure mode <c>CLAUDE.md</c> already describes for the other generated block: an
/// auto-generated section of <c>AGENTS.md</c> drifted out of sync with the code and, in one case,
/// told agents to do the exact opposite of what the code does. That was solved by moving the content
/// to a hand-maintained file; this is the same problem from the other side — keeping hand-written
/// text out of the generated region — and it is checkable, so it is checked rather than remembered.
/// </para>
/// <para>
/// The rule is deliberately narrow: the block must BEGIN with the generator's own H1. Nothing is
/// asserted about what follows, because that is the generator's business and changes on every
/// reindex.
/// </para>
/// </remarks>
public class GeneratedDocBlockTests
{
    private const string StartMarker = "<!-- gitnexus:start -->";
    private const string EndMarker = "<!-- gitnexus:end -->";

    /// <summary>
    /// The generator's H1, matched by PREFIX.
    /// </summary>
    /// <remarks>
    /// The full heading today is <c>"# GitNexus — Code Intelligence"</c>, but the suffix is
    /// decoration the generator owns and may retitle. Anchoring on the whole sentence would turn a
    /// routine version bump into a red build for a tree that is perfectly correct — a guard failing
    /// for a reason unrelated to the regression it exists for. The prefix still excludes every piece
    /// of hand-written text that could plausibly land here, and it keeps a non-ASCII character out of
    /// the comparison as a side benefit.
    /// </remarks>
    private const string GeneratedHeadingPrefix = "# GitNexus";

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    /// <summary>Both docs the generator writes. It rebuilds them from one template on every run.</summary>
    public static TheoryData<string> GeneratedDocs() => new() { "AGENTS.md", "CLAUDE.md" };

    private static string ReadDoc(string name)
    {
        var path = Path.Combine(RepoRoot(), name);
        Assert.True(File.Exists(path), $"{name} moved or was renamed");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Finds a marker only where it stands ALONE on its line.
    /// </summary>
    /// <remarks>
    /// **A PLAIN <c>IndexOf</c> WOULD MATCH THE MARKER INSIDE PROSE, AND THIS CHANGE INVITES EXACTLY
    /// THAT PROSE.** <c>CLAUDE.md</c> already names the marker inline while explaining the block, and
    /// the whole point of the guidance being added here is to get future agents talking about these
    /// markers by name. One sentence quoting one in backticks, anywhere above the real block, and a
    /// naive scan resolves to the sentence: the assertions then describe a file that is perfectly
    /// clean and send someone to move content that never moved.
    /// <para>
    /// The generator hit this same bug and fixed it the same way, so this mirrors its rule rather
    /// than inventing one.
    /// </para>
    /// </remarks>
    private static int IndexOfMarkerLine(string doc, string marker, int from = 0)
    {
        var match = Regex.Match(
            doc[from..],
            $@"^{Regex.Escape(marker)}[ \t]*\r?$",
            RegexOptions.Multiline);

        return match.Success ? from + match.Index : -1;
    }

    [Theory]
    [MemberData(nameof(GeneratedDocs))]
    public void TheGeneratedBlockContainsNothingButGeneratedContent(string docName)
    {
        var doc = ReadDoc(docName);

        var start = IndexOfMarkerLine(doc, StartMarker);
        var end = IndexOfMarkerLine(doc, EndMarker, Math.Max(start, 0));

        // ANTI-VACUITY FIRST. If the markers are ever renamed, everything below would compare
        // nothing to nothing and pass for ever - the exact way a source-scanning guard goes quiet,
        // and the reason this repo's scans carry a check like this before the real one.
        Assert.True(start >= 0, $"{docName}: the gitnexus:start marker is gone - has the generator changed?");
        Assert.True(end > start, $"{docName}: the gitnexus:end marker is gone or precedes the start");

        var block = doc[(start + StartMarker.Length)..end].TrimStart('\r', '\n');

        Assert.True(
            block.StartsWith(GeneratedHeadingPrefix, StringComparison.Ordinal),
            $"{docName} has hand-written text inside the gitnexus block. `npx gitnexus analyze` " +
            "rebuilds everything between those markers, so it will be deleted without warning " +
            "unless the section carries a `<!-- gitnexus:keep -->` marker. Move it ABOVE " +
            $"gitnexus:start. Expected the block to begin with \"{GeneratedHeadingPrefix}\"; it " +
            $"begins: {block[..Math.Min(120, block.Length)]}");
    }

    [Fact]
    public void TheTwoGeneratedBlocksAreIdentical()
    {
        // THE ONE THAT CATCHES CONTAMINATION ANYWHERE IN THE BLOCK, not just at the top. The test
        // above is a StartsWith, so hand-written text APPENDED at the end of the block - visually
        // the most natural place to append - sails past it. That was found by mutating for it and
        // watching nothing go red, which is the only way this kind of gap ever shows up.
        //
        // The invariant is free and needs no knowledge of what the generator emits: it writes both
        // files from ONE template in a single run, so the two blocks are byte-identical. Anything
        // hand-written in either one breaks that, wherever it sits. It cannot catch the same text
        // added to both, which nobody does by accident.
        var blocks = new List<string>();
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            var doc = ReadDoc(name);
            var start = IndexOfMarkerLine(doc, StartMarker);
            var end = IndexOfMarkerLine(doc, EndMarker, Math.Max(start, 0));

            Assert.True(start >= 0 && end > start, $"{name}: the gitnexus markers are missing or out of order");
            blocks.Add(doc[(start + StartMarker.Length)..end].Trim());
        }

        // Anti-vacuity: two empty strings are also "identical".
        Assert.NotEmpty(blocks[0]);

        Assert.True(
            string.Equals(blocks[0], blocks[1], StringComparison.Ordinal),
            "AGENTS.md and CLAUDE.md carry different gitnexus blocks. The generator writes both " +
            "from one template, so they only diverge when something hand-written was added to one " +
            "of them - which the next `npx gitnexus analyze` will delete without warning.");
    }

    [Fact]
    public void TheHandWrittenBeadSplittingRulesAreOutsideIt()
    {
        // NOT A RESTATEMENT OF THE TEST ABOVE, and it took a reviewer to show why: that one is a
        // StartsWith, so hand-written text appended at the END of the block - visually the most
        // natural place to append - passes it and fails only this. Deleting the guidance outright
        // also passes it and fails only this. They cover disjoint regressions.
        var doc = ReadDoc("AGENTS.md");

        var heading = doc.IndexOf("## Splitting a bead", StringComparison.Ordinal);
        var start = IndexOfMarkerLine(doc, StartMarker);

        // The same anti-vacuity discipline as above. Without it a renamed marker makes `start` -1,
        // the ordering assertion fails, and the message blames content that never moved - an
        // assertion that cannot fail for its stated reason, which is the thing the guidance this
        // test protects now argues against at length.
        Assert.True(start >= 0, "the gitnexus:start marker is gone - has the generator changed?");
        Assert.True(heading >= 0, "the bead-splitting guidance has been deleted from AGENTS.md");

        Assert.True(
            heading < start,
            "the bead-splitting guidance is inside the generated gitnexus block again; " +
            "a regeneration will delete it");
    }
}
