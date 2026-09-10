using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that both WebSocket endpoints construct their handler inside a <c>using</c>, so that
/// connection-end cleanup actually runs (RemEx-kqje).
/// </summary>
/// <remarks>
/// <para>
/// WHAT THE <c>using</c> IS LOAD-BEARING FOR. Disposing the handler is what releases keys a client
/// left held down on the user's real desktop — RemEx-e2p4 for the Remote Desktop stream, RemEx-73dc
/// for the Remote Control socket. A phone that vanishes mid-chord leaves Ctrl or Alt physically
/// pressed, and only the host can clean that up, because the client is what went away.
/// </para>
/// <para>
/// WHY A TEST AND NOT A COMPILER RULE. Deleting the one <c>using</c> keyword leaves every other test
/// green: the handler tests construct a handler and call <c>Dispose</c> themselves, so they pin
/// dispatch → tracker → Dispose but never connection-end → Dispose. That is the RemEx-y6x6 shape — a
/// one-token deletion that disables a user-visible guarantee with nothing going red — and it is a
/// plausible edit, since anyone extracting the endpoint delegate into a helper would drop it without
/// noticing.
/// </para>
/// <para>
/// TWO COMPILER-ENFORCED VERSIONS WERE CONSIDERED AND REJECTED, so nobody repeats the experiment.
/// Enabling CA2000 scoped to <c>HostBootstrapper.cs</c> does catch this, but it also fires on two
/// pieces of correct code in the same file — the logger provider handed to <c>AddProvider</c> and the
/// factory from <c>LoggerFactory.Create</c>, both of which transfer ownership to the host. Silencing
/// those two with <c>#pragma warning disable</c> would buy enforcement at every present and future
/// site in every syntactic form, which IS stronger than a regex; it was passed over because CA2000 is
/// notoriously false-positive-prone around awaits and lambda-captured locals, so the two suppressions
/// would not stay two — and a rule that reports correct code gets suppressed rather than obeyed. That
/// trade is a judgment call rather than a settled one; revisit it if this file ever needs widening
/// again.
/// </para>
/// <para>
/// Comments are stripped before scanning. A guard that fails on prose merely describing the pattern
/// it protects gets deleted rather than fixed — this file's own remarks would trip it otherwise.
/// </para>
/// </remarks>
public class HandlerDisposalWiringTests
{
    /// <summary>Handlers whose Dispose performs connection-end cleanup.</summary>
    private static readonly string[] DisposableHandlers = ["PingPongHandler", "RemoteDesktopHandler"];

    /// <summary>
    /// Matches a construction of <paramref name="handler"/> in any form, optionally namespace-qualified.
    /// </summary>
    /// <remarks>
    /// KNOWN GAP, stated rather than implied: a target-typed <c>using Handler h = new(…);</c> names no
    /// type and so matches neither this nor the disposed pattern. That reads as "the endpoint moved
    /// away" and fails <see cref="TheEndpointsAreStillConstructedHere"/> loudly, which is the right
    /// direction — it never passes silently.
    /// </remarks>
    private static Regex AnyConstruction(string handler) =>
        new(@"new\s+(?:[\w.]+\.)?" + handler + @"\s*\(");

    /// <summary>Matches a construction whose result is owned by a <c>using</c>, in any of its forms.</summary>
    /// <remarks>
    /// Accepts the declaration (<c>using var h = …</c>), the explicitly typed declaration
    /// (<c>using Handler h = …</c>), the statement form (<c>using (var h = …)</c>) and
    /// <c>await using</c>. Rejecting a correct form would make this the kind of guard that gets
    /// deleted rather than satisfied.
    /// </remarks>
    private static Regex DisposedConstruction(string handler) =>
        new(@"using\s*\(?\s*(?:var|[\w.]+)\s+\w+\s*=\s*new\s+(?:[\w.]+\.)?" + handler + @"\s*\(");

    private static string BootstrapperSourceWithoutComments()
    {
        var path = Path.Combine(RepoRoot(), "remex.agent", "HostBootstrapper.cs");
        Assert.True(File.Exists(path), $"expected the host bootstrapper at {path}");

        var source = File.ReadAllText(path);
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", string.Empty);
        return source;
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    [Theory]
    [InlineData("PingPongHandler")]
    [InlineData("RemoteDesktopHandler")]
    public void TheEndpointConstructsItsHandlerInsideAUsing(string handler)
    {
        var source = BootstrapperSourceWithoutComments();

        Assert.True(
            DisposedConstruction(handler).IsMatch(source),
            $"No `using` owns the {handler} built in HostBootstrapper.cs. Disposing it is what releases "
            + "keys a disconnecting client left held down on the user's desktop, and nothing else in the "
            + "suite covers connection-end disposal (RemEx-kqje).");
    }

    [Theory]
    [InlineData("PingPongHandler")]
    [InlineData("RemoteDesktopHandler")]
    public void NoEndpointConstructsItsHandlerWithoutOne(string handler)
    {
        // The complement, and the one that catches the likelier regression. Asserting only that a
        // `using` form EXISTS would still pass if someone added a second, un-disposed construction
        // site alongside it.
        var source = BootstrapperSourceWithoutComments();

        var all = AnyConstruction(handler).Matches(source).Count;
        var disposed = DisposedConstruction(handler).Matches(source).Count;

        Assert.True(
            all == disposed,
            $"{all - disposed} of {all} {handler} constructions in HostBootstrapper.cs are not owned by a "
            + "`using`, so that connection would never release the keys its client left held (RemEx-kqje).");
    }

    [Fact]
    public void TheEndpointsAreStillConstructedHere()
    {
        // Guards the guard. This protects one file, so it must fail loudly rather than pass vacuously
        // if the endpoints move. EXACTLY one construction each: at ">= 1" a delegate could move to a
        // new file WITHOUT its `using` while some other construction stayed behind, and every test
        // above would still pass while protecting nothing.
        var source = BootstrapperSourceWithoutComments();

        foreach (var handler in DisposableHandlers)
        {
            Assert.True(
                AnyConstruction(handler).Matches(source).Count == 1,
                $"Expected exactly one {handler} construction in HostBootstrapper.cs. If it moved, this "
                + "scan now protects nothing and the new site needs the same guarantee; if one was added, "
                + "point this at both (RemEx-kqje).");
        }
    }
}
