using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;
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
    public async Task AConnectTimeoutIsReportedToTheClientAndNotOnlyThrown()
    {
        // THE DEFECT RemEx-nl0z FOUND, which is worse than the one it was filed for. The bead was
        // about the message being unlocalizable English; tracing where it surfaced showed it did not
        // surface AT ALL. StartDesktopStream runs through OrderedAsyncWorkQueue, whose failure handler
        // writes to logcat and nothing else, so throwing was the same as staying silent: the phone sat
        // on a stalled screen with no explanation and no way to tell a sleeping PC from a broken app.
        //
        // ErrorReceived is the only channel that reaches the UI, so the assertion is that the failure
        // arrives there. Asserting on the CODE rather than the sentence is the other half of the bead:
        // the text is composed in Remex.Core, which cannot reach Android string resources, so the code
        // is what the client translates and the English is only a fallback for an older client.
        var reported = new List<string>();
        void Capture(string text) => reported.Add(text);

        RemexDesktopClient.Current.ErrorReceived += Capture;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                RemexDesktopClient.Current.ConnectAsync(UnroutableHost, Port, spkiHash: SomeSpkiHash));
        }
        finally
        {
            RemexDesktopClient.Current.ErrorReceived -= Capture;
        }

        var report = Assert.Single(reported);
        var parts = report.Split(DesktopErrorCodes.Delimiter);
        Assert.Equal(3, parts.Length);
        Assert.Equal(DesktopErrorCodes.ConnectTimeout, parts[0]);
        Assert.Equal($"{UnroutableHost}:{Port}", parts[1]);
        Assert.False(string.IsNullOrWhiteSpace(parts[2]),
            "the English fallback must survive for a client too old to know the code");
    }

    [Fact]
    public async Task CancellingTheConnectReportsNothingToTheUser()
    {
        // The counterpart to CancellingTheConnectIsNotReportedAsAnUnreachableHost above, one layer
        // out. Telling the user their PC is unreachable because they pressed stop is the confusion
        // that catch exists to prevent, and it would come straight back if the report were raised
        // before the filter rather than inside it.
        var reported = new List<string>();
        void Capture(string text) => reported.Add(text);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        RemexDesktopClient.Current.ErrorReceived += Capture;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RemexDesktopClient.Current.ConnectAsync(
                    UnroutableHost, Port, spkiHash: SomeSpkiHash, ct: cts.Token));
        }
        finally
        {
            RemexDesktopClient.Current.ErrorReceived -= Capture;
        }

        Assert.Empty(reported);
    }

    [Fact]
    public void TheHandshakeTimeoutNamesTheHostAndSaysWhatIsDifferentAboutIt()
    {
        // WHAT THIS COVERS AND WHAT IT DOES NOT, stated rather than implied, because the honest
        // answer is "less than it looks like".
        //
        // COVERED: the wording of the handshake failure, and that it names the host. The message is
        // one half of a pair — the client renders the localized string keyed on the CODE, and falls
        // back to this English only when it is too old to know the code — so the fallback still has
        // to be a sentence a user can act on.
        //
        // NOT COVERED: that the catch around the proof exchange actually classifies its own deadline
        // as a host timeout rather than as the user cancelling. Reaching that code needs a live
        // wss:// endpoint whose certificate matches the pin, since the client hardcodes the scheme
        // and hashes the presented SPKI. Every test in this file points at an unroutable address and
        // so fails one step earlier, at connect. Deleting that catch leaves the whole suite green.
        // A hand-rolled TLS + RFC 6455 listener was written for this and abandoned: debugging the
        // upgrade handshake had become the work rather than the bead. Tracked as its own issue so a
        // reusable harness benefits the other RD paths that need one too, rather than being smuggled
        // in here. RemEx-u5q0, which also records how far the abandoned attempt got and where it stuck.
        var message = RemexDesktopClient.DescribeHandshakeTimeout("192.0.2.7", 5005);

        Assert.Contains("192.0.2.7:5005", message);
        Assert.NotEqual(
            $"The PC at 192.0.2.7:5005 did not answer within {ShortTimeout.TotalSeconds:F0} seconds. "
            + "It is most likely asleep or off this network.",
            message);
        Assert.DoesNotContain("network", message, StringComparison.OrdinalIgnoreCase);
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
