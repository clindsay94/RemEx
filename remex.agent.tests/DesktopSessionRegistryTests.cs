using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop;

namespace Remex.Agent.Tests;

public class DesktopSessionRegistryTests
{
    private static DesktopSessionRegistry MakeRegistry() =>
        new(NullLogger<DesktopSessionRegistry>.Instance);

    // ── Test 1 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeOver_FirstConnection_ReturnsNonCancelledCts()
    {
        var registry = MakeRegistry();

        using var cts = await registry.TakeOverAsync("client-a", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(cts);
        Assert.False(cts.Token.IsCancellationRequested);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeOver_SameClientId_CancelsPriorCts()
    {
        var registry = MakeRegistry();

        // First session — never drained (simulates a stuck loop).
        // Use a separate CancellationTokenSource to observe the cancelled state
        // before TakeOverAsync disposes the prior CTS.
        var firstToken = default(System.Threading.CancellationToken);
        using var firstCts = await registry.TakeOverAsync("client-b", TimeSpan.FromMilliseconds(300), CancellationToken.None);
        firstToken = firstCts.Token; // capture token before disposal

        // Register a callback to record cancellation even after the CTS is disposed.
        var wasCancelled = false;
        firstToken.Register(() => wasCancelled = true);

        // Second session — should cancel the first within the drain timeout.
        using var secondCts = await registry.TakeOverAsync("client-b", TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(wasCancelled,
            "The first session's CTS must be cancelled when the second takes over.");
        Assert.False(secondCts.Token.IsCancellationRequested,
            "The second session's CTS must not be cancelled.");
    }

    // ── Test 3 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeOver_SameClientId_AwaitsMarkDrained()
    {
        var registry = MakeRegistry();

        // First session — will signal drain after 80ms
        using var firstCts = await registry.TakeOverAsync("client-c", TimeSpan.FromSeconds(2), CancellationToken.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            registry.MarkDrained("client-c", firstCts);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var secondCts = await registry.TakeOverAsync("client-c", TimeSpan.FromSeconds(2), CancellationToken.None);
        sw.Stop();

        // Should complete quickly (drain fires after 80ms, timeout is 2s)
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"TakeOver should complete within 500ms when drain fires at 80ms, but took {sw.ElapsedMilliseconds}ms.");
        Assert.False(secondCts.Token.IsCancellationRequested);
    }

    // ── Test 4 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeOver_DrainTimeout_ProceedsAnyway()
    {
        var registry = MakeRegistry();

        // First session — drain is NEVER signalled
        using var firstCts = await registry.TakeOverAsync("client-d", TimeSpan.FromMilliseconds(200), CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Should proceed after the 200ms drain timeout even though drain was never signalled.
        using var secondCts = await registry.TakeOverAsync("client-d", TimeSpan.FromMilliseconds(200), CancellationToken.None);
        sw.Stop();

        // 200ms timeout + generous margin
        Assert.True(sw.ElapsedMilliseconds < 800,
            $"TakeOver should complete within 800ms on drain timeout, but took {sw.ElapsedMilliseconds}ms.");
        Assert.False(secondCts.Token.IsCancellationRequested);
    }

    // ── Test 5 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeOver_EmptyClientId_DoesNotCancelEachOther()
    {
        var registry = MakeRegistry();

        // Two loopback (empty clientId) connections must not cancel each other.
        using var firstCts = await registry.TakeOverAsync(string.Empty, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        using var secondCts = await registry.TakeOverAsync(string.Empty, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(firstCts.Token.IsCancellationRequested,
            "First loopback session must not be cancelled by the second.");
        Assert.False(secondCts.Token.IsCancellationRequested,
            "Second loopback session must not be cancelled.");
    }

    // ── Test 6 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkDrained_AfterTakeover_NewSessionSeesNoPrior()
    {
        var registry = MakeRegistry();

        using var firstCts = await registry.TakeOverAsync("client-e", TimeSpan.FromMilliseconds(500), CancellationToken.None);
        registry.MarkDrained("client-e", firstCts);

        // The second take-over should not see any prior session to cancel.
        // If it did, it would try to drain an already-drained session — harmless, but
        // we verify by checking the second CTS comes back immediately (no wait).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var secondCts = await registry.TakeOverAsync("client-e", TimeSpan.FromMilliseconds(500), CancellationToken.None);
        sw.Stop();

        // Without a prior session to drain, this should be essentially instant.
        Assert.True(sw.ElapsedMilliseconds < 200,
            $"TakeOver with no prior session should be instant, but took {sw.ElapsedMilliseconds}ms.");
        Assert.False(secondCts.Token.IsCancellationRequested);

        registry.MarkDrained("client-e", secondCts);
    }
}
