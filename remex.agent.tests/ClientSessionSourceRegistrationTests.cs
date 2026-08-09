using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Agent.Services;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The desktop reads live sessions through <see cref="IClientSessionSource"/>, and it must land on
/// the registry the host is actually using (RemEx-0z7w).
/// </summary>
/// <remarks>
/// <para>
/// THE TRAP THIS EXISTS FOR IS ONE CHARACTER OF DIFFERENCE.
/// <c>AddSingleton&lt;IClientSessionSource, ClientSessionRegistry&gt;()</c> reads like the obvious
/// registration and is wrong: the container would construct a SECOND registry, nothing would ever
/// register a session in it, and the shell would confidently report zero phones forever — the exact
/// bug this whole feature exists to fix, arriving by way of its own wiring, with a green build and no
/// log line. The correct form resolves the existing singleton:
/// <c>AddSingleton&lt;IClientSessionSource&gt;(sp =&gt; sp.GetRequiredService&lt;ClientSessionRegistry&gt;())</c>.
/// </para>
/// <para>
/// ASSERTED AGAINST THE SOURCE TEXT of <c>HostBootstrapper</c> rather than against a container built
/// here. Rebuilding the registration in a test would prove that the two lines I typed in the test
/// behave correctly, which is not the question — the question is what the host does, and that is one
/// specific call site.
/// </para>
/// </remarks>
public class ClientSessionSourceRegistrationTests
{
    [Fact]
    public void TheRegistryIsWhatTheDesktopSeesThroughTheInterface()
    {
        // Compile-time in effect, but stated so that removing the interface is a failing test rather
        // than a build error in a different project with a confusing message.
        Assert.IsAssignableFrom<IClientSessionSource>(new ClientSessionRegistry());
    }

    [Fact]
    public void TheInterfaceIsRegisteredAsTheSAMEInstance_NotAsASecondRegistry()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.agent", "HostBootstrapper.cs"));

        // COMMENTS STRIPPED BEFORE EITHER ASSERTION, and the asymmetry that used to be here is the
        // lesson (review). The first version stripped them only for the DoesNotMatch below, leaving
        // the presence check reading raw text — so deleting the registration and leaving a comment
        // that QUOTES it, which is exactly the commenting style used above the real call site, would
        // have satisfied both halves. Green build, no source, shell reports zero phones forever: the
        // bug this feature exists to prevent, restored, with its only guard still passing.
        var code = Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);

        Assert.Matches(
            new Regex(
                @"AddSingleton<\s*(?:Remex\.Desktop\.Services\.)?IClientSessionSource\s*>\s*\(\s*\r?\n?\s*sp\s*=>\s*sp\.GetRequiredService<\s*ClientSessionRegistry\s*>\(\)\s*\)",
                RegexOptions.Multiline),
            code);

        // And the wrong form is absent. Asserting only the right form's presence would still pass if
        // somebody added the two-type overload alongside it, which is the same defect. The comment
        // above the real registration names that wrong form on purpose, to warn the next reader off
        // it — so a guard reading prose as code would be defeated by deleting the warning.
        Assert.DoesNotMatch(
            new Regex(@"AddSingleton<\s*(?:Remex\.Desktop\.Services\.)?IClientSessionSource\s*,"),
            code);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
