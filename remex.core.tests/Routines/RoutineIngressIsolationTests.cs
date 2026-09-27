using System.Runtime.CompilerServices;
using Remex.Core.Services.Command;

namespace Remex.Core.Tests.Routines;

/// <summary>
/// Routines spec §9 T15: routines add no ingress. <c>RemexNetworkListener</c> (TCP 8338) never handles a
/// routine type, and the script-ingress verb list is still the 11 it was before routines.
/// </summary>
public sealed class RoutineIngressIsolationTests
{
    [Fact]
    public void TheExternalListenerNeverMentionsRoutines()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.core", "Services", "Network", "RemexNetworkListener.cs"));

        Assert.DoesNotContain("routine", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptIngressIsStillTheElevenVerbs()
    {
        string[] expected =
        [
            "SHUTDOWN", "FORCESHUTDOWN", "RESTART", "FORCERESTART", "RESTARTTOUEFI",
            "SLEEP", "HIBERNATE", "SIGNOUT", "LOCK", "MONITOROFF", "WAKEONLAN",
        ];

        Assert.Equal(11, CommandVerbs.ScriptIngress.Count);
        Assert.Equal(
            expected.OrderBy(v => v, StringComparer.Ordinal),
            CommandVerbs.ScriptIngress.OrderBy(v => v, StringComparer.Ordinal));
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
