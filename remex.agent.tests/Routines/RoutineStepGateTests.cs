using System.Runtime.CompilerServices;
using Remex.Agent.Handlers;
using Remex.Core.Messages;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>RoutinesSyncGateTests</c>, the S1b half: <c>routine_step_request</c> and
/// <c>routine_cancel</c> are pairing-gated, and the handler acts only for a PROVEN client identity.
/// </summary>
public sealed class RoutineStepGateTests
{
    [Theory]
    [InlineData(MessageTypes.RoutineStepRequest)]
    [InlineData(MessageTypes.RoutineCancel)]
    public void RoutineMessagesRequirePairing(string type)
    {
        Assert.True(PingPongHandler.RequiresPairing(type));
        Assert.False(PingPongHandler.RequiresLoopback(type));
    }

    [Fact]
    public void TheDispatchIsDetachedAndScopedToAProvenIdentity()
    {
        // A source pin, because the two properties that matter here - the countdown must not park the
        // reader that would receive its cancel, and loopback (no proven identity) must never act as a
        // phone - are both invisible to a unit test of the handler class itself.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Handlers", "PingPongHandler.cs"));
        var start = source.IndexOf("case MessageTypes.RoutineStepRequest:", StringComparison.Ordinal);
        var end = source.IndexOf("// ── 2.5 Clipboard ──", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the routine cases moved; update this pin");
        var block = source[start..end];

        Assert.Contains("RunDetachedAsync(", block, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(block, "!identityProven"));
        Assert.Contains("case MessageTypes.RoutineCancel:", block, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
