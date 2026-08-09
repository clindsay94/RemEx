using System.Linq;
using System.Runtime.CompilerServices;

namespace Remex.Core.Tests;

/// <summary>
/// No tracked source file carries a stray control character (RemEx-ro00r).
/// </summary>
/// <remarks>
/// <para>
/// **A GUARD WENT INERT ON TWO INVISIBLE BYTES.** RemEx-wm4rt described an assertion that failed
/// against a string provably containing what it was looking for, could not be explained, and was
/// removed rather than shipped. The cause was two literal 0x08 BACKSPACE bytes sitting where a
/// regex's word-boundary escapes belonged: the pattern was backspace-IsConnected-backspace and
/// matched nothing. The offender scan built on it had been green since it shipped, because a pattern
/// that matches nothing finds no offenders — which is exactly what that test asserts.
/// </para>
/// <para>
/// WHY THIS EXISTS RATHER THAN JUST THE FIX. The byte is invisible in every editor and every diff.
/// The file compiles, the test passes, and the only symptom is a guard that never fires. It recurred
/// DURING the fix — a comment written to describe the bug reintroduced two more — which is the
/// clearest evidence available that noticing it by eye is not a strategy.
/// </para>
/// <para>
/// TAB, CR AND LF ARE LEGITIMATE and excluded. Everything else in the C0 range is either a
/// typesetting relic or a terminal escape, and none of them belongs in source this project writes.
/// </para>
/// </remarks>
public class NoStrayControlCharactersTests
{
    [Fact]
    public void NoTrackedSourceFileCarriesOne()
    {
        string[] extensions = [".cs", ".kt", ".kts", ".ps1", ".axaml", ".resx", ".xml", ".json", ".md"];
        string[] skipDirectories = ["obj", "bin", ".git", "node_modules", ".gradle", "artifacts", ".ralph"];

        var offenders = Directory
            .EnumerateFiles(RepoRoot(), "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(skipDirectories.Contains))
            .SelectMany(f => File.ReadLines(f)
                .Select((line, index) => (line, number: index + 1))
                .Where(l => l.line.Any(IsStray))
                .Select(l => $"{Path.GetRelativePath(RepoRoot(), f)}:{l.number}"))
            .Take(20)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These files carry a control character that is invisible in an editor and in a diff, and "
            + "which silently changes what a string literal means — a regex escape that became a "
            + "backspace is how RemEx-wm4rt's guard passed for months while matching nothing: "
            + string.Join(", ", offenders));
    }

    /// <summary>C0 controls other than tab, carriage return and line feed.</summary>
    private static bool IsStray(char c) => char.IsControl(c) && c != '\t' && c != '\r' && c != '\n';

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
