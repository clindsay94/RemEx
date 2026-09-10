using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Source-level sweep: every numeric <c>Parse</c> / <c>TryParse</c> in <c>remex.agent</c> must pass
/// <c>CultureInfo.InvariantCulture</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. <c>InvariantGlobalization</c> is set in no csproj and no
/// <c>Directory.Build.props</c>, so the ambient culture on a running host really is the user's — and
/// RemEx must run on a de-DE or fr-FR CachyOS box. Two kinds of input reach these parses and neither
/// is locale-dependent: kernel text (<c>/proc/uptime</c>, <c>/proc/stat</c>, sysfs hwmon, <c>ss</c>
/// output), which is C-locale by definition, and wire values from an Android phone whose locale the
/// host does not know. <c>double.Parse("1234.56")</c> under de-DE reads the '.' as a group separator
/// and yields <b>123456</b> — a silent 100× error with no exception, which is how the uptime figure
/// was wrong before this sweep.
/// </para>
/// <para>
/// WHY A SOURCE SCAN AND NOT A CULTURE-SWITCHING TEST. Some of these sites read files that exist only
/// on Linux, some need a live socket, and a test that sets <c>CurrentCulture</c> proves one call site
/// at a time while the realistic regression is a NEW parse written without the argument. The sibling
/// <c>CultureInvariantArgvTests</c> / <c>CaptureArgumentCultureTests</c> already cover the FORMATTING
/// direction the same way; this is the parsing half, which had no guard at all.
/// </para>
/// <para>
/// KNOWN LIMIT: it recognises the parse by the receiver type name, so
/// <c>SomeGeneric&lt;T&gt;.Parse</c> or an extension helper is invisible to it. It is aimed at the
/// slip that actually happens — typing the one-argument overload out of habit.
/// </para>
/// </remarks>
public class NumericParseCultureSweepTests
{
    /// <summary>The numeric receivers whose one-argument overloads read the ambient culture.</summary>
    private static readonly string[] NumericTypes =
        ["double", "float", "decimal", "int", "long", "short", "byte", "uint", "ulong", "ushort", "sbyte"];

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    private static IEnumerable<string> AgentSourceFiles()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "remex.agent"), "*.cs", SearchOption.AllDirectories)
            // artifacts/ and obj/ can land inside the project folder depending on how it was built;
            // generated sources are not ours to fix and would make this fail on a build layout change.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Blanks comments and string/char literals so a parse discussed in prose, or a ')' inside a
    /// literal, cannot be mistaken for code. Length is preserved so offsets stay usable.
    /// </summary>
    private static string Blanked(string source)
    {
        var result = new StringBuilder(source);
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            int start = i;

            if (c == '/' && i + 1 < result.Length && result[i + 1] == '/')
            {
                while (i < result.Length && result[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < result.Length && result[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < result.Length && !(result[i] == '*' && result[i + 1] == '/')) i++;
                i = Math.Min(i + 1, result.Length - 1);
            }
            else if (c == '"' || c == '\'')
            {
                // Verbatim and raw string literals are rare here and only ever WIDEN what gets
                // blanked, which is the safe direction for this scan.
                i++;
                while (i < result.Length && result[i] != c && result[i] != '\n')
                {
                    if (result[i] == '\\') i++;
                    i++;
                }
            }
            else
            {
                continue;
            }

            for (int j = start; j <= i && j < result.Length; j++)
                if (result[j] != '\n') result[j] = ' ';
        }

        return result.ToString();
    }

    /// <summary>Returns the balanced argument list that follows <paramref name="openParen"/>.</summary>
    private static string ArgumentList(string source, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0) return source[(openParen + 1)..i];
            }
        }
        return source[openParen..];
    }

    [Fact]
    public void EveryNumericParseInTheAgentPassesInvariantCulture()
    {
        var pattern = new Regex(
            @"(?<!\w)(" + string.Join('|', NumericTypes) + @")\.(Try)?Parse\(",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in AgentSourceFiles())
        {
            var source = Blanked(File.ReadAllText(file));
            foreach (Match match in pattern.Matches(source))
            {
                scanned++;
                var args = ArgumentList(source, match.Index + match.Length - 1);
                if (args.Contains("InvariantCulture", StringComparison.Ordinal))
                    continue;

                var line = source.Take(match.Index).Count(ch => ch == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{line} — {match.Value}…");
            }
        }

        // ANTI-VACUITY, and it is not decoration: a scan whose file walk or regex has quietly stopped
        // matching passes by finding nothing, which is exactly how an inert guard looks (AGENTS.md,
        // "Sweeping for inert GUARDS"). remex.agent had over 30 numeric parse sites when this was written.
        Assert.True(scanned >= 20, $"only {scanned} numeric parse site(s) found — this scan has gone blind");

        Assert.True(
            offenders.Count == 0,
            "These numeric parses read the ambient culture. Every input reaching them is either "
            + "C-locale kernel text or a wire value from a phone in an unknown locale, so on a de-DE "
            + "host '1234.56' parses as 123456 with no exception. Pass "
            + "NumberStyles.<X>, CultureInfo.InvariantCulture:\n  "
            + string.Join("\n  ", offenders));
    }
}
