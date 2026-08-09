using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Remex.Core.Tests;

/// <summary>
/// No test assembly blocks a thread-pool thread waiting on a <see cref="Task"/> (RemEx-7cq0).
/// </summary>
/// <remarks>
/// <para>
/// <c>PairingHandlerTests</c> ran <c>Task.Delay(150).Wait()</c> in its constructor, once per test in
/// the class, because a constructor cannot await and the class needs a pause between tests for
/// background Kestrel cleanup to release a shared lock. The cost was never the 150ms — that is paid
/// either way. It was one fewer pool thread for the duration, in an assembly that runs concurrently
/// with every other one in a whole-solution run.
/// </para>
/// <para>
/// THAT IS THE SHAPE THAT STARVES THE POOL, and pool starvation was the leading hypothesis for the
/// RemEx-w7ei flake — which appeared ONLY in whole-solution runs and never in project-only ones.
/// Nobody has measured the link and this guard does not claim it. It exists because the pattern is
/// banned repo-wide on its own merits and because the fix is otherwise a one-line revert away.
/// </para>
/// <para>
/// **TESTS ONLY, AND THAT IS WHAT MAKES IT ALLOWLIST-FREE.** Production has ten legitimate blocking
/// sites and they are not oversights: <c>AndroidNativeExports</c> and <c>RemexDesktopClient</c> sit
/// on a P/Invoke boundary that cannot be made async, <c>LinuxRemoteDesktopPrerequisites</c> offers a
/// documented synchronous overload, and <c>DxgiDesktopCapture</c> / <c>PairingService</c> call
/// <c>SemaphoreSlim.Wait()</c>, which is not sync-over-async at all. A repo-wide version of this rule
/// would need an allowlist of exactly the sites it should not fire on, and an allowlist is where a
/// guard goes to become a formality. Test code has no such excuse: every xUnit test may be async, and
/// <c>IAsyncLifetime</c> covers the setup and teardown a constructor cannot.
/// </para>
/// <para>
/// COMMENTS AND STRINGS ARE STRIPPED FIRST. Without that, this file's own prose describing the
/// banned pattern would trip it — and the natural repair is to stop writing the pattern down, which
/// costs the next reader the explanation. A guard should not make its own rationale unwritable.
/// </para>
/// </remarks>
public class NoBlockingWaitsInTestsTests
{
    [Fact]
    public void NoTestAssemblyBlocksOnATask()
    {
        var offenders = TestSourceFiles()
            .SelectMany(file => Offences(file))
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Test setup or a test body blocks a thread-pool thread waiting on a Task. Await it "
            + "instead — a test method may be async, and IAsyncLifetime.InitializeAsync is the async "
            + "constructor a test class does not otherwise have (RemEx-7cq0): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheScanWouldActuallyCatchTheThingItBans()
    {
        // THE ANTI-VACUITY CHECK, probing the real function rather than a re-implementation beside
        // it. An offender scan that finds nothing looks identical whether the codebase is clean or
        // the pattern is wrong, and this repo has shipped the second kind (RemEx-ro00r).
        Assert.NotEmpty(OffencesIn("x.cs", "Task.Delay(150).Wait();"));
        Assert.NotEmpty(OffencesIn("x.cs", "var r = FetchAsync().GetAwaiter().GetResult();"));

        // Stripped first, so the rule stays writable in prose and in test data.
        Assert.Empty(OffencesIn("x.cs", "// never write Task.Delay(150).Wait() here"));
        Assert.Empty(OffencesIn("x.cs", "/* Task.Delay(150).Wait() */"));
        Assert.Empty(OffencesIn("x.cs", "var advice = \"do not call .GetAwaiter().GetResult()\";"));

        // SemaphoreSlim.Wait() IS NOT SYNC-OVER-ASYNC and must not be swept in. Banning a bare
        // .Wait() would fire on every lock in the repo and the rule would be turned off.
        Assert.Empty(OffencesIn("x.cs", "_lock.Wait();"));
        Assert.Empty(OffencesIn("x.cs", "_gate.Wait(cancellationToken);"));

        // A CHAR LITERAL HOLDING A QUOTE, which is the bug that made the first Strip blank
        // twenty-two lines of a real file. The offence AFTER it must still be seen.
        Assert.NotEmpty(OffencesIn("x.cs",
            "if (c == '\"') { }\nvar r = FetchAsync().GetAwaiter().GetResult();"));

        // AND THE SAME THING ON ONE LINE, which is what actually pins the char-literal branch. The
        // probe above does NOT: with the newline exclusion in place, deleting the char branch leaves
        // it green, because a stray quote can no longer reach the next line to do any damage. Mutation
        // is how that came out — the branch looked tested and was not. Here the offence sits BETWEEN
        // the char literal's quote and a later quote on the SAME line, so without the branch the two
        // pair up and swallow it.
        Assert.NotEmpty(OffencesIn("x.cs",
            "if (c == '\"') { FetchAsync().GetAwaiter().GetResult(); var s = \"x\"; }"));

        // A REGULAR STRING MAY NOT SPAN A LINE, and this is the separate half of the same defect —
        // separate because the char-literal branch alone rescues the probe above. It needs TWO quotes
        // on DIFFERENT lines: the first version's newline-permissive body would pair them and blank
        // everything between, offence included. The first attempt at this probe used one quote, had
        // nothing to pair with, and passed either way — an inert guard written while fixing one.
        Assert.NotEmpty(OffencesIn("x.cs",
            "var s = \"open;\nFetchAsync().GetAwaiter().GetResult();\nvar t = \"close\";"));

        // .Result is the most idiomatic spelling of the thing being banned, and the first version of
        // this scan did not look for it at all.
        Assert.NotEmpty(OffencesIn("x.cs", "var r = FooAsync().Result;"));
        Assert.Empty(OffencesIn("x.cs", "var r = FooAsync().GetResult2();"));

        // A WRAPPED CHAIN, since this repo wraps long ones and adjacency-only patterns would let a
        // reformat disarm the guard against a line it was already catching.
        Assert.NotEmpty(OffencesIn("x.cs", "FooAsync()\n    .GetAwaiter()\n    .GetResult();"));

        // Nested parentheses in the arguments.
        Assert.NotEmpty(OffencesIn("x.cs", "RunAsync(Wrap(1)).Wait();"));

        // A verbatim string may span lines and must still be blanked wholesale; a raw string too.
        Assert.Empty(OffencesIn("x.cs", "var s = @\"line one\nTask.Delay(1).Wait()\nline three\";"));
        Assert.Empty(OffencesIn("x.cs", "var s = \"\"\"\nTask.Delay(1).Wait()\n\"\"\";"));
    }

    [Fact]
    public void TheScanLooksAtEveryTestAssembly()
    {
        // A PATH THAT RESOLVES TO NOTHING IS THE OTHER WAY THIS PASSES WITHOUT CHECKING ANYTHING.
        // Asserting each named assembly contributed files means a rename or a move fails here rather
        // than silently shrinking the scan's reach.
        var byAssembly = TestAssemblies
            .ToDictionary(name => name, name => TestSourceFiles().Count(
                f => f.Contains(Path.DirectorySeparatorChar + name + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)));

        Assert.All(byAssembly, entry =>
            Assert.True(entry.Value > 0, $"{entry.Key} contributed no files — did it move or get renamed?"));

        // AND THE LIST IS CHECKED AGAINST DISK, because the loop above cannot detect an ADDITION:
        // both of its sides read the same constant, so a fourth test project would be excluded from
        // the scan forever with every test still green. Comparing to what is actually there makes a
        // new project fail HERE, with a message saying to add it, instead of quietly going unscanned.
        var onDisk = Directory.GetDirectories(RepoRoot(), "*.tests")
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            onDisk,
            TestAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private static readonly string[] TestAssemblies =
        ["remex.core.tests", "remex.agent.tests", "remex.desktop.tests"];

    private static IEnumerable<string> TestSourceFiles() =>
        TestAssemblies
            .Select(name => Path.Combine(RepoRoot(), name))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));

