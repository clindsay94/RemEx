using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Services.Command;

namespace Remex.Core.Tests;

/// <summary>
/// The declared verb lists match what the dispatchers actually accept (RemEx-q7l0c).
/// </summary>
/// <remarks>
/// <para>
/// **A LIST THAT DRIFTS FROM THE CODE IS WORSE THAN NO LIST** — it answers "can the TCP ingress kill
/// a process?" confidently and wrongly. And drift is not hypothetical here: SCREENSHOT landed on
/// <c>/ws</c> in RemEx-byij and RemEx-pmb4's written record of the split never followed it, which is
/// how the bead describing the problem came to demonstrate it.
/// </para>
/// <para>
/// Reads the two switch statements as source. A behavioural alternative would mean standing up both
/// ingresses and probing every verb, which is far heavier than the thing it guards — and it would
/// prove the dispatchers accept the verbs, not that the LIST agrees with them, which is the property
/// at risk.
/// </para>
/// </remarks>
public class CommandVerbDriftTests
{
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    /// <summary>Every <c>case "VERB":</c> label inside a file's command switch.</summary>
    /// <remarks>
    /// Upper-case only, which is what both dispatchers switch on after normalising with
    /// <c>ToUpperInvariant</c>. That also keeps the scan from picking up unrelated string cases —
    /// message-type and status labels in these files are lower-case or mixed.
    /// </remarks>
    private static HashSet<string> DispatchedVerbs(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(full), $"{relativePath} moved or was renamed");

        return Regex.Matches(File.ReadAllText(full), "case \"([A-Z][A-Z0-9]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void TheScriptIngressDispatchesExactlyWhatItDeclares()
    {
        // THE ONE THAT CARRIES THE SECURITY PROPERTY. 8338 is external attack surface hardened in
        // RemEx-s032.2, so a verb appearing here is a widening of it. Failing on ANY difference -
        // rather than only on additions - also catches a verb quietly removed, which would leave the
        // list promising something the ingress no longer honours.
        Assert.Equal(
            CommandVerbs.ScriptIngress.OrderBy(v => v, StringComparer.Ordinal),
            DispatchedVerbs("remex.core/Services/Network/RemexNetworkListener.cs").OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void ThePairedChannelDispatchesExactlyWhatItDeclares()
    {
        Assert.Equal(
            CommandVerbs.PairedChannel.OrderBy(v => v, StringComparer.Ordinal),
            DispatchedVerbs("remex.agent/Handlers/PingPongHandler.cs").OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void TheScriptIngressIsASUBSETOfThePairedChannel()
    {
        // The split is a narrowing, not two independent tables. A verb the script ingress accepts and
        // the paired channel does not would mean an external caller could do something the
        // authenticated one cannot, which is the wrong way round for every verb here.
        Assert.Empty(CommandVerbs.ScriptIngress.Except(CommandVerbs.PairedChannel));
    }

    [Fact]
    public void TheVerbsWithheldFromTheScriptIngressAreTheOnesThatNameATarget()
    {
        // NOT A RESTATEMENT OF THE LIST - it is the RULE the split follows, and the reason a future
        // verb belongs on one side or the other. Everything withheld acts on something the caller
        // chose: a process, an executable, whatever is on screen. Everything shared is a whole-machine
        // power action whose blast radius does not depend on an argument.
        Assert.Equal(
            new[] { "KILLPROCESS", "KILLPROCESSELEVATED", "LAUNCHAPP", "SCREENSHOT" }.OrderBy(v => v, StringComparer.Ordinal),
            CommandVerbs.PairedOnly.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void TheScanFindsSomethingRatherThanPassingOnAnEmptySet()
    {
        // The anti-vacuity check. A wrong path or a tightened regex makes both comparisons above
        // compare nothing to nothing - and an empty declared list would then agree with it.
        Assert.NotEmpty(DispatchedVerbs("remex.core/Services/Network/RemexNetworkListener.cs"));
        Assert.NotEmpty(DispatchedVerbs("remex.agent/Handlers/PingPongHandler.cs"));
        Assert.NotEmpty(CommandVerbs.ScriptIngress);
    }
}
