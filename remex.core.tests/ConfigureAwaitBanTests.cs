using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that no production code calls <c>ConfigureAwait</c> (RemEx-8phl).
/// </summary>
/// <remarks>
/// <para>
/// The ban is stated in CLAUDE.md, in <c>docs/ASYNC_GUIDELINES.md</c>, and again in a doc comment on
/// <c>FireAndForgetExtensions</c> — and until this test, enforced by nothing. CA2007, the analyzer
/// that would have caught it, is deliberately SUPPRESSED in <c>.editorconfig</c>, precisely because
/// the rule here is the opposite of the analyzer's default advice.
/// </para>
/// <para>
/// THE REASON THE DOCS GIVE FOR THE RULE IS WRONG, and it is worth stating correctly here because
/// this is what someone reads while the build is red. <c>docs/ASYNC_GUIDELINES.md</c> says Avalonia
/// has no <c>SynchronizationContext</c>. Avalonia 11 ships <c>AvaloniaSynchronizationContext</c> with
/// <c>AutoInstall</c> — verified against the packaged <c>Avalonia.Base.dll</c>, not inferred — and the
/// connect commands start on the UI thread, so a context IS installed and the flag was never a no-op.
/// Only the ASP.NET Core half of that claim is correct. The docs and two other comments still repeat
/// it; that is RemEx-rbfq.
/// </para>
/// <para>
/// THE RULE IS STILL RIGHT, for a different reason. This codebase marshals to the UI thread
/// explicitly through <c>Dispatcher.UIThread</c> and never blocks on a task, so capturing the context
/// costs a dispatcher turn and buys nothing — and the deadlock <c>ConfigureAwait(false)</c> exists to
/// prevent cannot happen where nothing blocks. A violation therefore cannot be found by running the
/// code, only by reading it, which is why this is a guard rather than a behavioural assertion. The
/// one occurrence it was written for had survived long enough to look deliberate, and that is the
/// real harm: a lone exception to a stated rule reads as considered and invites copies.
/// </para>
/// <para>
/// A MENTION IN A COMMENT IS NOT A VIOLATION, and the codebase contains one — the doc comment on
/// <c>FireAndForgetExtensions</c> states the rule by name. Anything with <c>//</c> earlier in the
/// line is therefore ignored, which covers both a leading comment and a trailing one. String
/// literals are blanked first, because otherwise a URL earlier in the line would read as a comment
/// and hide a real call — a FALSE NEGATIVE, which is the direction that matters here: a guard that
/// silently stops guarding is the failure the liveness test below exists to catch, whereas a false
/// positive is loud and fixed in one line.
/// </para>
/// <para>
/// SCOPE: every C# tree that ships, including the Windows-only <c>remex.agent.windows</c> — a real
/// ProjectReference of remex.agent, kept out of the solution because it cannot build on Linux, and
/// WinRT interop is statistically the likeliest place someone reaches for the flag. Test projects
/// are deliberately excluded: CLAUDE.md says "anywhere", but the harm this guards against is in
/// shipped async paths.
/// </para>
/// </remarks>
public class ConfigureAwaitBanTests
{
    /// <summary>Every shipped C# tree, relative to the repo root. Absent ones are skipped.</summary>
    private static readonly string[] ScannedProjects =
    [
        "remex.core",
        "remex.desktop",
        "remex.agent",
        // Not in Remex.sln (Windows-only), so easy to forget - which is why it is named explicitly.
        "remex.agent.windows",
        "remex.branding",
        Path.Combine("tools", "BrandAssetGen"),
    ];

    [Fact]
    public void NoProductionSourceCallsConfigureAwait()
    {
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var root = Path.Combine(RepoRoot(), project);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Generated output, not authored source. An artifacts path outside the repo is the
                // normal case here, but a local build can still leave these in place.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = BlankStringLiterals(lines[i]);

                    var call = line.IndexOf("ConfigureAwait(", StringComparison.Ordinal);
                    if (call < 0) continue;

                    var comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0 && comment < call) continue;

                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "ConfigureAwait is banned repo-wide (CLAUDE.md, docs/ASYNC_GUIDELINES.md). This code "
            + "marshals to the UI thread explicitly via Dispatcher.UIThread and never blocks on a task, "
            + "so capturing the context costs a dispatcher turn and buys nothing, and the deadlock the "
            + "flag exists to prevent cannot occur. Remove it at:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScanWouldActuallyFindOne()
    {
        // A guard that scans source can pass because it found nothing OR because it looked nowhere,
        // and those are indistinguishable from a green run. This asserts it looked: the same three
        // trees must contain a large number of .cs files, and the mention in FireAndForgetExtensions
        // must be present — the one line the scan above has to see and deliberately not report.
        var files = ScannedProjects
            .Select(p => Path.Combine(RepoRoot(), p))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .ToList();

        Assert.True(files.Count > 100, $"only {files.Count} source files found; the scan is not reaching the repo");

        var mention = Path.Combine(RepoRoot(), "remex.desktop", "Services", "FireAndForgetExtensions.cs");
        Assert.True(File.Exists(mention), "the file whose comment states the rule has moved; re-point this check");
        Assert.Contains("ConfigureAwait(false)", File.ReadAllText(mention), StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces the contents of double-quoted string literals with spaces, preserving length.
    /// </summary>
    /// <remarks>
    /// So a <c>//</c> inside a literal - a URL being the realistic case - cannot be mistaken for the
    /// start of a comment and exempt a real call later on the same line. Escaped quotes are honoured
    /// so a literal containing one does not terminate early.
    /// </remarks>
    private static string BlankStringLiterals(string line)
    {
        var result = line.ToCharArray();
        var inside = false;

        for (int i = 0; i < result.Length; i++)
        {
            if (inside && result[i] == '\\')
            {
                result[i] = ' ';
                if (i + 1 < result.Length) result[i + 1] = ' ';
                i++;
                continue;
            }

            if (result[i] == '\"')
            {
                inside = !inside;
                continue;
            }

            if (inside) result[i] = ' ';
        }

        return new string(result);
    }

    /// <summary>
    /// The repo root, from this file's own compile-time path.
    /// </summary>
    /// <remarks>
    /// Not by walking up from the assembly: building with an out-of-repo artifacts path makes that
    /// fail with an error about a missing directory rather than about what is actually wrong
    /// (RemEx-6i1l, and the same mistake twice more since).
    /// </remarks>
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
