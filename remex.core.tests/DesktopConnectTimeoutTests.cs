using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that connecting to a PC which never answers fails promptly and says so, rather than hanging
/// (RemEx-g7hr).
/// </summary>
/// <remarks>
/// <para>
/// A sleeping or absent PC sends no RST, so the connect sits in TCP SYN retransmit — roughly two
/// minutes on Android before the OS gives up. For all of that time the UI has nothing to report and
/// the user sees a spinner. It matters more since desktop work moved onto one ordered consumer
/// (RemEx-krvz): that queue abandons any item over thirty seconds, so an unbounded connect was both
/// stalling everything behind it AND being abandoned mid-flight rather than failing cleanly.
/// </para>
/// <para>
/// THE DISTINCTION BETWEEN THE TWO CANCELLATION SOURCES IS THE SUBTLE PART, and the second test is
/// the one that pins it. The timeout is a linked token, so a naive implementation reports the
/// caller's own cancellation as a connection failure — the user taps stop and the app tells them
/// their PC is unreachable. The filter that separates them is invisible in the happy path and has no
/// other coverage.
/// </para>
/// <para>
/// <c>RemexDesktopClient</c> is a process singleton with a private constructor, so these run against
/// <c>Current</c> and share state — the same caveat <see cref="StoppedStreamDoesNotResurrectTests"/>
/// documents. Restoring the override in <c>Dispose</c> is NOT sufficient on its own, because that only
/// prevents a leak after the class finishes and xUnit runs classes in parallel; the assembly disables
/// test parallelism for exactly this reason (see AssemblyInfo.cs).
/// </para>
/// </remarks>
public class DesktopConnectTimeoutTests : IDisposable
{
    /// <summary>TEST-NET-1 (RFC 5737). Guaranteed not to route, so a connect gets no answer of any kind.</summary>
    private const string UnroutableHost = "192.0.2.1";
    private const int Port = 5005;

    /// <summary>Any non-empty value: the pin guard runs before the socket is touched.</summary>
    private const string SomeSpkiHash = "3q2+7wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    public DesktopConnectTimeoutTests() =>
        RemexDesktopClient.ConnectTimeoutOverrideForTests = ShortTimeout;

    public void Dispose() =>
        RemexDesktopClient.ConnectTimeoutOverrideForTests = null;

    [Fact]
    public async Task AHostThatNeverAnswersFailsWithATimeoutRatherThanHanging()
    {
        // THE BEAD. Unbounded, this sits in SYN retransmit for about two minutes. The assertion is
        // that it is bounded at all and reports what happened — the exact number is configuration.
        var elapsed = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            RemexDesktopClient.Current.ConnectAsync(UnroutableHost, Port, spkiHash: SomeSpkiHash));

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"the connect took {elapsed.Elapsed.TotalSeconds:F1}s against a {ShortTimeout.TotalSeconds:F1}s "
            + "budget, so it is not actually bounded by the timeout");
        Assert.Contains(UnroutableHost, ex.Message);
    }

    [Fact]
    public async Task CancellingTheConnectIsNotReportedAsAnUnreachableHost()
    {
        // THE DISCRIMINATING CASE. The deadline is a token linked to the caller's, so both arrive as
        // OperationCanceledException at the same catch. Without the filter that tells them apart, a
        // user who deliberately stops connecting is told their PC is asleep.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemexDesktopClient.Current.ConnectAsync(
                UnroutableHost, Port, spkiHash: SomeSpkiHash, ct: cts.Token));
    }

    [Fact]
    public async Task AFailedConnectDoesNotReportItselfAsConnected()
    {
        // Deliberately NOT a claim about cleanup. ClientWebSocket disposes itself on connect failure,
        // so this holds with or without any teardown of ours — which is exactly why production does
        // none. Asserting it anyway is worth one line: it is the property callers actually depend on.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            RemexDesktopClient.Current.ConnectAsync(UnroutableHost, Port, spkiHash: SomeSpkiHash));

        Assert.False(RemexDesktopClient.Current.IsConnected);
    }
}
