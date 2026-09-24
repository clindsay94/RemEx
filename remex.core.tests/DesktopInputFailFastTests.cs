using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that input queued while the PC is unreachable shares ONE connect attempt instead of each item
/// paying a full connect timeout of its own (perf audit P0-14).
/// </summary>
/// <remarks>
/// <para>
/// Desktop work runs on one ordered consumer (RemEx-krvz). Before this, every queued input item ran
/// its own connect — 15 s plus a 10 s proof — serially, so a user who kept touching a dead stream
/// built a backlog that took minutes to drain and then replayed stale input when the PC came back.
/// </para>
/// <para>
/// These use an ISOLATED <see cref="RemexDesktopClient"/> (RemEx-u5q0) so the failure window they open
/// cannot leak into tests that share <c>Current</c>. The connect timeout and the window are static
/// seams, restored in <see cref="Dispose"/>; test parallelism is off assembly-wide for that reason.
/// </para>
/// </remarks>
public class DesktopInputFailFastTests : IDisposable
{
    /// <summary>TEST-NET-1 (RFC 5737). Guaranteed not to route, so a connect gets no answer of any kind.</summary>
    private const string UnroutableHost = "192.0.2.1";
    private const int Port = 5005;
    private const string SomeSpkiHash = "3q2+7wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static readonly TimeSpan ShortConnectTimeout = TimeSpan.FromMilliseconds(400);

    public DesktopInputFailFastTests() =>
        RemexDesktopClient.ConnectTimeoutOverrideForTests = ShortConnectTimeout;

    public void Dispose()
    {
        RemexDesktopClient.ConnectTimeoutOverrideForTests = null;
        RemexDesktopClient.InputReconnectBackoffOverrideForTests = null;
    }

    private static InputEvent Click(int n) =>
        new() { EventType = InputEventTypes.MouseClick, Button = 0, X = n, Y = n };

    [Fact]
    public async Task AQueuedBacklogBehindAFailedConnectDrainsOnOneAttempt()
    {
        // THE ROW. Twenty clicks queued against an unreachable PC. Before: twenty serial connects
        // (twenty failures, twenty timeouts' worth of wall clock). After: the first item's connect
        // fails, and the nineteen behind it return at once because that failure speaks for them.
        const int count = 20;
        var client = new RemexDesktopClient();
        var failures = new ConcurrentQueue<string>();
        var queue = new OrderedAsyncWorkQueue((label, _) => failures.Enqueue(label));
        var drained = new TaskCompletionSource();

        var elapsed = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            int n = i;
            queue.Enqueue($"input-{n}", () => client.SendInputAsync(UnroutableHost, Port, Click(n), spkiHash: SomeSpkiHash));
        }

        queue.Enqueue("sentinel", () => { drained.SetResult(); return Task.CompletedTask; });

        await drained.Task.WaitAsync(TimeSpan.FromSeconds(20));
        elapsed.Stop();

        Assert.Equal(new[] { "input-0" }, failures.ToArray());
        Assert.True(
            elapsed.Elapsed < ShortConnectTimeout * 5,
            $"the backlog took {elapsed.Elapsed.TotalMilliseconds:F0} ms; one connect is ~{ShortConnectTimeout.TotalMilliseconds:F0} ms, "
            + "so items behind the failed attempt are still connecting one by one");
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task PointerBatchesAndKeyframeRequestsShareTheFailureToo()
    {
        var client = new RemexDesktopClient();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.SendInputAsync(UnroutableHost, Port, Click(1), spkiHash: SomeSpkiHash));

        var elapsed = Stopwatch.StartNew();
        await client.SendPointerBatchAsync(UnroutableHost, Port, new DesktopPointerBatch { Samples = [] }, spkiHash: SomeSpkiHash);
        await client.RequestKeyframeAsync(UnroutableHost, Port, spkiHash: SomeSpkiHash);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < ShortConnectTimeout,
            $"took {elapsed.Elapsed.TotalMilliseconds:F0} ms, so at least one of them ran its own connect");
    }

    [Fact]
    public async Task OnceTheWindowClosesInputTriesTheConnectAgain()
    {
        // The window is a backoff, not a verdict: a PC that wakes up must be reachable again without
        // the user doing anything beyond carrying on.
        RemexDesktopClient.InputReconnectBackoffOverrideForTests = TimeSpan.FromMilliseconds(150);
        var client = new RemexDesktopClient();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.SendInputAsync(UnroutableHost, Port, Click(1), spkiHash: SomeSpkiHash));

        await Task.Delay(300);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.SendInputAsync(UnroutableHost, Port, Click(2), spkiHash: SomeSpkiHash));
    }

    [Fact]
    public async Task AnExplicitStreamStartIsNeverGatedByTheWindow()
    {
        // StartStreamAsync is how the user — and the client's reconnect logic — asks to try again.
        // Gating it would turn one failed connect into ten seconds where "retry" does nothing.
        var client = new RemexDesktopClient();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.SendInputAsync(UnroutableHost, Port, Click(1), spkiHash: SomeSpkiHash));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.StartStreamAsync(UnroutableHost, Port, new DesktopConfig(), spkiHash: SomeSpkiHash));
    }
}
