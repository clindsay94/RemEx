using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Services.Command;

namespace Remex.Core.Tests;

/// <summary>
/// The declared verb lists match what the code actually dispatches (RemEx-q7l0c, RemEx-pmb4).
/// </summary>
/// <remarks>
/// <para>
/// **A LIST THAT DRIFTS FROM THE CODE IS WORSE THAN NO LIST** — it answers "can the TCP ingress kill
/// a process?" confidently and wrongly. Drift was not hypothetical here: SCREENSHOT landed on
/// <c>/ws</c> in RemEx-byij and the written record of the split never followed it, which is how the
/// bead describing the problem came to demonstrate it.
/// </para>
/// <para>
/// **WHAT THESE TESTS GUARD CHANGED SHAPE IN RemEx-pmb4, AND THAT IS THE POINT.** They used to read
/// two hand-written switches and compare them to two hand-written lists — detecting a divergence
/// that could still happen. The eleven shared verbs now have ONE implementation in
/// <see cref="SharedCommandVerbs"/> that both ingresses call, so the divergence cannot start. What
/// is left to guard is different: that the declared lists still describe that implementation, and
/// that a second copy has not reappeared beside it.
/// </para>
/// <para>
/// Source-scanned rather than behavioural. Standing up both ingresses and probing every verb is far
/// heavier than the thing it guards, and it would prove the dispatchers accept the verbs — not that
/// the LIST agrees with them, which is the property at risk.
/// </para>
/// </remarks>
public class CommandVerbDriftTests
{
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    /// <summary>Every <c>case "VERB":</c> label in a file.</summary>
    /// <remarks>
    /// Upper-case only, which is what every dispatcher switches on after normalising with
    /// <c>ToUpperInvariant</c>. That also keeps the scan off unrelated string cases — message-type
    /// and status labels in these files are lower-case or mixed.
    /// </remarks>
    private static HashSet<string> DispatchedVerbs(string relativePath)
    {
        var full = Path.Combine([RepoRoot(), .. relativePath.Split('/')]);
        Assert.True(File.Exists(full), $"{relativePath} moved or was renamed");

        return Regex.Matches(File.ReadAllText(full), """case "([A-Z][A-Z0-9]+)"\s*:""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private const string Shared = "remex.core/Services/Command/SharedCommandVerbs.cs";
    private const string PairedChannel = "remex.agent/Handlers/PingPongHandler.cs";
    private const string ScriptIngress = "remex.core/Services/Network/RemexNetworkListener.cs";

    [Fact]
    public void TheScriptIngressDeclaresExactlyTheSharedImplementation()
    {
        // THE ONE CARRYING THE SECURITY PROPERTY. 8338 is external attack surface hardened in
        // RemEx-s032.2, so a verb appearing here widens it. Since RemEx-pmb4 that ingress dispatches
        // the shared set and NOTHING else, so the declared list must be exactly that set - failing
        // on any difference rather than only on additions also catches a verb quietly removed.
        Assert.Equal(
            CommandVerbs.ScriptIngress.OrderBy(v => v, StringComparer.Ordinal),
            DispatchedVerbs(Shared).OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void ThePairedChannelDeclaresTheSharedSetPlusItsOwn()
    {
        Assert.Equal(
            CommandVerbs.PairedChannel.OrderBy(v => v, StringComparer.Ordinal),
            DispatchedVerbs(Shared).Union(DispatchedVerbs(PairedChannel), StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void TheScriptIngressKeepsNoVerbSwitchOfItsOwn()
    {
        // **THE ASSERTION RemEx-pmb4 ADDED, AND THE ONE THAT KEEPS ITS BENEFIT.** Both ingresses
        // writing out the same eleven verbs is what let them drift in the first place. Detecting
        // divergence was the old guard; this stops the second copy from ever existing again, which
        // is strictly better - a re-added `case "SHUTDOWN":` here fails immediately rather than
        // waiting to disagree with something.
        //
        // INTERSECTED WITH THE KNOWN VERBS RATHER THAN ASSERTED EMPTY. That file is 700+ lines and
        // handles more than commands, so a bare Assert.Empty would fail on any unrelated upper-case
        // case label someone adds - a guard that cries wolf on correct code gets deleted, and then
        // it is not there for the real thing.
        Assert.Empty(DispatchedVerbs(ScriptIngress).Intersect(CommandVerbs.PairedChannel, StringComparer.Ordinal));
    }

    [Fact]
    public void TheScriptIngressIsASubsetOfThePairedChannel()
    {
        // The split is a narrowing, not two independent tables. A verb the script ingress accepts
        // and the paired channel does not would let an external caller do something the
        // authenticated one cannot, which is the wrong way round for every verb here.
        Assert.Empty(CommandVerbs.ScriptIngress.Except(CommandVerbs.PairedChannel));
    }

    [Fact]
    public void TheVerbsWithheldFromTheScriptIngressAreTheOnesThatNameATarget()
    {
        // NOT A RESTATEMENT OF THE LIST - it is the RULE the split follows, and the reason a future
        // verb belongs on one side or the other. Everything withheld acts on something the caller
        // chose: a process, an executable, whatever is on screen. Everything shared is a
        // whole-machine power action whose blast radius does not depend on an argument.
        //
        // It is also why these four are not in SharedCommandVerbs: each needs per-connection state
        // that a static table has no business holding.
        Assert.Equal(
            new[] { "KILLPROCESS", "KILLPROCESSELEVATED", "LAUNCHAPP", "SCREENSHOT" }.OrderBy(v => v, StringComparer.Ordinal),
            CommandVerbs.PairedOnly.OrderBy(v => v, StringComparer.Ordinal));

        // And that the paired channel really is where they live. Intersected for the same reason as
        // above: PingPongHandler dispatches far more than commands.
        Assert.Equal(
            CommandVerbs.PairedOnly.OrderBy(v => v, StringComparer.Ordinal),
            DispatchedVerbs(PairedChannel).Intersect(CommandVerbs.PairedChannel, StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void TheScanFindsSomethingRatherThanPassingOnAnEmptySet()
    {
        // The anti-vacuity check, and it matters more since RemEx-pmb4 than before: one assertion
        // above now EXPECTS an empty set, so a scan that has silently stopped matching would satisfy
        // it while every other comparison compared nothing to nothing.
        Assert.NotEmpty(DispatchedVerbs(Shared));
        Assert.NotEmpty(DispatchedVerbs(PairedChannel));
        Assert.NotEmpty(CommandVerbs.ScriptIngress);
    }
}
