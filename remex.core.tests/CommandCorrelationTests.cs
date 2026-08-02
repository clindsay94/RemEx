using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins which pending command a <c>command_response</c> resolves (RemEx-s8h8).
/// </summary>
/// <remarks>
/// The replaced fallback resolved the FIRST pending command whenever the host echoed no correlation
/// id, and its own comment admitted it was "best-effort and incorrect under concurrency". Two
/// commands in flight at once is ordinary — a widget action landing while a task-manager kill runs —
/// and their answers could be swapped, so a kill would report the widget's result.
///
/// A wrong answer delivered confidently is worse than no answer, because the caller acts on it.
/// </remarks>
public class CommandCorrelationTests
{
    [Fact]
    public void ACorrelatedResponseResolvesExactlyThatCommand()
    {
        // The happy path, and the one every current host produces: all four send sites in
        // PingPongHandler echo the id the client sent.
        var target = RemexNativeClient.ResolveCommandTarget("cmd-2", ["cmd-1", "cmd-2", "cmd-3"]);

        Assert.Equal("cmd-2", target);
    }

    [Fact]
    public void ACorrelatedResponseIsTrustedEvenIfNothingIsPending()
    {
        // The caller re-checks the pending table anyway; returning the id keeps this function about
        // ATTRIBUTION rather than about liveness, which is the caller's concern.
        Assert.Equal("cmd-1", RemexNativeClient.ResolveCommandTarget("cmd-1", []));
    }

    [Fact]
    public void AnUncorrelatedResponseResolvesTheOnlyPendingCommand()
    {
        // DELIBERATELY KEPT. There is nothing to confuse it with, and it is what an older host that
        // never echoed the id produces in the ordinary sequential case. Dropping it would break
        // those hosts for no gain - the fix removes the AMBIGUOUS case, not the unambiguous one.
        Assert.Equal("only", RemexNativeClient.ResolveCommandTarget(null, ["only"]));
    }

    [Fact]
    public void AnUncorrelatedResponseResolvesNOTHINGWhenTwoAreInFlight()
    {
        // THE BUG. The old fallback picked the first pending entry, so two concurrent commands could
        // have their answers swapped. There is no way to attribute this response, and guessing is
        // how a kill command reports a widget action's result.
        Assert.Null(RemexNativeClient.ResolveCommandTarget(null, ["cmd-1", "cmd-2"]));
    }

    [Fact]
    public void AnUnattributableResponseDoesNotFailTheOtherCommands()
    {
        // The other direction of guess. Failing every pending command would invent a failure for
        // ones that may still receive a correctly correlated answer - so this returns null and the
        // existing per-command timeout reports honestly that nothing came back.
        //
        // Expressed as "returns no target", because returning any id at all IS the mis-resolution.
        foreach (var pending in new[] { new[] { "a", "b" }, new[] { "a", "b", "c" }, new[] { "a", "b", "c", "d" } })
        {
            Assert.Null(RemexNativeClient.ResolveCommandTarget(null, pending));
        }
    }

    [Fact]
    public void AnUncorrelatedResponseWithNothingPendingResolvesNothing()
    {
        Assert.Null(RemexNativeClient.ResolveCommandTarget(null, []));
    }

    [Fact]
    public void TheOnlyUncorrelatedCaseThatResolvesIsExactlyOnePending()
    {
        // Swept, because the rule is a count boundary and a boundary is where an off-by-one lives.
        // One resolves; zero and two or more do not.
        for (var count = 0; count <= 5; count++)
        {
            var pending = Enumerable.Range(1, count).Select(i => $"cmd-{i}").ToList();
            var target = RemexNativeClient.ResolveCommandTarget(null, pending);

            if (count == 1) Assert.Equal("cmd-1", target);
            else Assert.True(target is null, $"{count} pending must not resolve, got {target}");
        }
    }
}