    private static IEnumerable<string> Offences(string file) =>
        OffencesIn(Path.GetRelativePath(RepoRoot(), file), File.ReadAllText(file));

    /// <summary>The rule, applied to one file's text.</summary>
    /// <remarks>
    /// <para>
    /// <c>.Wait()</c> is matched only on something ending in <c>Async()</c> or on a
    /// <c>Task.Delay(...)</c> — never bare — so <c>SemaphoreSlim.Wait()</c>, which blocks on a lock
    /// rather than on a Task and is entirely correct, does not match. A Task-typed LOCAL
    /// (<c>var t = FooAsync(); t.Wait();</c>) is not matched either: telling a Task variable from a
    /// semaphore one needs types, not text.
    /// </para>
    /// <para>
    /// <c>\s*</c> BETWEEN THE MEMBERS because this repo wraps long fluent chains, and adjacency-only
    /// patterns would let a reformat disarm the guard against a line it was already catching.
    /// </para>
    /// <para>
    /// THE ONE LEGITIMATE BLOCKING TEST IN THIS REPO, named here so the next person to tighten these
    /// patterns meets it in the documentation rather than in a red build:
    /// <c>HostShutdownContextTests</c> runs <c>YieldThenComplete().Wait(TimeSpan.FromSeconds(2))</c>
    /// as the executable reproduction of the RemEx-rbfq dispatcher deadlock. It MUST block or it has
    /// no subject, and it blocks a dedicated thread rather than a pool one, so it does not carry the
    /// cost this guard exists to prevent. It escapes on two independent technicalities — the method
    /// does not end in <c>Async</c> and the <c>.Wait</c> takes an argument. That means the
    /// allowlist-free property here is REAL BUT CONTINGENT, not principled: broaden these patterns
    /// far enough and that test turns red, and the next step after that is an allowlist.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> OffencesIn(string label, string text)
    {
        var code = Strip(text);

        var patterns = new[]
        {
            @"\.GetAwaiter\(\)\s*\.GetResult\(\)",
            @"Task\.Delay\([^;]*?\)\s*\.Wait\(\)",
            @"\w*Async\([^;]*?\)\s*\.Wait\(\)",
            @"\.Result\b",
            @"\bTask\.WaitAll\b",
            @"\bTask\.WaitAny\b",
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(code, pattern))
            {
                var line = code.Take(match.Index).Count(c => c == '\n') + 1;
                yield return $"{label}:{line}";
            }
        }
    }

    /// <summary>Regions whose contents are not code: comments and every C# literal form.</summary>
    /// <remarks>
    /// <para>
    /// **THE FIRST VERSION BLANKED REAL CODE, AND IT DID SO SILENTLY.** It spelled a regular string
    /// as <c>"(?:\\.|[^"\\])*"</c>, and <c>[^"\\]</c> matches a newline — so any quote that was NOT
    /// a string delimiter opened a pseudo-string running to the next quote ANYWHERE in the file, with
    /// every line between blanked before the scan saw it. One <c>'"'</c> char literal in
    /// <c>ConfigureAwaitBanTests</c> was costing twenty-two lines of that file, and an offence
    /// injected into that window was invisible. A guard reading less than it claims looks exactly
    /// like a clean codebase.
    /// </para>
    /// <para>
    /// The fix is the C# rule the first version ignored: a non-verbatim string CANNOT span a line, so
    /// <c>\n</c> is excluded from its body. Char literals are consumed first, since <c>'"'</c> is
    /// legal and is what triggered the bug. Raw strings come before both, because <c>"""</c> would
    /// otherwise be read as an empty string followed by an opening quote.
    /// </para>
    /// <para>
    /// ONLY THE RAW-STRING ALTERNATIVE CAPTURES, so <c>\1</c> means its own delimiter run regardless
    /// of where the alternation is joined. Every other group is <c>(?:</c>.
    /// </para>
    /// </remarks>
    private static readonly string[] NonCodeRegions =
    [
        @"//[^\n]*",                    // line comment
        @"/\*.*?\*/",                   // block comment
        @"(""{3,})[\s\S]*?\1",          // raw string literal, any delimiter width
        @"'(?:\\.|[^'\\\n])'",          // char literal — including '"', which started all this
        @"@""(?:""""|[^""])*""",        // verbatim string, which MAY span lines
        @"""(?:\\[^\n]|[^""\\\n])*""",  // regular string, which may NOT
    ];

    /// <summary>
    /// Blanks comments and literals, preserving newlines so reported line numbers stay true.
    /// </summary>
    private static string Strip(string text) =>
        Regex.Replace(
            text,
            string.Join("|", NonCodeRegions),
            match => Regex.Replace(match.Value, "[^\n]", " "),
            RegexOptions.Singleline);

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
