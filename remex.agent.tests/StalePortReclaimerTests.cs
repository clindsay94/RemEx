using Remex.Agent.Services.Network;

namespace Remex.Agent.Tests;

/// <summary>
/// Kill-safety contract of <see cref="StalePortReclaimer"/>: only a Remex.Agent process other than
/// the current one may ever be terminated (RemEx-uk38f.4). Everything is driven through the injected
/// seam, so no process is looked up or killed.
/// </summary>
public sealed class StalePortReclaimerTests
{
    private const int Port = 5005;

    // The first listener lookup reports the occupants; the post-kill wait loop then sees the port free.
    private static Func<int, IReadOnlyList<int>> Listeners(params int[] pids)
    {
        var calls = 0;
        return _ => calls++ == 0 ? pids : Array.Empty<int>();
    }

    private static int Reclaim(
        IReadOnlyDictionary<int, string?> names, int[] listeners, List<int> killed, out bool result,
        Action<int>? kill = null)
    {
        result = StalePortReclaimer.TryReclaim(
            Port,
            Listeners(listeners),
            pid => names.TryGetValue(pid, out var n) ? n : null,
            kill ?? killed.Add);
        return killed.Count;
    }

    [Fact]
    public void StaleRemexAgent_IsKilled_AndReclaimReportsTrue()
    {
        var killed = new List<int>();

        Reclaim(new Dictionary<int, string?> { [41000] = "Remex.Agent" }, new[] { 41000 }, killed, out var result);

        Assert.Equal(new[] { 41000 }, killed);
        Assert.True(result);
    }

    [Theory]
    [InlineData("remex.agent")]
    [InlineData("REMEX.AGENT")]
    public void ProcessNameMatch_IsCaseInsensitive(string name)
    {
        var killed = new List<int>();

        Reclaim(new Dictionary<int, string?> { [41000] = name }, new[] { 41000 }, killed, out _);

        Assert.Single(killed);
    }

    [Theory]
    [InlineData("svchost")]
    [InlineData("System")]
    [InlineData("nginx")]
    [InlineData("dotnet")]
    [InlineData("Remex")]
    [InlineData("Remex.Agen")]
    [InlineData("MyRemex.Agent")]
    [InlineData("")]
    public void OtherProcessNames_AreNeverKilled(string name)
    {
        var killed = new List<int>();

        Reclaim(new Dictionary<int, string?> { [41000] = name }, new[] { 41000 }, killed, out var result);

        Assert.Empty(killed);
        Assert.False(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(1)]
    public void SystemPidsWithSystemNames_AreNeverKilled(int pid)
    {
        var killed = new List<int>();
        var names = new Dictionary<int, string?> { [pid] = pid == 1 ? "systemd" : "System" };

        Reclaim(names, new[] { pid }, killed, out var result);

        Assert.Empty(killed);
        Assert.False(result);
    }

    [Fact]
    public void ProcessLookupFailure_IsTreatedAsNotRemex_AndNothingIsKilled()
    {
        var killed = new List<int>();

        // Absent from the map == getProcessName returned null (exited or access denied).
        Reclaim(new Dictionary<int, string?>(), new[] { 41000 }, killed, out var result);

        Assert.Empty(killed);
        Assert.False(result);
    }

    [Fact]
    public void CurrentProcess_IsNeverKilled_EvenWhenItsNameMatches()
    {
        var killed = new List<int>();
        var self = Environment.ProcessId;

        Reclaim(new Dictionary<int, string?> { [self] = "Remex.Agent" }, new[] { self }, killed, out var result);

        Assert.Empty(killed);
        Assert.False(result);
    }

    [Fact]
    public void MixedOccupants_OnlyTheStaleRemexAgentIsKilled()
    {
        var killed = new List<int>();
        var self = Environment.ProcessId;
        var names = new Dictionary<int, string?>
        {
            [41000] = "chrome",
            [41001] = "Remex.Agent",
            [41002] = null,
            [self] = "Remex.Agent",
        };

        Reclaim(names, new[] { 41000, self, 41001, 41002 }, killed, out var result);

        Assert.Equal(new[] { 41001 }, killed);
        Assert.True(result);
    }

    [Fact]
    public void KillFailure_IsSwallowed_ReportsFalse_AndDoesNotBlockTheNextCandidate()
    {
        var killed = new List<int>();
        var names = new Dictionary<int, string?> { [41000] = "Remex.Agent", [41001] = "Remex.Agent" };

        Reclaim(names, new[] { 41000, 41001 }, killed, out var result, kill: pid =>
        {
            if (pid == 41000) throw new UnauthorizedAccessException("denied");
            killed.Add(pid);
        });

        Assert.Equal(new[] { 41001 }, killed);
        Assert.True(result);
    }

    [Fact]
    public void KillFailureOnly_ReportsFalse_SoTheCallerFallsBackToAnotherPort()
    {
        var names = new Dictionary<int, string?> { [41000] = "Remex.Agent" };

        var result = StalePortReclaimer.TryReclaim(
            Port, Listeners(41000), pid => names[pid], _ => throw new InvalidOperationException("already gone"));

        Assert.False(result);
    }

    [Fact]
    public void ListenerLookupThrowing_IsContained_AndKillsNothing()
    {
        var killed = new List<int>();

        var result = StalePortReclaimer.TryReclaim(
            Port, _ => throw new IOException("ss missing"), _ => "Remex.Agent", killed.Add);

        Assert.False(result);
        Assert.Empty(killed);
    }

    [Fact]
    public void NameLookupThrowing_IsContained_AndKillsNothing()
    {
        var killed = new List<int>();

        var result = StalePortReclaimer.TryReclaim(
            Port, Listeners(41000), _ => throw new InvalidOperationException("boom"), killed.Add);

        Assert.False(result);
        Assert.Empty(killed);
    }

    [Fact]
    public void NoListeners_DoesNothing()
    {
        var killed = new List<int>();

        var result = StalePortReclaimer.TryReclaim(
            Port, _ => Array.Empty<int>(), _ => "Remex.Agent", killed.Add);

        Assert.False(result);
        Assert.Empty(killed);
    }

    [Fact]
    public void Kill_IsOnlyCalledForThePortsOwnListeners()
    {
        var queriedPorts = new List<int>();
        var killed = new List<int>();

        StalePortReclaimer.TryReclaim(
            Port,
            p => { queriedPorts.Add(p); return queriedPorts.Count == 1 ? new[] { 41000 } : Array.Empty<int>(); },
            _ => "Remex.Agent",
            killed.Add);

        Assert.All(queriedPorts, p => Assert.Equal(Port, p));
        Assert.Equal(new[] { 41000 }, killed);
    }
}
